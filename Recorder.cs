using System.Diagnostics;
using System.Text.Json.Nodes;

namespace PainelDeck;

/// <summary>
/// Modo gravação: manda o OBS gravar (obs-websocket v5) e acompanha a gravação pra tela de gravação da Navy.
///  - Toggle/Start/Stop/Pause pelo atalho global "record" (settings.json → hotkeys), pela ação { "type": "record" }
///    ou por Bridge.call("record", { cmd }).
///  - Níveis de áudio ao vivo das duas faixas (microfone e som do sistema) pelo evento InputVolumeMeters do OBS.
///  - Guarda o histórico dos níveis (4 pontos por segundo) pra linha do tempo sobreviver a um recarregar do painel.
/// Config em "gravacao" no deck.json. Só fala com o OBS se ele estiver aberto (não fica tentando conectar à toa).
/// </summary>
internal sealed class Recorder : IDisposable
{
    private const int SubScenes = 1 << 2, SubInputs = 1 << 3, SubOutputs = 1 << 6, SubMeters = 1 << 16;
    public const int BucketMs = 250;          // resolução da linha do tempo
    private const int MaxBuckets = 12 * 3600 * 4; // 12 h
    private const double FloorDb = -60;

    private sealed class Track
    {
        public string Name = "", Source = "";
        public bool Muted, Found;
        public int[] Mixer = Array.Empty<int>();    // faixas do arquivo em que essa fonte entra (1..6)
        public double Peak = -100, Mag = -100;      // maior valor desde a última leitura do painel
        public readonly List<byte> History = new();
    }

    private sealed record Cfg(string Scene, string ObsPath, (string Name, string Source)[] Tracks, int Replay, int[] ReplayButtons, int ReplayKey, bool ReplayInGame, string Ffmpeg);

    private readonly ObsClient obs;
    private readonly Deck deck;
    private readonly string hotkey, replayHotkey, root;
    private readonly List<object> clips = new();   // replays salvos desde que o app abriu (o mais novo primeiro)
    private int replayTrim;                        // corte pedido pro próximo replay salvo (segundos; 0 = o buffer inteiro)
    private bool playing, replayAuto;              // jogo aberto / o replay foi ligado sozinho por causa do jogo
    private DateTime replayWhenReadyUntil;         // abriu o OBS pra ligar o replay: liga assim que ele responder
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim wake = new(0, 1);

    private Track[] tracks = { new() { Name = "Microfone" }, new() { Name = "Sistema" } };
    private string cfgKey = "";
    private string state = "closed";   // closed (OBS fechado) | connecting | ok | error
    private string? error;             // por que não está conectado (aparece no cartão)
    private string? fail;              // último comando que deu errado (o painel mostra num aviso quando failSeq muda)
    private int failSeq;
    private bool failOk;               // o aviso é uma boa notícia ("Replay salvo"), não um erro
    private string? micDevice;         // nome do dispositivo que a fonte do microfone usa no OBS
    private bool replayActive;         // buffer de repetição do OBS ligado (guarda os últimos segundos na memória)
    private int replaySeconds;
    private string? replayLast;
    private bool active, paused, starting;
    private long baseMs, bytes;        // duração na última leitura do OBS
    private readonly Stopwatch since = new();
    private string? path, scene, dir;
    private readonly List<long> markers = new();
    private JsonObject? stats, last;
    private List<object> recent = new();
    private double? freeGb;
    private DateTime meterUntil, startWhenReadyUntil, nextConnect, nextSlow, startedAt;
    private bool micHeard, micWarned;  // chegou algum som do microfone nesta gravação / já avisei que não chegou
    private bool needRefresh = true;
    private int step;

    public Recorder(Settings settings, Deck deck)
    {
        this.deck = deck;
        hotkey = settings.Hotkeys.TryGetValue("record", out var hk) ? hk : "";
        replayHotkey = settings.Hotkeys.TryGetValue("replay", out var rhk) ? rhk : "";
        root = settings.Root;
        obs = new ObsClient(settings.ObsUrl, settings.ObsPassword, SubScenes | SubInputs | SubOutputs | SubMeters);
        obs.Event += OnEvent;
        try { if (File.Exists(DirFile)) dir = File.ReadAllText(DirFile).Trim(); } catch { }
        _ = Task.Run(Loop);
    }

    // ---------- comandos ----------

    /// <summary>cmd: toggle | start | stop | pause | mark | mute (track) | test | open (path) | history | frame (width) | status
    /// | devices | device (id) | replay | replay-off.</summary>
    public async Task<object?> CommandAsync(string cmd, JsonNode? args = null)
    {
        if (cmd == "frame") return await FrameAsync(args?["width"]?.GetValue<int>() ?? 640); // pedido várias vezes por segundo: erro não vira aviso
        try
        {
            switch (cmd)
            {
                case "toggle": if (IsActive) await StopAsync(); else await StartAsync(); break;
                case "start": if (!IsActive) await StartAsync(); break;
                case "stop": if (IsActive) await StopAsync(); break;
                case "pause":
                    if (IsActive) { await obs.RequestAsync("ToggleRecordPause", null); Wake(); }
                    break;
                case "mark": await MarkAsync(); break;
                case "mute":
                    Track? t;
                    lock (gate) t = tracks.ElementAtOrDefault(args?["track"]?.GetValue<int>() ?? -1);
                    if (t is { Found: true }) await obs.RequestAsync("ToggleInputMute", new JsonObject { ["inputName"] = t.Source });
                    break;
                case "test": // níveis ao vivo por 20 s sem gravar (pra conferir o áudio antes)
                    await EnsureObsAsync(launch: false);
                    lock (gate) meterUntil = DateTime.UtcNow.AddSeconds(20);
                    break;
                case "open": OpenInExplorer(args?["path"]?.GetValue<string>()); break;
                case "devices": return await DevicesAsync();
                case "device": await SetDeviceAsync(args?["id"]?.GetValue<string>() ?? ""); break;
                case "replay": await ReplayAsync(args?["seconds"] is JsonValue sv && sv.TryGetValue<double>(out var sec) ? (int)sec : -1); break;
                case "replay-on": await ReplayOnAsync(); break;
                case "replay-off":
                    if (obs.IsConnected) { try { await obs.RequestAsync("StopReplayBuffer", null); } catch { } }
                    lock (gate) { replayActive = false; replayAuto = false; replayWhenReadyUntil = default; }
                    break;
                case "history": return History();
                case "status": break;
                default: throw new ArgumentException($"Comando de gravação desconhecido: '{cmd}'");
            }
        }
        catch (Exception ex)
        {
            lock (gate) { starting = false; }
            Fail(ex.Message);
            throw;
        }
        return Status();
    }

