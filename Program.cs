namespace PainelDeck;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // ajudante de sensores (rodando como admin pela tarefa agendada): veja Sensors.cs
        if (args.Contains("--sensors")) { Sensors.Run(); return; }

        Crash.WaitForPrevious(args); // reiniciando (crash, atualização, bandeja): espera a instância antiga sair

        using var mutex = new Mutex(true, "PainelDeck.SingleInstance", out bool first);
        if (!first) return; // já está rodando

        var settings = Settings.Load();
        Crash.Install(settings.Root, settings.AutoRestart);
        ApplicationConfiguration.Initialize();
        Application.Run(new PanelForm(settings));

        // janela fechada (Sair, reiniciar, atualizar): termina o processo mesmo se sobrou alguma thread rodando
        // (OBS, sensores, notícias...), senão o PainelDeck ficava vivo escondido e a instância nova não abria
        Crash.Exiting = true;
        try { mutex.ReleaseMutex(); } catch { }
        mutex.Dispose();
        Environment.Exit(0);
    }
}
