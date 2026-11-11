using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PainelDeck;

/// <summary>Executa as ações que vêm dos botões do painel (veja os tipos no topo do deck.json).</summary>
internal sealed class ActionRunner
{
    private readonly Settings settings;
    private readonly Mic mic;
    private readonly Action ensureTargetFocused;
    private readonly ObsClient obs;
    private readonly Deck deck;
    private readonly Recorder recorder;
    private const int MaxMacroDepth = 8;

    public ActionRunner(Settings settings, Deck deck, Mic mic, Recorder recorder, Action ensureTargetFocused)
    {
        this.settings = settings;
        this.deck = deck;
        this.mic = mic;
        this.recorder = recorder;
        this.ensureTargetFocused = ensureTargetFocused;
        obs = new ObsClient(settings.ObsUrl, settings.ObsPassword);
    }

    public bool ObsConnected => obs.IsConnected;

    public Task<object?> RunAsync(JsonElement a) => RunAsync(a, 0);

    private async Task<object?> RunAsync(JsonElement a, int depth)
    {
        var type = Str(a, "type");
        switch (type)
        {
            case "keys":
                ensureTargetFocused();
                Keyboard.Combo(Str(a, "keys"));
                return null;

            case "type":
                ensureTargetFocused();
                Keyboard.Type(Str(a, "text"));
                return null;

            case "media": // teclas de mídia são globais, não precisam de foco
                Keyboard.Combo(Str(a, "key"));
                return null;

            case "macro":
                if (depth >= MaxMacroDepth) throw new InvalidOperationException("Macro chamando macro demais (loop?)");
                JsonElement steps;
                if (Opt(a, "ref") is string name)
                    steps = deck.Macro(name) ?? throw new ArgumentException($"Macro '{name}' não existe em \"macros\" no deck.json");
                else if (!a.TryGetProperty("steps", out steps) || steps.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("Macro precisa de \"steps\" ou \"ref\"");
                foreach (var step in steps.EnumerateArray())
                {
                    if (!step.TryGetProperty("type", out _) && step.TryGetProperty("delay", out var d))
                        await Task.Delay(d.GetInt32());
                    else
                        await RunAsync(step, depth + 1);
                }
                return null;

            case "run":
                Process.Start(new ProcessStartInfo(Str(a, "path"))
                {
                    Arguments = Opt(a, "args") ?? "",
                    WorkingDirectory = Opt(a, "cwd") ?? "",
                    UseShellExecute = true,
                });
                return null;

            case "url":
                Process.Start(new ProcessStartInfo(Str(a, "url")) { UseShellExecute = true });
                return null;

            case "script":
                RunScript(Str(a, "file"), Opt(a, "args") ?? "", !(a.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.False));
                return null;

            case "mic":
                return new { muted = mic.Set(Opt(a, "mode") ?? "toggle") };

            case "obs":
                JsonNode? data = a.TryGetProperty("data", out var dataEl) ? JsonNode.Parse(dataEl.GetRawText()) : null;
                return await obs.RequestAsync(Str(a, "request"), data);

            case "record": // modo gravação: toggle | start | stop | pause | mark
                return await recorder.CommandAsync(Opt(a, "cmd") ?? "toggle");

            default:
                throw new InvalidOperationException($"Tipo de ação desconhecido: '{type}'");
        }
    }

    private void RunScript(string file, string args, bool hidden)
    {
        var path = Path.IsPathRooted(file) ? file : Path.Combine(settings.ScriptsDir, file);
        if (!File.Exists(path)) throw new FileNotFoundException($"Script não encontrado: {path}");

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var psi = ext switch
        {
            ".ps1" => new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass {(hidden ? "-WindowStyle Hidden " : "")}-File \"{path}\" {args}"),
            ".bat" or ".cmd" => new ProcessStartInfo("cmd.exe", $"/c \"\"{path}\" {args}\""),
            ".py" => new ProcessStartInfo("python", $"\"{path}\" {args}"),
            ".js" => new ProcessStartInfo("node", $"\"{path}\" {args}"),
            _ => new ProcessStartInfo(path, args) { UseShellExecute = true },
        };
        if (!psi.UseShellExecute) psi.CreateNoWindow = hidden;
        psi.WorkingDirectory = Path.GetDirectoryName(path)!;
        Process.Start(psi);
    }

    private static string Str(JsonElement a, string name) =>
        Opt(a, name) ?? throw new ArgumentException($"Ação sem o campo '{name}'");

    private static string? Opt(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
