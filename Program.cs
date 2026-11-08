namespace PainelDeck;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Crash.WaitForPrevious(args); // reiniciando (crash, atualização, bandeja): espera a instância antiga sair

        using var mutex = new Mutex(true, "PainelDeck.SingleInstance", out bool first);
        if (!first) return; // já está rodando

        var settings = Settings.Load();
        Crash.Install(settings.Root, settings.AutoRestart);
        ApplicationConfiguration.Initialize();
        Application.Run(new PanelForm(settings));
    }
}
