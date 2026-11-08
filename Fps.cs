using System.Diagnostics;
using System.Globalization;

namespace PainelDeck;

internal sealed record FpsReading(double? Fps, double? Low1, double? Avg, string? Info);

/// <summary>
/// FPS do jogo sem injetar nada nele: usa o PresentMon (ferramenta oficial da Intel, open source) que lê os eventos
/// de "present" do Windows (ETW), o mesmo que o Xbox Game Bar e o CapFrameX usam. Funciona com qualquer placa.
/// O PresentMon precisa estar em tools/ (scripts/ativar-fps.ps1 baixa e dá a permissão, uma vez só).
/// Roda escondido (sem janela, não rouba foco) e fecha sozinho quando o jogo fecha.
/// </summary>
internal sealed class Fps : IDisposable
{
    private const long WindowMs = 10_000;
    private readonly Settings settings;
    private readonly object gate = new();
    private readonly Dictionary<string, Queue<(long T, double Ms)>> chains = new();
    private Process? proc;
    private string? info, lastErr;
    private int msCol = -1, chainCol = -1;

    public Fps(Settings settings) => this.settings = settings;

    public void Start(uint pid, string exe)
    {
        Stop();
        lock (gate)
        {
            chains.Clear();
            msCol = chainCol = -1;
            lastErr = null;
            if (!settings.FpsEnabled) { info = "FPS desligado no settings.json"; return; }
            var tool = FindPresentMon();
            if (tool == null) { info = "Pra ver o FPS, rode scripts\\ativar-fps.ps1 uma vez"; return; }

            var args = settings.FpsArgs.Replace("{pid}", pid.ToString(CultureInfo.InvariantCulture)).Replace("{exe}", exe);
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(tool, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(tool)!,
                },
                EnableRaisingEvents = true,
            };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(p, e.Data); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lock (gate) lastErr = e.Data.Trim(); };
            p.Exited += (_, _) =>
            {
                lock (gate)
                {
                    if (proc != p) return;
                    proc = null;
                    info = Explain(lastErr);
                }
            };
            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                proc = p;
                info = "medindo…";
            }
            catch (Exception ex)
            {
                info = "Não consegui abrir o PresentMon: " + ex.Message;
                p.Dispose();
            }
        }
    }

    public void Stop()
    {
        Process? p;
        lock (gate) { p = proc; proc = null; chains.Clear(); info = null; }
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(); } catch { }
        p.Dispose();
    }

    /// <summary>FPS do último segundo, 1% low e média dos últimos 10s (null = sem leitura; Info diz o porquê).</summary>
    public FpsReading Read()
    {
        lock (gate)
        {
            var now = Environment.TickCount64;
            foreach (var q in chains.Values)
                while (q.Count > 0 && now - q.Peek().T > WindowMs) q.Dequeue();
            // o jogo pode ter mais de uma "swap chain" (overlay do Discord/Steam): fica com a que mais desenha
            var best = chains.Values.OrderByDescending(q => q.Count(f => now - f.T <= 1000)).FirstOrDefault();
            var last = best?.Where(f => now - f.T <= 1000).Select(f => f.Ms).ToArray() ?? Array.Empty<double>();
            if (last.Length == 0)
                return new FpsReading(null, null, null, proc != null && best != null ? "sem quadros agora (jogo minimizado?)" : info);
            var all = best!.Select(f => f.Ms).OrderBy(x => x).ToArray();
            // 1% low = o FPS do quadro mais lento entre os 1% piores (percentil 99 do tempo de quadro)
            double? low = all.Length >= 30 ? Math.Round(1000 / all[Math.Min(all.Length - 1, (int)(all.Length * 0.99))]) : null;
            return new FpsReading(Math.Round(1000 / last.Average()), low, Math.Round(1000 / all.Average()), null);
        }
    }

    /// <summary>Uma linha do CSV do PresentMon (1.x ou 2.x: acha as colunas pelo cabeçalho).</summary>
    private void OnLine(Process p, string line)
    {
        var f = line.Split(',');
        lock (gate)
        {
            if (proc != p) return;
            if (msCol < 0)
            {
                // PresentMon 1.x e 2.x: "MsBetweenPresents"; versões novas também têm "FrameTime"
                msCol = Find(f, "MsBetweenPresents", "msBetweenPresents", "FrameTime");
                chainCol = Find(f, "SwapChainAddress");
                return;
            }
            if (msCol >= f.Length || !double.TryParse(f[msCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms <= 0 || ms > 5000) return;
            var key = chainCol >= 0 && chainCol < f.Length ? f[chainCol] : "";
            if (!chains.TryGetValue(key, out var q)) chains[key] = q = new Queue<(long, double)>();
            q.Enqueue((Environment.TickCount64, ms));
            if (q.Count > 20000) q.Dequeue(); // 2000 FPS por 10s: mais que isso, corta
        }
    }

    private static int Find(string[] header, params string[] names)
    {
        foreach (var n in names)
        {
            var i = Array.FindIndex(header, h => string.Equals(h.Trim(), n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return -1;
    }

    private static string Explain(string? err)
    {
        if (err != null && (err.Contains("Performance Log Users", StringComparison.OrdinalIgnoreCase)
            || err.Contains("administrat", StringComparison.OrdinalIgnoreCase) || err.Contains("access", StringComparison.OrdinalIgnoreCase)))
            return "O PresentMon precisa de permissão: rode scripts\\ativar-fps.ps1 e saia/entre no Windows";
        return "O PresentMon parou" + (err != null ? ": " + err : "");
    }

    /// <summary>"fps.presentMon" do settings.json, ou o PresentMon*.exe mais novo da pasta tools/.</summary>
    private string? FindPresentMon()
    {
        if (settings.FpsPresentMon is { Length: > 0 } custom)
        {
            var path = Path.IsPathRooted(custom) ? custom : Path.Combine(settings.Root, custom);
            return File.Exists(path) ? path : null;
        }
        var tools = Path.Combine(settings.Root, "tools");
        if (!Directory.Exists(tools)) return null;
        return Directory.GetFiles(tools, "PresentMon*.exe").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).LastOrDefault();
    }

    public void Dispose() => Stop();
}
