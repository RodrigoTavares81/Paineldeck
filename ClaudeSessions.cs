using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PainelDeck;

/// <summary>
/// Sessões do Claude Code deste PC (as conversas ficam em %USERPROFILE%\.claude\projects\&lt;pasta&gt;\&lt;id&gt;.jsonl).
/// Lista as que mexeram nas últimas horas, com título, projeto, última mensagem e estado,
/// e manda mensagem nova pra uma delas com `claude --resume &lt;id&gt; -p` em segundo plano (nenhuma janela abre).
/// Config opcional no deck.json: "claude": { "hours": 72, "max": 14, "permissionMode": "acceptEdits", "allowedTools": "" }.
/// As conversas do claude.ai (app/web) não têm API pública: aqui só aparecem as do Claude Code no PC.
/// </summary>
internal sealed partial class ClaudeSessions
{
    private sealed record Parsed(long Length, DateTime Mtime, string Id, string Cwd, string Title, string? FirstPrompt,
        string LastRole, string LastText, bool LastIsFinal, string? Branch, List<object> Recent);

    private sealed class Run
    {
        public DateTimeOffset Started = DateTimeOffset.Now;
        public string Text = "";
    }

    private readonly string root; // pasta do PainelDeck: esconde as sessões do "✨ Pedir ao Claude"
    private readonly object gate = new();
    private readonly Dictionary<string, Parsed> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Run> running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Message, DateTimeOffset At)> errors = new(StringComparer.OrdinalIgnoreCase);

    public ClaudeSessions(string root) => this.root = root;

    private sealed record Config(int Hours, int Max, string PermissionMode, string AllowedTools);

    private static Config ReadConfig(JsonNode deck)
    {
        var n = deck["claude"] as JsonObject;
        string Str(string k, string def) => n?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : def;
        int Int(string k, int def, int min, int max) => n?[k] is JsonValue v && v.TryGetValue<int>(out var i) ? Math.Clamp(i, min, max) : def;
        var mode = Str("permissionMode", "acceptEdits");
        if (mode is not ("default" or "acceptEdits" or "plan" or "bypassPermissions")) mode = "acceptEdits";
        var tools = Str("allowedTools", "");
        if (!SafeTools().IsMatch(tools)) tools = "";
        return new Config(Int("hours", 72, 1, 24 * 30), Int("max", 14, 1, 40), mode, tools);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_,:() *.\-]*$")] private static partial Regex SafeTools();
    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z\-]{7,63}$")] private static partial Regex SafeId();

    public static string ProjectsDir()
    {
        var cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var baseDir = !string.IsNullOrWhiteSpace(cfg) ? cfg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(baseDir, "projects");
    }

    // ---------- lista ----------

    /// <summary>Resposta da chamada "claudeSessions". Lê só os arquivos que mudaram desde a última vez.</summary>
    public object List(JsonNode deck)
    {
        var cfg = ReadConfig(deck);
        var dir = ProjectsDir();
        if (!Directory.Exists(dir))
            return new { items = Array.Empty<object>(), error = "Não achei sessões do Claude Code neste PC (pasta .claude\\projects)." };

        var since = DateTime.Now - TimeSpan.FromHours(cfg.Hours);
        var files = new List<FileInfo>();
        foreach (var proj in SafeDirs(dir))
            foreach (var f in SafeFiles(proj))
                if (f.LastWriteTime >= since) files.Add(f);

        var list = new List<(Parsed P, DateTime M)>();
        lock (gate)
        {
            foreach (var f in files.OrderByDescending(f => f.LastWriteTime))
            {
                if (list.Count >= cfg.Max) break;
                Parsed? p;
                if (!cache.TryGetValue(f.FullName, out p) || p.Length != f.Length || p.Mtime != f.LastWriteTime)
                {
                    p = Parse(f);
                    if (p != null) cache[f.FullName] = p; else cache.Remove(f.FullName);
                }
                if (p == null || Hidden(p)) continue;
                list.Add((p, f.LastWriteTime));
            }
            // não deixa o cache crescer pra sempre
            if (cache.Count > 200) foreach (var k in cache.Keys.Except(files.Select(f => f.FullName)).ToList()) cache.Remove(k);
        }

        var now = DateTime.Now;
        object Item(Parsed p, DateTime mtime)
        {
            Run? run; (string Message, DateTimeOffset At) err;
            bool busy, hasErr;
            lock (gate)
            {
                busy = running.TryGetValue(p.Id, out run);
                hasErr = errors.TryGetValue(p.Id, out err) && err.At > new DateTimeOffset(mtime) - TimeSpan.FromSeconds(5);
            }
            var age = now - mtime;
            // trabalhando: mandamos agora daqui, ou o arquivo mexeu há pouco e o Claude ainda não fechou a resposta
            var state = busy || (!p.LastIsFinal && age < TimeSpan.FromSeconds(90)) ? "working"
                      : p.LastIsFinal ? "waiting" : "idle";
            return new
            {
                id = p.Id,
                title = p.Title,
                project = ProjectName(p.Cwd),
                cwd = p.Cwd,
                branch = p.Branch,
                updated = new DateTimeOffset(mtime).ToString("o"),
                state,
                lastRole = p.LastRole,
                last = p.LastText,
                recent = p.Recent,
                sending = busy ? run!.Text : null,
                sendingSince = busy ? run!.Started.ToString("o") : null,
                error = hasErr ? err.Message : null,
            };
        }

        return new { items = list.Select(x => Item(x.P, x.M)).ToArray(), error = (string?)null };
    }

    /// <summary>Esconde as sessões que o próprio painel abre no "✨ Pedir ao Claude".</summary>
    private bool Hidden(Parsed p) =>
        p.FirstPrompt != null && p.FirstPrompt.StartsWith("Você está dentro da pasta do PainelDeck", StringComparison.Ordinal)
        && string.Equals(Path.GetFullPath(p.Cwd.Length > 0 ? p.Cwd : "."), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);

    private static string ProjectName(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "";
        var name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? cwd : name;
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<FileInfo> SafeFiles(string dir)
    {
        try { return new DirectoryInfo(dir).GetFiles("*.jsonl", SearchOption.TopDirectoryOnly); } catch { return Array.Empty<FileInfo>(); }
    }

    // ---------- leitura do .jsonl ----------

    private const int HeadBytes = 256 * 1024, TailBytes = 768 * 1024;

    private static Parsed? Parse(FileInfo f)
    {
        try
        {
            using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = fs.Length;
            var head = ReadLines(fs, 0, (int)Math.Min(len, HeadBytes), dropFirst: false);
            var tailStart = Math.Max(0, len - TailBytes);
            var tail = tailStart == 0 ? head : ReadLines(fs, tailStart, (int)(len - tailStart), dropFirst: true);

            var id = Path.GetFileNameWithoutExtension(f.Name);
            string cwd = "", title = "", branch = "";
            string? firstPrompt = null, summary = null, custom = null;

            foreach (var line in head)
            {
                var o = TryParse(line);
                if (o == null || IsSidechain(o)) continue;
                if (cwd == "") cwd = Str(o, "cwd");
                if (firstPrompt == null && Str(o, "type") == "user" && !Bool(o, "isMeta"))
                {
                    var t = UserText(o);
                    if (t != null) firstPrompt = t;
                }
            }

            string lastRole = "", lastText = "";
            bool lastIsFinal = false, sawLast = false;
            var recent = new List<object>();
            foreach (var line in tail)
            {
                var o = TryParse(line);
                if (o == null) continue;
                var type = Str(o, "type");
                if (type == "summary") { var s = Str(o, "summary"); if (s != "") summary = s; continue; }
                if (type == "custom-title") { var s = Str(o, "customTitle"); if (s != "") custom = s; continue; }
                if (IsSidechain(o)) continue;
                var c = Str(o, "cwd"); if (c != "") cwd = c;
                var b = Str(o, "gitBranch"); if (b != "") branch = b;
                if (type == "user" && !Bool(o, "isMeta"))
                {
                    var t = UserText(o);
                    sawLast = true;
                    if (t != null)
                    {
                        lastRole = "user"; lastText = t; lastIsFinal = false;
                        Push(recent, "user", t);
                    }
                    else lastIsFinal = false; // resultado de ferramenta: o Claude vai continuar
                }
                else if (type == "assistant")
                {
                    sawLast = true;
                    var msg = o["message"] as JsonObject;
                    var (text, toolUse) = AssistantText(msg);
                    var stop = msg?["stop_reason"] is JsonValue sv && sv.TryGetValue<string>(out var ss) ? ss : null;
                    if (text != null)
                    {
                        lastRole = "assistant"; lastText = text;
                        Push(recent, "assistant", text);
                    }
                    lastIsFinal = !toolUse && (text != null || stop == "end_turn");
                }
            }
            if (!sawLast && firstPrompt == null) return null; // arquivo sem conversa (só snapshot/resumo)

            title = Clean(custom ?? summary ?? firstPrompt ?? "Sessão sem título", 90);
            return new Parsed(f.Length, f.LastWriteTime, id, cwd, title, firstPrompt, lastRole, Clean(lastText, 1400),
                lastIsFinal, branch == "" ? null : branch, recent);
        }
        catch { return null; }
    }

    private static void Push(List<object> recent, string role, string text)
    {
        recent.Add(new { role, text = Clean(text, 900) });
        if (recent.Count > 6) recent.RemoveAt(0);
    }

    private static List<string> ReadLines(FileStream fs, long start, int count, bool dropFirst)
    {
        var buf = new byte[count];
        fs.Seek(start, SeekOrigin.Begin);
        var read = 0;
        while (read < count) { var n = fs.Read(buf, read, count - read); if (n <= 0) break; read += n; }
        var lines = Encoding.UTF8.GetString(buf, 0, read).Split('\n').ToList();
        if (dropFirst && lines.Count > 0) lines.RemoveAt(0);           // começou no meio de uma linha
        if (read == count && lines.Count > 0 && start + count < fs.Length) lines.RemoveAt(lines.Count - 1); // terminou no meio
        return lines;
    }

    private static JsonObject? TryParse(string line)
    {
        line = line.Trim();
        if (line.Length < 2 || line[0] != '{') return null;
        try { return JsonNode.Parse(line) as JsonObject; } catch { return null; }
    }

    private static string Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static bool Bool(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static bool IsSidechain(JsonObject o) => Bool(o, "isSidechain");

    /// <summary>Texto que uma pessoa digitou (ignora resultado de ferramenta, comandos /x e avisos internos).</summary>
    private static string? UserText(JsonObject o)
    {
        var content = (o["message"] as JsonObject)?["content"];
        string? text = null;
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) text = s;
        else if (content is JsonArray arr)
        {
            if (arr.Any(x => x is JsonObject xo && Str(xo, "type") == "tool_result")) return null;
            text = string.Join("\n", arr.OfType<JsonObject>().Where(x => Str(x, "type") == "text").Select(x => Str(x, "text")));
        }
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith('<') || text.StartsWith("Caveat:", StringComparison.Ordinal)) return null;
        return text;
    }

    private static (string? Text, bool ToolUse) AssistantText(JsonObject? msg)
    {
        if (msg?["content"] is JsonValue v && v.TryGetValue<string>(out var s)) return (string.IsNullOrWhiteSpace(s) ? null : s.Trim(), false);
        if (msg?["content"] is not JsonArray arr) return (null, false);
        var toolUse = arr.Any(x => x is JsonObject xo && Str(xo, "type") == "tool_use");
        var text = string.Join("\n", arr.OfType<JsonObject>().Where(x => Str(x, "type") == "text").Select(x => Str(x, "text"))).Trim();
        return (text == "" ? null : text, toolUse);
    }

    private static string Clean(string s, int max)
    {
        s = Regex.Replace(s, @"[ \t]+", " ").Replace("\r", "").Trim();
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Length > max ? s[..(max - 1)].TrimEnd() + "…" : s;
    }

    // ---------- mandar mensagem ----------

    /// <summary>
    /// Manda o texto pra sessão em segundo plano e volta na hora. O andamento aparece na lista
    /// (estado "working" e depois a resposta do Claude como última mensagem).
    /// </summary>
    public object Send(JsonNode deck, string id, string text)
    {
        text = (text ?? "").Trim();
        if (text == "") throw new ArgumentException("Mensagem vazia.");
        if (!SafeId().IsMatch(id ?? "")) throw new ArgumentException("Sessão inválida.");
        var file = FindFile(id!) ?? throw new FileNotFoundException("Não achei essa sessão no PC.");
        string cwd;
        lock (gate) cwd = cache.TryGetValue(file, out var p) ? p.Cwd : "";
        if (cwd == "") cwd = Parse(new FileInfo(file))?.Cwd ?? "";
        if (cwd == "" || !Directory.Exists(cwd)) throw new DirectoryNotFoundException("A pasta dessa sessão não existe mais: " + cwd);

        var cfg = ReadConfig(deck);
        lock (gate)
        {
            if (running.ContainsKey(id!)) throw new InvalidOperationException("Essa sessão ainda está respondendo a última mensagem.");
            if (running.Count >= 4) throw new InvalidOperationException("Já tem 4 mensagens rodando. Espera uma terminar.");
            running[id!] = new Run { Text = Clean(text, 300) };
            errors.Remove(id!);
        }
        _ = Task.Run(() => RunClaudeAsync(cfg, id!, cwd, text));
        return new { started = true };
    }

    private static string? FindFile(string id)
    {
        foreach (var proj in SafeDirs(ProjectsDir()))
        {
            var f = Path.Combine(proj, id + ".jsonl");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    private async Task RunClaudeAsync(Config cfg, string id, string cwd, string text)
    {
        try
        {
            var args = $"claude --resume {id} -p --output-format text --permission-mode {cfg.PermissionMode}";
            if (cfg.AllowedTools != "") args += $" --allowedTools \"{cfg.AllowedTools}\"";
            var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{args}\"")
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não consegui iniciar o Claude Code.");
            await p.StandardInput.WriteAsync(text); // pela entrada padrão: sem problema com aspas e acentos
            p.StandardInput.Close();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(45));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* já saiu */ }
                throw new TimeoutException("Passou de 45 minutos e eu parei.");
            }
            var output = (await outTask).Trim();
            var error = (await errTask).Trim();
            if (p.ExitCode == 9009 || error.Contains("is not recognized", StringComparison.OrdinalIgnoreCase)
                                   || error.Contains("não é reconhecido", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Não achei o Claude Code (comando claude) no PC.");
            if (p.ExitCode != 0) throw new InvalidOperationException(Clean(error.Length > 0 ? error : output, 300));
        }
        catch (Exception ex)
        {
            lock (gate) errors[id] = (ex.Message, DateTimeOffset.Now);
        }
        finally
        {
            lock (gate) running.Remove(id);
        }
    }
}
