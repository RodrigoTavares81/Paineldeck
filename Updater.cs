using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PainelDeck;

/// <summary>
/// Atualização com um clique. Confere no GitHub se o "main" do repositório tem commit novo (a cada 6h) e, quando
/// você manda atualizar, roda scripts\atualizar.ps1 escondido: baixa, compila (dotnet publish), e só quando a
/// compilação deu certo pede pro app fechar; aí copia por cima (guardando backup e o que você mudou no PC) e abre de novo.
/// O script conta o progresso em .atualizacao\status.json, que esta classe lê e manda pro painel.
/// </summary>
internal sealed class Updater : IDisposable
{
    private static readonly HttpClient Http = CreateHttp();
    private readonly Settings settings;
    private readonly Action exitForInstall;
    private readonly System.Windows.Forms.Timer watch = new() { Interval = 1000 };
    private readonly System.Threading.Timer checkTimer;
    private Process? script;
    private bool exiting;

    /// <summary>idle | checking | current | available | working | error | done | dev</summary>
    public string State { get; private set; } = "idle";
    public string? Message { get; private set; }
    public string? Installed { get; private set; }
    public string? Latest { get; private set; }
    public string? LatestTitle { get; private set; }
    public DateTimeOffset? LatestDate { get; private set; }

    private string WorkDir => Path.Combine(settings.Root, ".atualizacao");
    private string StatusFile => Path.Combine(WorkDir, "status.json");

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("PainelDeck/1.0");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>Precisa ser criado na thread da janela (o timer que acompanha o script roda nela).</summary>
    public Updater(Settings settings, Action exitForInstall)
    {
        this.settings = settings;
        this.exitForInstall = exitForInstall;
        Installed = ReadInstalled();
        if (settings.DevMode) { State = "dev"; Message = "Rodando pelo código-fonte: atualize com git pull"; }
        ReadResult();
        watch.Tick += (_, _) => Watch();
        // 1ª conferida 40s depois de abrir (sem pesar a inicialização), depois a cada 6h
        checkTimer = new System.Threading.Timer(_ => _ = CheckAsync(), null,
            settings.UpdateCheck && !settings.DevMode ? TimeSpan.FromSeconds(40) : Timeout.InfiniteTimeSpan, TimeSpan.FromHours(6));
    }

    public object Status() => new
    {
        state = State,
        message = Message,
        installed = Short(Installed),
        latest = Short(Latest),
        title = LatestTitle,
        date = LatestDate?.ToString("o"),
    };

    /// <summary>Pergunta pro GitHub qual é o último commit do branch.</summary>
    public async Task CheckAsync()
    {
        if (settings.DevMode || State is "working" or "checking") return;
        var before = State;
        State = "checking";
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{settings.UpdateRepo}/commits/{Uri.EscapeDataString(settings.UpdateBranch)}");
            var node = JsonNode.Parse(json);
            Latest = (string?)node?["sha"];
            var msg = (string?)node?["commit"]?["message"] ?? "";
            LatestTitle = msg.Split('\n')[0].Trim();
            LatestDate = DateTimeOffset.TryParse((string?)node?["commit"]?["committer"]?["date"], out var d) ? d : null;
            var available = Latest != null && !string.Equals(Latest, Installed, StringComparison.OrdinalIgnoreCase);
            // "atualizado agora" fica na tela até aparecer coisa nova
            State = available ? "available" : before == "done" ? "done" : "current";
            if (State != "done") Message = available ? "Atualização disponível" : "Você está na última versão";
        }
        catch (Exception ex)
        {
            State = before is "available" or "done" ? before : "idle";
            Message = "Não consegui ver se tem atualização: " + ex.Message;
        }
    }

    /// <summary>Começa a atualização (o app fecha sozinho quando a versão nova estiver compilada).</summary>
    public void Start()
    {
        if (settings.DevMode) throw new InvalidOperationException("Rodando pelo código-fonte (dotnet run): atualize com git pull");
        if (State == "working") return;
        var src = Path.Combine(settings.ScriptsDir, "atualizar.ps1");
        if (!File.Exists(src)) throw new FileNotFoundException("Faltou o scripts\\atualizar.ps1");

        Directory.CreateDirectory(WorkDir);
        // roda uma cópia: o script substitui a pasta scripts\ no meio do caminho
        var copy = Path.Combine(WorkDir, "atualizar.ps1");
        File.Copy(src, copy, true);
        try { File.Delete(StatusFile); } catch { }

        static string Q(string s) => "\"" + s.TrimEnd('\\') + "\"";
        var args = $"-NoProfile -ExecutionPolicy Bypass -File {Q(copy)} -Root {Q(settings.Root)} -Repo {settings.UpdateRepo} " +
                   $"-Branch {settings.UpdateBranch} -WaitPid {Environment.ProcessId} -Exe {Q(Environment.ProcessPath!)}";
        script = Process.Start(new ProcessStartInfo("powershell.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true, // sem janela: não rouba o foco do jogo
            WorkingDirectory = WorkDir,
        });
        State = "working";
        Message = "Preparando…";
        exiting = false;
        watch.Start();
    }

    private void Watch()
    {
        string? step = null, msg = null;
        try
        {
            if (File.Exists(StatusFile))
            {
                var n = JsonNode.Parse(File.ReadAllText(StatusFile));
                step = (string?)n?["step"];
                msg = (string?)n?["message"];
            }
        }
        catch { /* o script está escrevendo: lê no próximo segundo */ }

        if (msg != null) Message = msg;
        if (step == "erro")
        {
            watch.Stop();
            State = "error";
            return;
        }
        if (step == "instalando" && !exiting)
        {
            exiting = true;
            Message = msg ?? "Instalando… o painel volta em instantes";
            // dá 1 segundo pro painel mostrar a mensagem antes de fechar
            var t = new System.Windows.Forms.Timer { Interval = 1200 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); exitForInstall(); };
            t.Start();
            return;
        }
        if (!exiting && script is { HasExited: true })
        {
            watch.Stop();
            State = "error";
            Message = "O atualizador parou sem terminar (veja .atualizacao\\atualizar.log)";
        }
    }

    /// <summary>version.json (escrito pelo atualizador): qual commit está instalado.</summary>
    private string? ReadInstalled()
    {
        try
        {
            var f = Path.Combine(settings.Root, "version.json");
            return File.Exists(f) ? (string?)JsonNode.Parse(File.ReadAllText(f))?["sha"] : null;
        }
        catch { return null; }
    }

    /// <summary>Resultado da última atualização (.atualizacao\result.json), mostrado uma vez depois de reabrir.</summary>
    private void ReadResult()
    {
        var f = Path.Combine(WorkDir, "result.json");
        try
        {
            if (!File.Exists(f)) return;
            var n = JsonNode.Parse(File.ReadAllText(f));
            File.Move(f, Path.Combine(WorkDir, "result.visto.json"), true);
            var kept = (n?["kept"] as JsonArray)?.Count ?? 0;
            var conflicts = (n?["conflicts"] as JsonArray)?.Count ?? 0;
            State = (bool?)n?["ok"] == false ? "error" : "done";
            Message = (string?)n?["message"] ?? "Atualizado";
            if (State == "done" && kept + conflicts > 0)
                Message += $" · {kept + conflicts} arquivo(s) que você mudou no PC foram mantidos" + (conflicts > 0 ? $" ({conflicts} com versão nova ao lado, .novo)" : "");
        }
        catch { }
    }

    private static string? Short(string? sha) => sha is { Length: >= 7 } ? sha[..7] : sha;

    public void Dispose()
    {
        watch.Stop();
        checkTimer.Dispose();
    }
}
