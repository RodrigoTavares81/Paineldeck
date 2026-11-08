namespace PainelDeck;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "PainelDeck.SingleInstance", out bool first);
        if (!first) return; // já está rodando

        ApplicationConfiguration.Initialize();
        Application.Run(new PanelForm());
    }
}
