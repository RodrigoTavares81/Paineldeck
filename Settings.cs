using System.Text.Json;
using System.Text.Json.Nodes;

namespace PainelDeck;

internal sealed class Settings
{
    public string Root { get; }
    public string WebRoot => Path.Combine(Root, "wwwroot");
    public string ScriptsDir => Path.Combine(Root, "scripts");
    public string Monitor { get; private set; } = "auto";
    public string ObsUrl { get; private set; } = "ws://127.0.0.1:4455";
    public string ObsPassword { get; private set; } = "";
    /// <summary>Identidade do monitor do painel ("panelMonitor" no settings.json). null = ainda não escolhido.</summary>
    public MonitorInfo? PanelMonitor { get; private set; }
    /// <summary>Atalhos globais, nome → teclas. Ex.: "rest" → "ctrl+alt+h"; "record" começa/para a gravação no OBS; "replay" liga/salva o replay. "" desliga o atalho.</summary>
    public Dictionary<string, string> Hotkeys { get; } = new() { ["rest"] = "ctrl+alt+h", ["record"] = "ctrl+alt+g", ["replay"] = "ctrl+alt+r" };
    /// <summary>Cena do painel ("panelScene"): "classic" = wwwroot/index.html, outra = wwwroot/scenes/&lt;nome&gt;/index.html.</summary>
    public string PanelScene { get; private set; } = "classic";
    public string PanelUrl => PanelScene == "classic" ? "https://painel.local/index.html" : $"https://painel.local/scenes/{PanelScene}/index.html";

    /// <summary>FPS pelo PresentMon ("fps" no settings.json): liga/desliga, caminho do .exe e argumentos ({pid}, {exe}).</summary>
    public bool FpsEnabled { get; private set; } = true;
    public string FpsPresentMon { get; private set; } = "";
    public string FpsArgs { get; private set; } = DefaultFpsArgs;
    public const string DefaultFpsArgs = "--process_id {pid} --output_stdout --stop_existing_session --session_name PainelDeckFps --terminate_on_proc_exit";
    /// <summary>Se o app der erro grave, abre de novo sozinho ("autoRestart", padrão true; no máximo 3x em 10 min).</summary>
    public bool AutoRestart { get; private set; } = true;
    /// <summary>De onde vem a atualização ("update": { "repo", "branch", "check" }).</summary>
    public string UpdateRepo { get; private set; } = "RodrigoTavares81/paineldeck";
    public string UpdateBranch { get; private set; } = "main";
    public bool UpdateCheck { get; private set; } = true;
    /// <summary>Rodando pelo código-fonte (dotnet run): a pasta tem o .csproj. Aí atualização é com git, não pelo botão.</summary>
    public bool DevMode => File.Exists(Path.Combine(Root, "PainelDeck.csproj"));

    private string FilePath => Path.Combine(Root, "settings.json");
    private static readonly JsonDocumentOptions DocOpts = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private Settings(string root) => Root = root;

