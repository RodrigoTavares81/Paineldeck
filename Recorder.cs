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

    private sealed record Cfg(string Scene, string ObsPath, (string Name, string Source)[] Tracks);

    private readonly ObsClient obs;
    private readonly Deck deck;
    private readonly string hotkey;
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim wake = new(0, 1);

    private Track[] tracks = { new() { Name = "Microfone" }, new() { Name = "Sistema" } };
    private string cfgKey = "";
    private string state = "closed";   // closed (OBS fechado) | connecting | ok | error
    private string? error;             // por que não está conectado (aparece no cartão)
    private string? fail;              // último comando que deu errado (o painel mostra num aviso quando failSeq muda)
    private int failSeq;
    private bool active, paused, starting;
    private long baseMs, bytes;        // duração na última leitura do OBS
    private readonly Stopwatch since = new();
    private string? path, scene, dir;
    private readonly List<long> markers = new();
    private JsonObject? stats, last;
    private List<object> recent = new();
    private double? freeGb;
    private DateTime meterUntil, startWhenReadyUntil, nextConnect, nextSlow;
    private bool needRefresh = true;
    private int step;

    public Recorder(Settings settings, Deck deck)
    {
        this.deck = deck;
        hotkey = settings.Hotkeys.TryGetValue("record", out var hk) ? hk : "";
        obs = new ObsClient(settings.ObsUrl, settings.ObsPassword, SubScenes | SubInputs | SubOutputs | SubMeters);
        obs.Event += OnEvent;
        try { if (File.Exists(DirFile)) dir = File.ReadAllText(DirFile).Trim(); } catch { }
        _ = Task.Run(Loop);
    }

    // ---------- comandos ----------

    /// <summary>cmd: toggle | start | stop | pause | mark | mute (track) | test | open (path) | history | frame (width) | status.</summary>
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

    private async Task StartAsync()
    {
        if (!await EnsureObsAsync(launch: true)) return; // o OBS está abrindo: grava assim que ele responder
        var cfg = ReadCfg();
        lock (gate) { starting = true; path = null; }
        if (cfg.Scene.Equals("auto", StringComparison.OrdinalIgnoreCase)) await EnsureAutoSceneAsync();
        else if (cfg.Scene != "") await obs.RequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = cfg.Scene });
        await RefreshAsync();
        await obs.RequestAsync("StartRecord", null);
        Wake();
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
        await obs.RequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = sceneName });
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
                        if (starting) { starting = false; fail = error; failSeq++; } // abriu o OBS mas ele não respondeu: avisa em vez de sumir
                    }
                }
                return;
            }
            lock (gate) { state = "ok"; error = null; needRefresh = true; }
        }

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
        }
        if (begin)
        {
            try { await StartAsync(); }
            catch (Exception ex) { lock (gate) starting = false; Fail(ex.Message); }
            return;
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
        string? sceneNow = null, recDir = null;
        try { sceneNow = (string?)(await obs.RequestAsync("GetCurrentProgramScene", null))?["currentProgramSceneName"]; } catch { }
        try { recDir = (string?)(await obs.RequestAsync("GetRecordDirectory", null))?["recordDirectory"]; } catch { }
        lock (gate)
        {
            // mantém o histórico da gravação em andamento se as faixas continuam as mesmas
            for (int i = 0; i < fresh.Count && i < tracks.Length; i++)
                if (tracks[i].Source == fresh[i].Source || tracks[i].Source == "") fresh[i].History.AddRange(tracks[i].History);
            tracks = fresh.ToArray();
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

    private void Fail(string message) { lock (gate) { fail = message; failSeq++; } }

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
        return new Cfg(g?["cena"] == null ? "auto" : S(g["cena"]), S(g?["obs"]), list.Take(4).ToArray());
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
                metering = Streaming(),
                tracks = tracks.Select(t => new { name = t.Name, source = t.Source, muted = t.Muted, found = t.Found, mixer = t.Mixer }).ToArray(),
                markers = markers.ToArray(),
                stats = stats?.DeepClone(),
                last = last?.DeepClone(),
                recent,
            };
        }
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
