using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PainelDeck;

internal sealed record GameSession(string Name, string? Image, string Exe, uint Pid, DateTimeOffset Since, bool Known);

/// <summary>
/// Modo jogo automático: a cada 2s olha se tem jogo aberto e, quando tem, liga o FPS, conta o tempo de jogo
/// e roda as ações "onStart"/"onStop" (do jogo e da seção "gameMode" do deck.json). Só LÊ os processos,
/// não mexe em janela nenhuma (nada de roubar foco).
///
/// Como reconhece o jogo, em ordem:
///  1. "process" do jogo na lista "games" ("ForHonor.exe" ou uma lista) — em qualquer lugar, mesmo sem foco;
///  2. a janela em foco: jogo da Store/Xbox pelo pacote (o "shell:AppsFolder\..." da ação do jogo);
///  3. a janela em foco: pasta da biblioteca (steamapps\common\ARC Raiders, Ubisoft...\games\For Honor) parecida com o nome do jogo;
///  4. se "detectUnknown" (padrão): qualquer .exe dentro de uma biblioteca de jogos vira um jogo com o nome da pasta.
/// Depois de achar, segue aquele processo até ele fechar (dar Alt+Tab não encerra a sessão).
/// </summary>
internal sealed class GameMode : IDisposable
{
    private const int TickMs = 2000, GraceMs = 20_000;

    private static readonly string[][] Libraries =
    {
        new[] { "steamapps", "common" },
        new[] { "ubisoft game launcher", "games" },
        new[] { "epic games" },
        new[] { "xboxgames" },
        new[] { "gog galaxy", "games" },
        new[] { "riot games" },
        new[] { "ea games" },
        new[] { "battle.net" },
    };
    private static readonly string[] Helpers =
        { "crash", "report", "launcher", "updater", "setup", "unins", "install", "redist", "helper", "anticheat", "battleye", "service", "overlay", "cefprocess", "webhelper" };

    private readonly Deck deck;
    private readonly Playtime playtime;
    private readonly Fps fps;
    private readonly Func<JsonElement, Task> run;
    private readonly CancellationTokenSource cts = new();
    private readonly uint myPid = (uint)Environment.ProcessId;
    private GameSession? current;
    private Cfg? currentCfg;
    private long goneSince;
    private bool idle;

    public GameMode(Deck deck, Playtime playtime, Fps fps, Func<JsonElement, Task> run)
    {
        (this.deck, this.playtime, this.fps, this.run) = (deck, playtime, fps, run);
        Task.Run(LoopAsync);
    }

    public GameSession? Current => current;

    /// <summary>"game" do status (null = nenhum jogo aberto).</summary>
    public object? Status()
    {
        var c = current;
        if (c == null) return null;
        var f = fps.Read();
        return new
        {
            name = c.Name,
            image = c.Image,
            exe = c.Exe,
            known = c.Known,
            since = c.Since.ToString("o"),
            sessionSec = Math.Round(playtime.SessionSec),
            todaySec = Math.Round(playtime.TodaySec(c.Name)),
            idle,
            fps = f.Fps,
            fpsLow = f.Low1,
            fpsAvg = f.Avg,
            fpsInfo = f.Info,
        };
    }

