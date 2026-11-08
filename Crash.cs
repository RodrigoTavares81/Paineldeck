using System.Diagnostics;

namespace PainelDeck;

/// <summary>
/// Se o app der um erro grave, grava em logs\crash.log e abre de novo sozinho (sem janela de erro na frente do jogo).
/// Pra não ficar em loop: no máximo 3 reinícios em 10 minutos. Desliga com "autoRestart": false no settings.json.
/// </summary>
internal static class Crash
{
    private const int MaxRestarts = 3;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
    private static bool autoRestart = true;
    private static int handling;

    /// <summary>Limpeza rápida antes de sair num crash (ícone da bandeja, PresentMon...).</summary>
    public static event Action? BeforeExit;

    public static void Install(string root, bool restart)
    {
        logDir = Path.Combine(root, "logs");
        autoRestart = restart;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Fatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("tarefa em segundo plano", e.Exception); e.SetObserved(); };
    }

    /// <summary>Espera a instância anterior fechar ("--wait-pid N", usado ao reiniciar e pelo atualizador).</summary>
    public static void WaitForPrevious(string[] args)
    {
        var i = Array.IndexOf(args, "--wait-pid");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(20_000);
        }
        catch { /* já fechou */ }
    }

    public static void Fatal(Exception? ex)
    {
        if (Interlocked.Exchange(ref handling, 1) == 1) return;
        Log("erro grave", ex);
        try { BeforeExit?.Invoke(); } catch { }
        if (autoRestart && AllowRestart()) Relaunch();
        Environment.Exit(1);
    }

    /// <summary>Abre uma instância nova, que espera esta fechar antes de começar.</summary>
    public static void Relaunch()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--wait-pid {Environment.ProcessId}")
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (Exception e) { Log("não consegui reabrir", e); }
    }

    /// <summary>Guarda os horários dos reinícios em logs\restarts.txt; recusa se já foram 3 nos últimos 10 min.</summary>
    public static bool AllowRestart()
    {
        try
        {
            Directory.CreateDirectory(logDir);
            var file = Path.Combine(logDir, "restarts.txt");
            var now = DateTimeOffset.Now;
            var recent = (File.Exists(file) ? File.ReadAllLines(file) : Array.Empty<string>())
                .Select(l => DateTimeOffset.TryParse(l, out var d) ? d : (DateTimeOffset?)null)
                .Where(d => d != null && now - d < Window).Select(d => d!.Value).ToList();
            if (recent.Count >= MaxRestarts) { Log($"não reabri: já foram {MaxRestarts} reinícios em 10 min", null); return false; }
            recent.Add(now);
            File.WriteAllLines(file, recent.Select(d => d.ToString("o")));
            return true;
        }
        catch { return true; }
    }

    public static void Log(string what, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(logDir);
            var file = Path.Combine(logDir, "crash.log");
            if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Move(file, file + ".old", true);
            File.AppendAllText(file, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {what}{(ex != null ? ": " + ex : "")}{Environment.NewLine}");
        }
        catch { }
    }
}

/// <summary>"Iniciar com o Windows": um valor em HKCU\...\Run (só do seu usuário, sem admin).</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "PainelDeck";

    private static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    /// <summary>Ligado E apontando pra ESTE PainelDeck.exe (uma cópia antiga em outra pasta conta como desligado).</summary>
    public static bool Enabled
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string v && string.Equals(v, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool on)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, Command);
        else k.DeleteValue(Name, false);
    }
}