    /// <summary>Pro atalho global: roda o comando sem ninguém esperando (o erro aparece no painel pelo status).</summary>
    public void Fire(string cmd) => _ = Task.Run(async () => { try { await CommandAsync(cmd); } catch { } });

    public bool IsRecording { get { lock (gate) return active; } }
    /// <summary>Prévia do vídeo: um JPEG pequeno da cena que o OBS está gravando (data URL), pra tela de gravação.</summary>
    private async Task<object> FrameAsync(int width)
    {
        try
        {
            if (!obs.IsConnected) return new { image = (string?)null, error = (string?)"OBS fechado" };
            string? src;
            lock (gate) src = scene;
            src ??= (string?)(await obs.RequestAsync("GetCurrentProgramScene", null))?["currentProgramSceneName"];
            var r = await obs.RequestAsync("GetSourceScreenshot", new JsonObject
            {
                ["sourceName"] = src, ["imageFormat"] = "jpg",
                ["imageWidth"] = Math.Clamp(width, 160, 1280), ["imageCompressionQuality"] = 70,
            });
            return new { image = (string?)r?["imageData"], error = (string?)null };
        }
        catch (Exception ex) { return new { image = (string?)null, error = (string?)ex.Message }; }
    }

    private bool IsActive { get { lock (gate) return active || starting; } }