    private async Task LoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            try { Tick(); } catch { /* um processo esquisito não pode derrubar o app */ }
            try { await Task.Delay(TickMs, cts.Token); } catch (TaskCanceledException) { }
        }
    }

    private void Tick()
    {
        var cfg = Cfg.Read(deck.Snapshot());
        if (!cfg.Enabled) { if (current != null) End(); return; }
        idle = Native.IdleMs() > cfg.IdleMinutes * 60_000L;

        if (current is { } c)
        {
            if (Native.IsAlive(c.Pid)) { goneSince = 0; playtime.Tick(idle); return; }
            // o processo sumiu: pode ser launcher → jogo, ou o jogo reabrindo. Procura de novo antes de encerrar.
            var again = Find(cfg, c.Name);
            if (again != null)
            {
                current = c with { Pid = again.Pid, Exe = again.Exe };
                fps.Start(again.Pid, again.Exe);
                goneSince = 0;
                return;
            }
            if (goneSince == 0) goneSince = Environment.TickCount64;
            if (Environment.TickCount64 - goneSince < GraceMs) return;
            End();
        }

        if (Find(cfg, null) is { } found) Begin(cfg, found);
    }

    private void Begin(Cfg cfg, GameSession s)
    {
        current = s;
        currentCfg = cfg;
        goneSince = 0;
        playtime.Begin(s.Name);
        fps.Start(s.Pid, s.Exe);
        var g = cfg.Games.FirstOrDefault(x => x.Name == s.Name);
        RunAll(cfg.OnStart, g?.OnStart);
    }

    private void End()
    {
        var s = current;
        var cfg = currentCfg;
        current = null;
        currentCfg = null;
        playtime.End();
        fps.Stop();
        if (s == null || cfg == null) return;
        var g = cfg.Games.FirstOrDefault(x => x.Name == s.Name);
        RunAll(g?.OnStop, cfg.OnStop);
    }

    /// <summary>Roda as ações em ordem, fora desta thread; erro numa não impede as outras.</summary>
    private void RunAll(params JsonElement?[] lists) => Task.Run(async () =>
    {
        foreach (var list in lists)
        {
            if (list is not { } l) continue;
            var items = l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().ToArray() : new[] { l };
            foreach (var a in items)
            {
                try { await run(a); } catch { }
            }
        }
    });

    /// <summary>Procura um jogo aberto (onlyName = só esse jogo, pra reencontrar depois de reabrir).</summary>
    private GameSession? Find(Cfg cfg, string? onlyName)
    {
        // 1) "process" explícito: vale mesmo sem foco
        var named = cfg.Games.Where(g => g.Processes.Length > 0 && (onlyName == null || g.Name == onlyName)).ToArray();
        if (named.Length > 0)
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    var exe = p.ProcessName + ".exe";
                    var g = named.FirstOrDefault(x => x.Processes.Contains(exe, StringComparer.OrdinalIgnoreCase));
                    if (g != null && p.Id != myPid) return new GameSession(g.Name, g.Image, exe, (uint)p.Id, DateTimeOffset.Now, true);
                }
            }
        }

        // 2-4) a janela em foco
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(h, out var pid);
        if (pid == 0 || pid == myPid) return null;
        var (path, family) = Native.ProcessIdentity(pid);
        if (path == null) return null;
        var file = Path.GetFileName(path);
        if (cfg.Ignore.Contains(file, StringComparer.OrdinalIgnoreCase)) return null;

        var folder = LibraryFolder(path, cfg.ExtraLibraries);
        var game = cfg.Games.FirstOrDefault(g => (onlyName == null || g.Name == onlyName) && Matches(g, file, family, folder));
        if (game != null) return new GameSession(game.Name, game.Image, file, pid, DateTimeOffset.Now, true);
        if (onlyName != null || !cfg.DetectUnknown || folder == null || IsHelper(file)) return null;
        return new GameSession(folder, null, file, pid, DateTimeOffset.Now, false);
    }

    private static bool Matches(GameCfg g, string file, string? family, string? folder)
    {
        if (g.Processes.Contains(file, StringComparer.OrdinalIgnoreCase)) return true;
        if (family != null && g.Family != null && string.Equals(family, g.Family, StringComparison.OrdinalIgnoreCase)) return true;
        if (folder == null || g.Key.Length < 3) return false;
        var f = Normalize(folder);
        return f == g.Key || (g.Key.Length >= 5 && f.Contains(g.Key)) || (f.Length >= 5 && g.Key.Contains(f));
    }

    private static bool IsHelper(string file)
    {
        var f = file.ToLowerInvariant();
        return Helpers.Any(f.Contains);
    }

    /// <summary>"D:\SteamLibrary\steamapps\common\ARC Raiders\...\x.exe" → "ARC Raiders" (null se não está numa biblioteca).</summary>
    internal static string? LibraryFolder(string path, string[] extra)
    {
        foreach (var root in extra)
        {
            var r = root.TrimEnd('\\', '/') + "\\";
            if (path.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            {
                var rest = path[r.Length..].Split('\\', '/');
                if (rest.Length > 1) return rest[0];
            }
        }
        var parts = path.Split('\\', '/');
        foreach (var lib in Libraries)
        {
            for (int i = 0; i + lib.Length < parts.Length - 1; i++)
            {
                bool ok = true;
                for (int k = 0; k < lib.Length && ok; k++) ok = string.Equals(parts[i + k], lib[k], StringComparison.OrdinalIgnoreCase);
                if (ok) return parts[i + lib.Length];
            }
        }
        return null;
    }

    internal static string Normalize(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public void Dispose()
    {
        cts.Cancel();
        End();
    }

    // ---------- configuração (deck.json: "games" e "gameMode") ----------

    private sealed record GameCfg(string Name, string? Image, string Key, string[] Processes, string? Family, JsonElement? OnStart, JsonElement? OnStop);

    private sealed record Cfg(bool Enabled, bool DetectUnknown, int IdleMinutes, string[] Ignore, string[] ExtraLibraries,
        JsonElement? OnStart, JsonElement? OnStop, GameCfg[] Games)
    {
        public static Cfg Read(JsonNode deck)
        {
            var gm = deck["gameMode"] as JsonObject;
            bool Bool(string k, bool def) => gm?[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;
            int idle = gm?["idleMinutes"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? Math.Clamp(i, 1, 240) : 10;
            var games = (deck["games"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(g => (string?)Val(g["name"]) is { Length: > 0 } name
                    ? new GameCfg(name, Val(g["image"]), Normalize(name), Strings(g["process"]).Select(ExeName).ToArray(), FamilyOf(g["action"]), El(g["onStart"]), El(g["onStop"]))
                    : null)
                .Where(g => g != null).Select(g => g!).ToArray();
            return new Cfg(Bool("enabled", true), Bool("detectUnknown", true), idle,
                Strings(gm?["ignore"]).Select(ExeName).ToArray(), Strings(gm?["libraries"]), El(gm?["onStart"]), El(gm?["onStop"]), games);
        }

        private static string? Val(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        private static string[] Strings(JsonNode? n) => n switch
        {
            JsonArray a => a.Select(Val).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToArray(),
            JsonValue => Val(n) is { Length: > 0 } s ? new[] { s.Trim() } : Array.Empty<string>(),
            _ => Array.Empty<string>(),
        };

        private static string ExeName(string s) => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : s + ".exe";

        private static JsonElement? El(JsonNode? n) => n == null ? null : JsonSerializer.SerializeToElement(n);

        /// <summary>Ação "run explorer.exe shell:AppsFolder\FAMILIA!App" (jogo da Store/Xbox) → "FAMILIA".</summary>
        private static string? FamilyOf(JsonNode? action)
        {
            var text = Val(action?["args"]) ?? Val(action?["path"]) ?? Val(action?["url"]) ?? "";
            var i = text.IndexOf("AppsFolder\\", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            var rest = text[(i + "AppsFolder\\".Length)..];
            var bang = rest.IndexOf('!');
            return bang > 0 ? rest[..bang].Trim() : null;
        }
    }
}
