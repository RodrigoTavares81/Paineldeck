using System.Diagnostics;
using System.Text;

namespace PainelDeck;

/// <summary>
/// "✨ Pedir ao Claude": manda o seu pedido pro Claude Code instalado no PC (comando `claude`),
/// rodando dentro da pasta do PainelDeck. Ele lê o CLAUDE.md, edita deck.json / widgets.js / style.css / scripts
/// e o painel recarrega sozinho quando os arquivos mudam.
/// </summary>
internal static class ClaudeAgent
{
    public static async Task<string> AskAsync(string root, string request, string pageName)
    {
        if (string.IsNullOrWhiteSpace(request)) throw new ArgumentException("Escreva o que você quer.");

        var prompt = $"""
            Você está dentro da pasta do PainelDeck (Stream Deck caseiro). Leia o CLAUDE.md antes de tudo e siga as regras dele.

            O Rodrigo pediu, pelo próprio painel:
            "{request.Trim()}"

            Página aberta agora no painel: "{pageName}". Se o pedido for um item novo e ele não disser outra página, coloque nessa.
            Faça o que ele pediu editando os arquivos do projeto. NÃO rode o app, nem dotnet, nem scripts.
            No fim, responda em português, em 1 ou 2 frases curtas, o que você fez (vai aparecer num aviso no painel).
            """;

        // `claude` pode ser .exe (instalador nativo) ou .cmd (npm): o cmd.exe acha qualquer um dos dois no PATH.
        // O pedido vai pela entrada padrão, assim não tem problema com aspas e acentos.
        var psi = new ProcessStartInfo("cmd.exe",
            "/d /s /c \"claude -p --output-format text --permission-mode acceptEdits --allowedTools Read,Edit,Write,Glob,Grep\"")
        {
            WorkingDirectory = root,
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
        await p.StandardInput.WriteAsync(prompt);
        p.StandardInput.Close();

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* já saiu */ }
            throw new TimeoutException("O Claude demorou mais de 10 minutos e eu parei ele.");
        }

        var output = (await outTask).Trim();
        var error = (await errTask).Trim();
        if (p.ExitCode == 9009 || error.Contains("is not recognized", StringComparison.OrdinalIgnoreCase)
                               || error.Contains("não é reconhecido", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Não achei o Claude Code no PC. Instale (https://claude.com/claude-code), faça login uma vez com `claude` no terminal e tente de novo.");
        if (p.ExitCode != 0)
            throw new InvalidOperationException("O Claude Code deu erro: " + (error.Length > 0 ? error : output));
        return output.Length > 0 ? output : "Pronto!";
    }
}