    public static Settings Load()
    {
        var s = new Settings(FindRoot());
        var node = s.ReadNode();
        if (node["monitor"] is JsonValue mv)
            s.Monitor = mv.TryGetValue<string>(out var str) ? str : mv.TryGetValue<int>(out var i) ? i.ToString() : "auto";
        s.ObsUrl = node["obs"]?["url"]?.GetValue<string>() ?? s.ObsUrl;
        s.ObsPassword = node["obs"]?["password"]?.GetValue<string>() ?? "";
        if (node["panelMonitor"] is JsonObject pm)
            s.PanelMonitor = new MonitorInfo("", Str(pm, "name"), Str(pm, "edid"), Str(pm, "path"));
        if (node["hotkeys"] is JsonObject hk)
            foreach (var (k, v) in hk)
                if (v is JsonValue jv && jv.TryGetValue<string>(out var keys)) s.Hotkeys[k] = keys;
        if (node["fps"] is JsonObject fps)
        {
            if (fps["enabled"] is JsonValue fe && fe.TryGetValue<bool>(out var on)) s.FpsEnabled = on;
            s.FpsPresentMon = Str(fps, "presentMon");
            if (Str(fps, "args") is { Length: > 0 } fa) s.FpsArgs = fa;
        }
        if (node["autoRestart"] is JsonValue ar && ar.TryGetValue<bool>(out var restart)) s.AutoRestart = restart;
        if (node["update"] is JsonObject up)
        {
            if (Str(up, "repo") is { Length: > 0 } repo && System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$")) s.UpdateRepo = repo;
            if (Str(up, "branch") is { Length: > 0 } br && System.Text.RegularExpressions.Regex.IsMatch(br, @"^[\w./-]+$")) s.UpdateBranch = br;
            if (up["check"] is JsonValue uc && uc.TryGetValue<bool>(out var chk)) s.UpdateCheck = chk;
        }
        if (node["panelScene"] is JsonValue sc && sc.TryGetValue<string>(out var scene) && IsScene(s.WebRoot, scene)) s.PanelScene = scene;
        return s;
    }

    /// <summary>"classic" ou uma pasta que existe em wwwroot/scenes (e só letras/números: vira pedaço de URL).</summary>
    public static bool IsScene(string webRoot, string name) =>
        name == "classic" || (name.All(char.IsAsciiLetterOrDigit) && name != "" && File.Exists(Path.Combine(webRoot, "scenes", name, "index.html")));

    public void SavePanelScene(string scene)
    {
        PanelScene = scene;
        Write(node => node["panelScene"] = scene);
    }

    private static string Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>
    /// Tela do painel. Com "panelMonitor" salvo, só devolve a tela onde ESSE monitor físico está (caminho do
    /// dispositivo → EDID → nome); se ele não estiver ligado, devolve null e o painel se esconde em vez de cair em outro.
    /// Sem identidade salva (1ª vez): escolhe pelo jeito antigo e grava a identidade.
    /// </summary>
    public Screen? PickScreen()
    {
        var mons = DisplayConfig.Active();
        if (PanelMonitor is { } want)
        {
            var m = mons.FirstOrDefault(x => want.Path != "" && string.Equals(x.Path, want.Path, StringComparison.OrdinalIgnoreCase))
                 ?? mons.FirstOrDefault(x => want.Edid != "" && x.Edid == want.Edid)
                 ?? mons.FirstOrDefault(x => want.Edid == "" && want.Name != "" && x.Name == want.Name);
            return m == null ? null : Screen.AllScreens.FirstOrDefault(s => s.DeviceName == m.GdiName);
        }
        var pick = LegacyPick();
        var info = mons.FirstOrDefault(x => x.GdiName == pick.DeviceName);
        if (info != null) SavePanelMonitor(info);
        return pick;
    }

    /// <summary>Monitor físico que está numa tela agora (pra mostrar o nome no menu da bandeja).</summary>
    public static MonitorInfo? MonitorOf(Screen s) => DisplayConfig.Active().FirstOrDefault(x => x.GdiName == s.DeviceName);

    public void SavePanelMonitor(MonitorInfo m)
    {
        PanelMonitor = m;
        Write(node => node["panelMonitor"] = new JsonObject { ["name"] = m.Name, ["edid"] = m.Edid, ["path"] = m.Path });
    }

    private void Write(Action<JsonObject> change)
    {
        var node = ReadNode();
        change(node);
        File.WriteAllText(FilePath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Jeito antigo ("monitor": nome da tela, número ou "auto" = menor tela que não é a principal). Só na 1ª vez.</summary>
    private Screen LegacyPick()
    {
        var screens = Screen.AllScreens;
        var byName = screens.FirstOrDefault(x => string.Equals(x.DeviceName, Monitor, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;
        if (int.TryParse(Monitor, out var idx) && idx >= 1 && idx <= screens.Length) return screens[idx - 1];

        var others = screens.Where(x => !x.Primary).ToArray();
        if (others.Length == 0) return Screen.PrimaryScreen!;
        return others.OrderBy(x => x.Bounds.Width * x.Bounds.Height).ThenByDescending(x => x.Bounds.X).First();
    }

    private JsonObject ReadNode()
    {
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath), null, DocOpts) is JsonObject o) return o;
        }
        catch { /* json inválido: usa padrão */ }
        return new JsonObject();
    }

    /// <summary>Em dev (dotnet run) usa a pasta do projeto, assim editar o wwwroot recarrega na hora.</summary>
    internal static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PainelDeck.csproj"))) return dir.FullName;
        return AppContext.BaseDirectory;
    }
}