    /// <summary>Cena no ar, pasta de gravação válida e áudio na faixa 1: o que vale tanto pra gravar quanto pro replay.</summary>
    private async Task<bool> PrepareAsync(List<string> notes)
    {
        var cfg = ReadCfg();
        if (cfg.Scene.Equals("auto", StringComparison.OrdinalIgnoreCase)) await EnsureAutoSceneAsync();
        else if (cfg.Scene != "") await obs.RequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = cfg.Scene });
        await RefreshAsync();
        if (!await EnsureRecordDirAsync(notes)) return false;
        await PrepareAudioAsync(notes);
        return true;
    }

    // ---------- microfone: qual dispositivo ----------

    /// <summary>A fonte do microfone no OBS; se não existir nenhuma, cria uma na cena que está no ar.</summary>
    private async Task<string> MicSourceAsync()
    {
        await EnsureObsAsync(launch: false);
        await RefreshAsync();
        Track? t;
        lock (gate) t = tracks.FirstOrDefault();
        if (t is { Found: true, Source.Length: > 0 }) return t.Source;
        var sceneNow = (string?)(await obs.RequestAsync("GetCurrentProgramScene", null))?["currentProgramSceneName"]
            ?? throw new InvalidOperationException("O OBS não tem nenhuma cena");
        await obs.RequestAsync("CreateInput", new JsonObject
        {
            ["sceneName"] = sceneNow, ["inputName"] = AudioKinds[0].Name, ["inputKind"] = AudioKinds[0].Kind,
            ["inputSettings"] = new JsonObject { ["device_id"] = "default" }, ["sceneItemEnabled"] = true,
        });
        await RefreshAsync();
        return AudioKinds[0].Name;
    }

    /// <summary>{ source, current, items: [{ id, name }] }: os microfones que o OBS enxerga e qual está em uso.</summary>
    private async Task<object> DevicesAsync()
    {
        var source = await MicSourceAsync();
        var current = (string?)(await obs.RequestAsync("GetInputSettings", new JsonObject { ["inputName"] = source }))?["inputSettings"]?["device_id"] ?? "default";
        var items = (await obs.RequestAsync("GetInputPropertiesListPropertyItems",
            new JsonObject { ["inputName"] = source, ["propertyName"] = "device_id" }))?["propertyItems"] as JsonArray;
        var list = (items ?? new JsonArray())
            .Where(i => (string?)i?["itemValue"] is { Length: > 0 } && i?["itemEnabled"]?.GetValue<bool>() != false)
            .Select(i => (object)new { id = (string)i!["itemValue"]!, name = (string?)i["itemName"] ?? (string)i["itemValue"]! })
            .ToList();
        return new { source, current, items = list };
    }

    private async Task SetDeviceAsync(string id)
    {
        if (id == "") throw new ArgumentException("Faltou dizer qual microfone");
        var source = await MicSourceAsync();
        await obs.RequestAsync("SetInputSettings", new JsonObject
        {
            ["inputName"] = source, ["inputSettings"] = new JsonObject { ["device_id"] = id }, ["overlay"] = true,
        });
        var name = await DeviceNameAsync(source);
        lock (gate) { micDevice = name; micHeard = false; micWarned = false; meterUntil = DateTime.UtcNow.AddSeconds(20); } // mostra o nível por 20 s pra conferir
    }

    // ---------- replay: salva os últimos segundos ----------

    /// <summary>
    /// Buffer de repetição do OBS: ele guarda os últimos "gravacao.replay" segundos na memória. Desligado: liga.
    /// Ligado: salva num arquivo na pasta das gravações e, se o pedido foi menor que o buffer (30 s de 5 min),
    /// corta o arquivo com o ffmpeg (sem recodificar). seconds: -1 = o corte do atalho, 0 = o buffer inteiro.
    /// </summary>
    private async Task ReplayAsync(int seconds)
    {
        if (!await ReplayOnAsync(announce: true)) return; // estava desligado: ligou agora (ainda não tem o que salvar)
        var cfg = ReadCfg();
        lock (gate) replayTrim = seconds < 0 ? cfg.ReplayKey : seconds;
        await obs.RequestAsync("SaveReplayBuffer", null); // o arquivo chega pelo evento ReplayBufferSaved
    }

    /// <summary>Garante o buffer ligado. true = já estava ligado; false = ligou agora (ou abriu o OBS pra ligar).</summary>
    private async Task<bool> ReplayOnAsync(bool announce = true)
    {
        if (!obs.IsConnected && !ObsRunning())
        {
            ObsLocalConfig.EnableServer();
            LaunchObs();
            lock (gate) { state = "connecting"; nextConnect = DateTime.UtcNow.AddSeconds(3); replayWhenReadyUntil = DateTime.UtcNow.AddSeconds(90); }
            if (announce) Note("Abrindo o OBS pra ligar o replay…");
            return false;
        }
        await EnsureObsAsync(launch: false);
        bool on;
        try { on = (await obs.RequestAsync("GetReplayBufferStatus", null))?["outputActive"]?.GetValue<bool>() == true; }
        catch
        {
            // o OBS só cria o buffer se ele estiver ligado nas configurações quando o OBS abre
            foreach (var cat in new[] { "SimpleOutput", "AdvOut" })
                try { await obs.RequestAsync("SetProfileParameter", new JsonObject { ["parameterCategory"] = cat, ["parameterName"] = "RecRB", ["parameterValue"] = "true" }); } catch { }
            throw new InvalidOperationException("O Buffer de Repetição estava desligado no OBS. Liguei nas configurações dele: feche e abra o OBS uma vez.");
        }
        if (on) { lock (gate) replayActive = true; return true; }
        var cfg = ReadCfg();
        var notes = new List<string>();
        if (!await PrepareAsync(notes)) return false;
        foreach (var cat in new[] { "SimpleOutput", "AdvOut" }) // a duração é lida pelo OBS na hora de ligar o buffer
            try { await obs.RequestAsync("SetProfileParameter", new JsonObject { ["parameterCategory"] = cat, ["parameterName"] = "RecRBTime", ["parameterValue"] = cfg.Replay.ToString() }); } catch { }
        await obs.RequestAsync("StartReplayBuffer", null);
        lock (gate) { replayActive = true; replaySeconds = cfg.Replay; }
        notes.Insert(0, $"Replay ligado: guardando os últimos {Span(cfg.Replay)}.");
        if (announce || notes.Count > 1) Note(string.Join(" ", notes));
        return false;
    }

    /// <summary>
    /// Chamado 1x por segundo com "tem jogo aberto?". Com "replayNoJogo" (padrão ligado), abrir um jogo liga o replay
    /// sozinho e fechar o jogo desliga (só se foi o painel que ligou).
    /// </summary>
    public void SetGame(bool on)
    {
        bool start, stopIt;
        lock (gate)
        {
            if (on == playing) return;
            playing = on;
            start = on && !replayActive;
            stopIt = !on && replayAuto;
            if (start) replayAuto = true;
        }
        if (start && !ReadCfg().ReplayInGame) { lock (gate) replayAuto = false; return; }
        if (start) _ = Task.Run(async () => { try { await ReplayOnAsync(announce: true); } catch (Exception ex) { lock (gate) replayAuto = false; Fail(ex.Message); } });
        if (stopIt) Fire("replay-off");
    }

    /// <summary>O ffmpeg.exe pra cortar o replay: "gravacao.ffmpeg", a pasta tools\ do painel (scripts\instalar-ffmpeg.ps1) ou o PATH.</summary>
    private string? FindFfmpeg()
    {
        var c = ReadCfg().Ffmpeg;
        if (c != "" && File.Exists(c)) return c;
        var local = Path.Combine(root, "tools", "ffmpeg.exe");
        if (File.Exists(local)) return local;
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            try { var f = Path.Combine(d.Trim(), "ffmpeg.exe"); if (d.Trim() != "" && File.Exists(f)) return f; } catch { }
        return null;
    }

    /// <summary>O OBS salvou o buffer inteiro: corta os últimos "trim" segundos (cópia direta, sem recodificar) e fica só com o corte.</summary>
    private async Task ReplaySavedAsync(string? saved, int trim, int buffer)
    {
        string? final = saved; int secs = buffer; string? extra = null;
        if (!string.IsNullOrEmpty(saved) && trim > 0 && trim < buffer)
        {
            var ff = FindFfmpeg();
            if (ff == null) extra = $"veio com os {Span(buffer)} inteiros (pra cortar, rode scripts\\instalar-ffmpeg.ps1 uma vez)";
            else
                try
                {
                    for (int i = 0; i < 20 && !File.Exists(saved); i++) await Task.Delay(100);
                    var outPath = Path.Combine(Path.GetDirectoryName(saved)!, $"{Path.GetFileNameWithoutExtension(saved)} ({Span(trim).Replace(" ", "")}){Path.GetExtension(saved)}");
                    var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true }; // sem janela: não rouba o foco do jogo
                    foreach (var a in new[] { "-y", "-v", "error", "-sseof", $"-{trim}", "-i", saved, "-map", "0", "-c", "copy", outPath }) psi.ArgumentList.Add(a);
                    using var p = Process.Start(psi)!;
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                    await p.WaitForExitAsync(cts.Token);
                    if (p.ExitCode == 0 && File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                    {
                        try { File.Delete(saved); } catch { } // o arquivo inteiro que o próprio painel acabou de pedir
                        final = outPath; secs = trim;
                    }
                    else extra = "não consegui cortar, ficou o arquivo inteiro";
                }
                catch { extra = "não consegui cortar, ficou o arquivo inteiro"; }
        }
        long size = 0;
        try { if (final != null && File.Exists(final)) size = new FileInfo(final).Length; } catch { }
        lock (gate)
        {
            replayLast = final; nextSlow = default;
            if (final != null)
            {
                clips.Insert(0, new { name = Path.GetFileName(final), path = final, seconds = secs, bytes = size, at = DateTimeOffset.Now.ToString("o") });
                if (clips.Count > 12) clips.RemoveAt(clips.Count - 1);
            }
        }
        var msg = $"Replay salvo: últimos {Span(secs)}" + (extra != null ? $" ({extra})" : "");
        if (extra != null) Fail(msg); else Note(msg);
        Wake();
    }

    private static string Span(int seconds) => seconds % 60 == 0 ? $"{seconds / 60} min" : seconds > 60 ? $"{seconds / 60} min {seconds % 60} s" : $"{seconds} s";

    private async Task StartAsync()
    {
        if (!await EnsureObsAsync(launch: true)) return; // o OBS está abrindo: grava assim que ele responder
        lock (gate) { starting = true; path = null; }
        var notes = new List<string>();
        if (!await PrepareAsync(notes)) { lock (gate) starting = false; return; }
        lock (gate) { startedAt = DateTime.UtcNow; micHeard = false; micWarned = false; }
        await obs.RequestAsync("StartRecord", null);
        if (notes.Count > 0) Fail(string.Join(" ", notes));
        Wake();
    }

    /// <summary>
    /// O OBS abre um aviso ("Caminho de arquivo inválido") e não grava quando a pasta de gravação dele está vazia ou
    /// não existe mais. Antes de gravar: cria a pasta se der; se não der (disco que sumiu), passa a gravar em Vídeos\OBS.
    /// </summary>
    private async Task<bool> EnsureRecordDirAsync(List<string> notes)
    {
        string? d = null;
        try { d = (string?)(await obs.RequestAsync("GetRecordDirectory", null))?["recordDirectory"]; }
        catch { return true; } // OBS antigo sem esse pedido: deixa ele decidir
        d = d?.Trim();
        if (!string.IsNullOrEmpty(d))
        {
            if (Directory.Exists(d)) return true;
            try { if (Path.IsPathRooted(d)) { Directory.CreateDirectory(d); return true; } } catch { /* disco/pasta inacessível */ }
        }
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos)) videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
        var fallback = Path.Combine(videos, "OBS");
        try
        {
            Directory.CreateDirectory(fallback);
            await obs.RequestAsync("SetRecordDirectory", new JsonObject { ["recordDirectory"] = fallback });
        }
        catch
        {
            Fail("A pasta de gravação do OBS " + (string.IsNullOrEmpty(d) ? "está vazia" : $"não existe ({d})")
               + ". Escolha uma pasta em OBS → Configurações → Saída → Gravação.");
            return false;
        }
        lock (gate)
        {
            dir = fallback; nextSlow = default;
            try { Directory.CreateDirectory(Path.GetDirectoryName(DirFile)!); File.WriteAllText(DirFile, fallback); } catch { }
        }
        notes.Add((string.IsNullOrEmpty(d) ? "O OBS estava sem pasta de gravação" : $"A pasta de gravação do OBS não existe ({d})") + $": passei a gravar em {fallback}.");
        return true;
    }

    /// <summary>
    /// Microfone e som do sistema têm que estar na faixa 1 do arquivo (a única que o modo simples do OBS grava e a que
    /// os players tocam). Avisa se o microfone está mudo no OBS.
    /// </summary>
    private async Task PrepareAudioAsync(List<string> notes)
    {
        Track[] ts;
        lock (gate) ts = tracks.Take(2).ToArray();
        bool changed = false;
        for (int i = 0; i < ts.Length; i++)
        {
            var t = ts[i];
            if (!t.Found || t.Source == "")
            {
                if (i == 0) notes.Add("Não achei nenhum microfone no OBS: confira OBS → Configurações → Áudio → Mic/Aux.");
                continue;
            }
            if (!t.Mixer.Contains(1))
                try
                {
                    await obs.RequestAsync("SetInputAudioTracks", new JsonObject { ["inputName"] = t.Source, ["inputAudioTracks"] = new JsonObject { ["1"] = true } });
                    changed = true;
                }
                catch { }
            if (i == 0 && t.Muted) notes.Add($"O microfone ({t.Source}) está mudo no OBS: toque nele na tela de gravação pra ligar.");
        }
        if (changed) await RefreshAsync();
    }

    /// <summary>Nome do dispositivo que uma fonte de captura de áudio usa (pra dizer qual trocar quando não chega som).</summary>
    private async Task<string> DeviceNameAsync(string source)
    {
        try
        {
            var id = (string?)(await obs.RequestAsync("GetInputSettings", new JsonObject { ["inputName"] = source }))?["inputSettings"]?["device_id"] ?? "default";
            var items = (await obs.RequestAsync("GetInputPropertiesListPropertyItems",
                new JsonObject { ["inputName"] = source, ["propertyName"] = "device_id" }))?["propertyItems"] as JsonArray;
            var name = (string?)items?.FirstOrDefault(i => (string?)i?["itemValue"] == id)?["itemName"];
            return name ?? (id == "default" ? "padrão do Windows" : id);
        }
        catch { return ""; }
    }

    private async Task StopAsync()
    {
        lock (gate) starting = false;
        var r = await obs.RequestAsync("StopRecord", null);
        if (r?["outputPath"]?.GetValue<string>() is { Length: > 0 } p) lock (gate) path = p;
        Wake();
    }

    private async Task MarkAsync()
    {
        long ms; int n;
        lock (gate)
        {
            if (!active) return;
            ms = NowMs();
            markers.Add(ms);
            n = markers.Count;
        }
        // OBS 30.2+ com MP4/MOV híbrido grava o capítulo no arquivo; nos outros formatos só o painel mostra
        try { await obs.RequestAsync("CreateRecordChapter", new JsonObject { ["chapterName"] = $"Marca {n}" }); } catch { }
    }

    /// <summary>true = conectado; false = o OBS estava fechado e foi aberto (a gravação começa quando ele responder).</summary>
    private async Task<bool> EnsureObsAsync(bool launch)
    {
        if (obs.IsConnected) return true;
        if (!ObsRunning())
        {
            if (!launch) throw new InvalidOperationException("O OBS está fechado");
            ObsLocalConfig.EnableServer(); // OBS fechado: liga o servidor WebSocket dele antes de abrir, se estiver desligado
            LaunchObs();
            lock (gate) { startWhenReadyUntil = DateTime.UtcNow.AddSeconds(90); starting = true; state = "connecting"; nextConnect = DateTime.UtcNow.AddSeconds(3); }
            return false;
        }
        try { await obs.RequestAsync("GetVersion", null); }
        catch (Exception ex) { throw new InvalidOperationException(ObsLocalConfig.Explain(ex.Message)); }
        lock (gate) { state = "ok"; needRefresh = true; }
        return true;
    }

    private void LaunchObs()
    {
        var cfg = ReadCfg();
        var exe = cfg.ObsPath != "" ? cfg.ObsPath : new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio", "bin", "64bit", "obs64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "obs-studio", "bin", "64bit", "obs64.exe"),
        }.FirstOrDefault(File.Exists) ?? "";
        if (!File.Exists(exe))
            throw new FileNotFoundException("Não achei o OBS. Abra ele na mão ou coloque o caminho em \"gravacao\": { \"obs\": \"C:\\\\...\\\\obs64.exe\" } no deck.json");
        // o OBS só abre direito com a pasta dele como pasta de trabalho
        Process.Start(new ProcessStartInfo(exe, "--minimize-to-tray --disable-shutdown-check")
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Minimized,
        })?.Dispose();
    }

    private static bool ObsRunning()
    {
        var ps = Process.GetProcessesByName("obs64");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    /// <summary>
    /// "cena": "auto" → garante a cena "PainelDeck · Tela principal" (captura do monitor principal ajustada
    /// à tela do OBS) e põe ela no ar. O microfone e o som do sistema são fontes globais do OBS: entram em toda cena.
    /// </summary>
    private async Task EnsureAutoSceneAsync()
    {
        const string sceneName = "PainelDeck · Tela principal", inputName = "Monitor principal (PainelDeck)";
        var list = await obs.RequestAsync("GetSceneList", null);
        var exists = (list?["scenes"] as JsonArray)?.Any(s => (string?)s?["sceneName"] == sceneName) == true;
        if (!exists)
        {
            await obs.RequestAsync("CreateScene", new JsonObject { ["sceneName"] = sceneName });
            await obs.RequestAsync("CreateInput", new JsonObject
            {
                ["sceneName"] = sceneName, ["inputName"] = inputName, ["inputKind"] = "monitor_capture",
                ["inputSettings"] = new JsonObject(), ["sceneItemEnabled"] = true,
            });
            // qual monitor: o principal do Windows fica na posição 0,0
            foreach (var prop in new[] { "monitor_id", "monitor" })
            {
                try
                {
                    var items = (await obs.RequestAsync("GetInputPropertiesListPropertyItems",
                        new JsonObject { ["inputName"] = inputName, ["propertyName"] = prop }))?["propertyItems"] as JsonArray;
                    if (items == null || items.Count == 0) continue;
                    var real = items.Where(i => (string?)i?["itemName"] is { } n && !n.Contains("DUMMY", StringComparison.OrdinalIgnoreCase)).ToList();
                    var pick = real.FirstOrDefault(i => ((string?)i?["itemName"] ?? "").Contains("@ 0,0"))
                            ?? real.FirstOrDefault(i => ((string?)i?["itemName"] ?? "").Contains("Primary", StringComparison.OrdinalIgnoreCase)
                                                     || ((string?)i?["itemName"] ?? "").Contains("Principal", StringComparison.OrdinalIgnoreCase))
                            ?? real.FirstOrDefault();
                    if (pick?["itemValue"] == null) continue;
                    await obs.RequestAsync("SetInputSettings", new JsonObject
                    {
                        ["inputName"] = inputName,
                        ["inputSettings"] = new JsonObject { [prop] = pick["itemValue"]!.DeepClone() },
                    });
                    break;
                }
                catch { /* versão do OBS sem essa propriedade: tenta a outra */ }
            }
            try // ajusta o monitor (ultrawide ou não) pra caber inteiro na tela do OBS
            {
                var video = await obs.RequestAsync("GetVideoSettings", null);
                var id = (await obs.RequestAsync("GetSceneItemId", new JsonObject { ["sceneName"] = sceneName, ["sourceName"] = inputName }))?["sceneItemId"]?.GetValue<int>();
                if (id != null && video != null)
                    await obs.RequestAsync("SetSceneItemTransform", new JsonObject
                    {
                        ["sceneName"] = sceneName, ["sceneItemId"] = id,
                        ["sceneItemTransform"] = new JsonObject
                        {
                            ["positionX"] = 0, ["positionY"] = 0, ["alignment"] = 5, ["boundsAlignment"] = 0,
                            ["boundsType"] = "OBS_BOUNDS_SCALE_INNER",
                            ["boundsWidth"] = video["baseWidth"]!.GetValue<double>(), ["boundsHeight"] = video["baseHeight"]!.GetValue<double>(),
                        },
                    });
            }
            catch { /* fica do tamanho original */ }
        }
        await EnsureAudioAsync(sceneName);
        await obs.RequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = sceneName });
    }

    private static readonly (string Special, string Kind, string Name)[] AudioKinds =
    {
        ("mic1", "wasapi_input_capture", "Microfone (PainelDeck)"),
        ("desktop1", "wasapi_output_capture", "Som do sistema (PainelDeck)"),
    };

    /// <summary>
    /// Garante microfone e som do sistema na cena do painel. Com o Mic/Aux ou o Áudio do desktop DESATIVADOS nas
    /// configurações do OBS (aí o áudio de cada um só existe como fonte dentro de alguma cena), põe na cena do painel
    /// a fonte de captura de áudio que já existir no OBS; se não existir nenhuma, cria uma com o dispositivo padrão do Windows.
    /// </summary>
    private async Task EnsureAudioAsync(string sceneName)
    {
        JsonNode? special = null;
        try { special = await obs.RequestAsync("GetSpecialInputs", null); } catch { }
        foreach (var (key, kind, name) in AudioKinds)
        {
            if ((string?)special?[key] is { Length: > 0 }) continue; // fonte global ligada: já entra em toda cena
            try
            {
                var existing = await FirstInputAsync(kind);
                if (existing == null)
                {
                    await obs.RequestAsync("CreateInput", new JsonObject
                    {
                        ["sceneName"] = sceneName, ["inputName"] = name, ["inputKind"] = kind,
                        ["inputSettings"] = new JsonObject { ["device_id"] = "default" }, ["sceneItemEnabled"] = true,
                    });
                    continue;
                }
                try { await obs.RequestAsync("GetSceneItemId", new JsonObject { ["sceneName"] = sceneName, ["sourceName"] = existing }); }
                catch { await obs.RequestAsync("CreateSceneItem", new JsonObject { ["sceneName"] = sceneName, ["sourceName"] = existing, ["sceneItemEnabled"] = true }); }
            }
            catch { /* sem essa fonte: a faixa aparece como "fonte não achada" no painel */ }
        }
    }

    private async Task<string?> FirstInputAsync(string kind)
    {
        var list = (await obs.RequestAsync("GetInputList", new JsonObject { ["inputKind"] = kind }))?["inputs"] as JsonArray;
        return (string?)list?.FirstOrDefault()?["inputName"];
    }

    private void OpenInExplorer(string? file)
    {
        string? d;
        lock (gate) d = dir;
        if (!string.IsNullOrEmpty(file) && File.Exists(file))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true })?.Dispose();
        else if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{d}\"") { UseShellExecute = true })?.Dispose();
        else throw new InvalidOperationException("Ainda não sei onde o OBS salva as gravações (abra o OBS)");
    }

    // ---------- acompanhamento ----------

    private async Task Loop()
    {
        while (!stop.IsCancellationRequested)
        {
            try { await StepAsync(); }
            catch (Exception ex) { lock (gate) { if (state != "closed") { state = "error"; error = ex.Message; } } }
            try { await wake.WaitAsync(1000, stop.Token); } catch (OperationCanceledException) { return; }
        }
    }

    private void Wake() { try { if (wake.CurrentCount == 0) wake.Release(); } catch { } }

    private async Task StepAsync()
    {
        step++;
        var now = DateTime.UtcNow;
        bool scan;
        lock (gate) { scan = now >= nextSlow; if (scan) nextSlow = now.AddSeconds(active ? 5 : 20); }
        if (scan) ScanFolder(); // últimas gravações e espaço livre: funciona com o OBS fechado (lembra a pasta)
        if (!obs.IsConnected)
        {
            if (now < nextConnect) return;
            nextConnect = now.AddSeconds(5);
            if (!ObsRunning())
            {
                lock (gate)
                {
                    if (now > startWhenReadyUntil) { if (active) Finish(null); state = "closed"; starting = false; error = null; }
                    foreach (var t in tracks) t.Found = false;
                }
                return;
            }
            try { await obs.RequestAsync("GetVersion", null); }
            catch (Exception ex)
            {
                lock (gate)
                {
                    state = now < startWhenReadyUntil ? "connecting" : "error"; // abrindo: o servidor demora uns segundos
                    if (state == "error")
                    {
                        error = ObsLocalConfig.Explain(ex.Message);
                        if (starting) { starting = false; fail = error; failOk = false; failSeq++; } // abriu o OBS mas ele não respondeu: avisa em vez de sumir
                    }
                }
                return;
            }
            lock (gate) { state = "ok"; error = null; needRefresh = true; }
        }

        bool rbNow;
        lock (gate) { rbNow = now < replayWhenReadyUntil; if (rbNow) replayWhenReadyUntil = default; }
        if (rbNow)
            try { await ReplayOnAsync(announce: true); }
            catch (Exception ex) { Fail(ex.Message); }

        bool refresh;
        lock (gate) { refresh = needRefresh || Key(ReadCfg()) != cfgKey; needRefresh = false; }
        if (refresh) await RefreshAsync();

        var st = await obs.RequestAsync("GetRecordStatus", null);
        bool isActive = st?["outputActive"]?.GetValue<bool>() == true;
        bool begin = false;
        lock (gate)
        {
            state = "ok"; error = null;
            if (isActive && !active) Begin();
            if (!isActive && active) Finish(null);
            active = isActive;
            if (isActive) starting = false;
            paused = isActive && st?["outputPaused"]?.GetValue<bool>() == true;
            baseMs = (long)(st?["outputDuration"]?.GetValue<double>() ?? 0);
            bytes = (long)(st?["outputBytes"]?.GetValue<double>() ?? 0);
            if (isActive && !paused) since.Restart(); else since.Reset();
            if (!isActive && now < startWhenReadyUntil) { startWhenReadyUntil = default; begin = true; }
            else if (!isActive && starting && startedAt != default && now > startedAt.AddSeconds(8))
            {
                // pediu pra gravar e o OBS não começou (ele costuma mostrar o motivo numa janela dele)
                starting = false; startedAt = default;
                fail = "O OBS não começou a gravar. Veja se ele mostrou algum aviso na janela dele."; failOk = false; failSeq++;
            }
        }
        if (begin)
        {
            try { await StartAsync(); }
            catch (Exception ex) { lock (gate) starting = false; Fail(ex.Message); }
            return;
        }

        string? silent = null;
        lock (gate)
            if (isActive && !paused && !micHeard && !micWarned && NowMs() > 8000 && tracks.Length > 0 && tracks[0].Found && !tracks[0].Muted)
            { micWarned = true; silent = tracks[0].Source; }
        if (silent != null)
        {
            var dev = await DeviceNameAsync(silent);
            Fail($"Ainda não chegou som nenhum do microfone \"{silent}\"" + (dev != "" ? $" (dispositivo: {dev})" : "")
               + ". Se você já falou, troque o dispositivo em OBS → Configurações → Áudio → Mic/Aux.");
        }

        if (isActive && step % 2 == 0)
        {
            try
            {
                var g = await obs.RequestAsync("GetStats", null) as JsonObject;
                if (g != null) lock (gate) stats = new JsonObject
                {
                    ["fps"] = Math.Round(g["activeFps"]?.GetValue<double>() ?? 0, 1),
                    ["cpu"] = Math.Round(g["cpuUsage"]?.GetValue<double>() ?? 0, 1),
                    ["skipped"] = g["outputSkippedFrames"]?.GetValue<double>() ?? 0,
                    ["total"] = g["outputTotalFrames"]?.GetValue<double>() ?? 0,
                    ["lagged"] = g["renderSkippedFrames"]?.GetValue<double>() ?? 0,
                };
            }
            catch { }
        }
    }

    /// <summary>Nomes das fontes de áudio, mudo, faixas do arquivo, cena no ar e pasta das gravações.</summary>
    private async Task RefreshAsync()
    {
        var cfg = ReadCfg();
        JsonNode? special = null;
        try { special = await obs.RequestAsync("GetSpecialInputs", null); } catch { }
        var defaults = new[] { (string?)special?["mic1"], (string?)special?["desktop1"] };
        for (int i = 0; i < defaults.Length; i++) // Mic/Aux ou Áudio do desktop desativados no OBS: usa a fonte de captura de áudio que existir
            if (string.IsNullOrEmpty(defaults[i]))
                try { defaults[i] = await FirstInputAsync(AudioKinds[i].Kind); } catch { }
        var fresh = new List<Track>();
        for (int i = 0; i < cfg.Tracks.Length; i++)
        {
            var (name, source) = cfg.Tracks[i];
            if (source == "") source = defaults.ElementAtOrDefault(i) ?? "";
            var t = new Track { Name = name, Source = source };
            if (source != "")
            {
                try
                {
                    t.Muted = (await obs.RequestAsync("GetInputMute", new JsonObject { ["inputName"] = source }))?["inputMuted"]?.GetValue<bool>() == true;
                    t.Found = true;
                    var at = (await obs.RequestAsync("GetInputAudioTracks", new JsonObject { ["inputName"] = source }))?["inputAudioTracks"] as JsonObject;
                    if (at != null)
                        t.Mixer = at.Where(kv => kv.Value?.GetValue<bool>() == true).Select(kv => int.TryParse(kv.Key, out var n) ? n : 0).Where(n => n > 0).ToArray();
                }
                catch { /* fonte não existe no OBS */ }
            }
            fresh.Add(t);
        }
        string? micDev = null; bool rb = false;
        if (fresh.Count > 0 && fresh[0].Found) micDev = await DeviceNameAsync(fresh[0].Source);
        try { rb = (await obs.RequestAsync("GetReplayBufferStatus", null))?["outputActive"]?.GetValue<bool>() == true; } catch { }
        string? sceneNow = null, recDir = null;
        try { sceneNow = (string?)(await obs.RequestAsync("GetCurrentProgramScene", null))?["currentProgramSceneName"]; } catch { }
        try { recDir = (string?)(await obs.RequestAsync("GetRecordDirectory", null))?["recordDirectory"]; } catch { }
        lock (gate)
        {
            // mantém o histórico da gravação em andamento se as faixas continuam as mesmas
            for (int i = 0; i < fresh.Count && i < tracks.Length; i++)
                if (tracks[i].Source == fresh[i].Source || tracks[i].Source == "") fresh[i].History.AddRange(tracks[i].History);
            tracks = fresh.ToArray();
            micDevice = string.IsNullOrEmpty(micDev) ? null : micDev;
            replayActive = rb;
            if (rb && replaySeconds == 0) replaySeconds = cfg.Replay;
            scene = sceneNow ?? scene;
            if (recDir != null && recDir != dir)
            {
                dir = recDir;
                try { Directory.CreateDirectory(Path.GetDirectoryName(DirFile)!); File.WriteAllText(DirFile, recDir); } catch { }
            }
            cfgKey = Key(cfg);
            nextSlow = default;
        }
    }

    private void Begin() // com o gate
    {
        foreach (var t in tracks) { t.History.Clear(); t.Peak = t.Mag = -100; }
        micHeard = false; micWarned = false;
        markers.Clear();
        stats = null; last = null;
        nextSlow = default;
    }

    private void Finish(string? outputPath) // com o gate
    {
        if (outputPath != null) path = outputPath;
        var ms = NowMs();
        if (ms <= 0 && last != null) return;
        last = new JsonObject
        {
            ["path"] = path, ["name"] = path != null ? Path.GetFileName(path) : null,
            ["ms"] = ms, ["bytes"] = bytes, ["markers"] = markers.Count, ["at"] = DateTimeOffset.Now.ToString("o"),
        };
        active = false; paused = false; starting = false;
        since.Reset(); baseMs = ms;
        nextSlow = default;
    }

    private long NowMs() => baseMs + (since.IsRunning ? since.ElapsedMilliseconds : 0);

    private void OnEvent(string type, JsonObject? d)
    {
        if (d == null) return;
        switch (type)
        {
            case "InputVolumeMeters":
                if (d["inputs"] is not JsonArray inputs) return;
                lock (gate)
                {
                    bool rec = active && !paused;
                    int idx = rec ? (int)Math.Min(NowMs() / BucketMs, MaxBuckets - 1) : -1;
                    foreach (var t in tracks)
                    {
                        if (t.Source == "") continue;
                        var input = inputs.FirstOrDefault(i => (string?)i?["inputName"] == t.Source);
                        double peak = 0, mag = 0;
                        if (input?["inputLevelsMul"] is JsonArray chans)
                            foreach (var ch in chans)
                                if (ch is JsonArray c && c.Count >= 2)
                                {
                                    mag = Math.Max(mag, c[0]!.GetValue<double>());
                                    peak = Math.Max(peak, c[1]!.GetValue<double>());
                                }
                        double pdb = t.Muted ? -100 : Db(peak), mdb = t.Muted ? -100 : Db(mag);
                        if (rec && pdb > -70 && ReferenceEquals(t, tracks[0])) micHeard = true;
                        t.Peak = Math.Max(t.Peak, pdb);
                        t.Mag = Math.Max(t.Mag, mdb);
                        if (idx < 0) continue;
                        while (t.History.Count <= idx) t.History.Add(0);
                        t.History[idx] = Math.Max(t.History[idx], ToByte(pdb));
                    }
                }
                break;
            case "RecordStateChanged":
                var outState = (string?)d["outputState"];
                if (outState == "OBS_WEBSOCKET_OUTPUT_STOPPED" && (string?)d["outputPath"] is { Length: > 0 } p)
                    lock (gate) { path = p; if (last != null) { last["path"] = p; last["name"] = Path.GetFileName(p); } }
                else if (outState == "OBS_WEBSOCKET_OUTPUT_STARTING")
                    lock (gate) path = null;
                else if (outState == "OBS_WEBSOCKET_OUTPUT_STARTED" && (string?)d["outputPath"] is { Length: > 0 } p2)
                    lock (gate) path = p2;
                Wake();
                break;
            case "ReplayBufferStateChanged":
                lock (gate) replayActive = d["outputActive"]?.GetValue<bool>() == true;
                break;
            case "ReplayBufferSaved":
                var saved = (string?)d["savedReplayPath"];
                int trim, buffer;
                lock (gate) { trim = replayTrim; replayTrim = 0; buffer = replaySeconds > 0 ? replaySeconds : ReadCfg().Replay; }
                _ = Task.Run(() => ReplaySavedAsync(saved, trim, buffer));
                break;
            case "InputSettingsChanged":
                lock (gate) needRefresh = true;
                break;
            case "InputMuteStateChanged":
                lock (gate)
                    foreach (var t in tracks)
                        if (t.Source == (string?)d["inputName"]) t.Muted = d["inputMuted"]?.GetValue<bool>() == true;
                break;
            case "CurrentProgramSceneChanged":
                lock (gate) scene = (string?)d["sceneName"] ?? scene;
                break;
            case "InputNameChanged" or "InputCreated" or "InputRemoved":
                lock (gate) needRefresh = true;
                break;
        }
    }

    private static double Db(double mul) => mul <= 0.00001 ? -100 : 20 * Math.Log10(mul);
    private static byte ToByte(double db) => (byte)Math.Round(Math.Clamp((db - FloorDb) / -FloorDb, 0, 1) * 255);

    /// <summary>Lembra a pasta das gravações pra lista de "últimas gravações" aparecer mesmo com o OBS fechado.</summary>
    private static string DirFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PainelDeck", "gravacao-pasta.txt");

    private static readonly string[] VideoExts = { ".mkv", ".mp4", ".mov", ".flv", ".ts", ".m3u8", ".webm" };

    private void ScanFolder()
    {
        string? d;
        lock (gate) d = dir;
        if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) return;
        try
        {
            var files = new DirectoryInfo(d).EnumerateFiles()
                .Where(f => VideoExts.Contains(f.Extension.ToLowerInvariant()))
                .OrderByDescending(f => f.LastWriteTime).Take(6)
                .Select(f => (object)new { name = f.Name, path = f.FullName, bytes = f.Length, at = new DateTimeOffset(f.LastWriteTime).ToString("o") })
                .ToList();
            double? free = null;
            try { free = Math.Round(new DriveInfo(Path.GetPathRoot(d)!).AvailableFreeSpace / 1073741824.0, 1); } catch { }
            lock (gate) { recent = files; freeGb = free; }
        }
        catch { /* pasta sem acesso */ }
    }

    private void Fail(string message) { lock (gate) { fail = message; failOk = false; failSeq++; } }
    private void Note(string message) { lock (gate) { fail = message; failOk = true; failSeq++; } }

    private static string Key(Cfg c) => c.Scene + "|" + c.ObsPath + "|" + string.Join("|", c.Tracks.Select(t => t.Name + "=" + t.Source));

    private Cfg ReadCfg()
    {
        var g = deck.Snapshot()["gravacao"] as JsonObject;
        string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        var list = new List<(string, string)>();
        if (g?["faixas"] is JsonArray arr)
            foreach (var it in arr)
                if (it is JsonObject o) list.Add((S(o["nome"]), S(o["fonte"])));
        if (list.Count == 0) { list.Add(("Microfone", "")); list.Add(("Sistema", "")); }
        for (int i = 0; i < list.Count; i++)
            if (list[i].Item1 == "") list[i] = (i == 0 ? "Microfone" : i == 1 ? "Sistema" : $"Faixa {i + 1}", list[i].Item2);
        // sem "cena" no deck: "auto" (cria a cena do monitor principal); "cena": "" = grava o que estiver no OBS
        // replay: "replay" = quanto o buffer guarda; "replayBotoes" = os cortes oferecidos; "replayAtalho" = o corte do atalho
        int N(string k, int def) => g?[k] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)Math.Clamp(d, 5, 1800) : def;
        var buttons = (g?["replayBotoes"] as JsonArray)?.Select(b => b is JsonValue v && v.TryGetValue<double>(out var d) ? (int)Math.Clamp(d, 5, 1800) : 0)
            .Where(b => b > 0).Distinct().OrderBy(b => b).Take(4).ToArray();
        if (buttons == null || buttons.Length == 0) buttons = new[] { 30, 60, 300 };
        int replay = Math.Max(N("replay", 300), buttons.Max());
        bool inGame = !(g?["replayNoJogo"] is JsonValue ig && ig.TryGetValue<bool>(out var igb) && !igb);
        return new Cfg(g?["cena"] == null ? "auto" : S(g["cena"]), S(g?["obs"]), list.Take(4).ToArray(), replay, buttons,
            Math.Min(N("replayAtalho", 60), replay), inGame, S(g?["ffmpeg"]));
    }

    // ---------- o que o painel lê ----------

    /// <summary>Vai dentro do status (1x por segundo) como "rec".</summary>
    public object Status()
    {
        lock (gate)
        {
            return new
            {
                obs = state,
                error,
                fail,
                failSeq,
                failOk,
                active,
                paused,
                starting,
                ms = NowMs(),
                bytes,
                path,
                file = path != null ? Path.GetFileName(path) : null,
                scene,
                dir,
                freeGb,
                hotkey,
                micDevice,
                replay = ReplayStatus(),
                metering = Streaming(),
                tracks = tracks.Select(t => new { name = t.Name, source = t.Source, muted = t.Muted, found = t.Found, mixer = t.Mixer }).ToArray(),
                markers = markers.ToArray(),
                stats = stats?.DeepClone(),
                last = last?.DeepClone(),
                recent,
            };
        }
    }

    private object ReplayStatus() // com o gate
    {
        var c = ReadCfg();
        int buffer = replayActive && replaySeconds > 0 ? replaySeconds : c.Replay;
        return new
        {
            active = replayActive, seconds = buffer, last = replayLast, hotkey = replayHotkey, key = c.ReplayKey,
            buttons = c.ReplayButtons.Where(b => b <= buffer).ToArray(), auto = c.ReplayInGame, clips = clips.ToArray(),
            opening = DateTime.UtcNow < replayWhenReadyUntil,
        };
    }

    private bool Streaming() => active || DateTime.UtcNow < meterUntil; // com o gate

    /// <summary>Níveis desde a última chamada (o painel pede ~15x por segundo); null = nada pra mostrar agora.</summary>
    public object? Meters()
    {
        lock (gate)
        {
            if (!Streaming() || state != "ok") return null;
            var l = new double[tracks.Length * 2];
            for (int i = 0; i < tracks.Length; i++)
            {
                l[i * 2] = Math.Round(tracks[i].Peak, 1);
                l[i * 2 + 1] = Math.Round(tracks[i].Mag, 1);
                tracks[i].Peak = tracks[i].Mag = -100;
            }
            return new { type = "meters", ms = active ? NowMs() : -1, paused, l };
        }
    }

    /// <summary>Histórico dos níveis da gravação atual: um byte por 250 ms por faixa (0 = -60 dB, 255 = 0 dB), em base64.</summary>
    private object History()
    {
        lock (gate)
            return new { bucketMs = BucketMs, ms = NowMs(), tracks = tracks.Select(t => Convert.ToBase64String(t.History.ToArray())).ToArray(), markers = markers.ToArray() };
    }

    public void Dispose()
    {
        stop.Cancel();
        obs.Close();
    }
}
