using static PainelDeck.Native;

namespace PainelDeck;

internal sealed record RamInfo(double Percent, double UsedGb, double TotalGb);

/// <summary>Uso de CPU e RAM sem precisar de admin nem de pacote extra.</summary>
internal sealed class Stats
{
    private long lastIdle, lastKernel, lastUser;

    public Stats() => GetSystemTimes(out lastIdle, out lastKernel, out lastUser);

    public double Cpu()
    {
        GetSystemTimes(out var idle, out var kernel, out var user);
        long di = idle - lastIdle, dk = kernel - lastKernel, du = user - lastUser;
        (lastIdle, lastKernel, lastUser) = (idle, kernel, user);
        long total = dk + du; // kernel já inclui idle
        return total <= 0 ? 0 : Math.Round(100.0 * (total - di) / total, 1);
    }

    public RamInfo Ram()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        const double gb = 1024d * 1024 * 1024;
        double total = m.ullTotalPhys / gb, used = (m.ullTotalPhys - m.ullAvailPhys) / gb;
        return new RamInfo(m.dwMemoryLoad, Math.Round(used, 1), Math.Round(total, 1));
    }

    private GpuInfo? gpu;
    private int gpuBusy;
    private long gpuNext;

    /// <summary>Último valor lido da GPU (null sem placa NVIDIA). A leitura roda em segundo plano a cada 2s.</summary>
    // shortcut: só NVIDIA (nvidia-smi); pra AMD/Intel, trocar pelos contadores PDH "GPU Adapter Memory".
    public GpuInfo? Gpu()
    {
        if (Environment.TickCount64 >= gpuNext && Interlocked.Exchange(ref gpuBusy, 1) == 0)
        {
            gpuNext = Environment.TickCount64 + 2000;
            Task.Run(() => { try { gpu = QueryNvidia(); } catch { gpu = null; } finally { gpuBusy = 0; } });
        }
        return gpu;
    }

    private static GpuInfo? QueryNvidia()
    {
        var exe = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(exe)) return null;
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe,
            "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu,name --format=csv,noheader,nounits")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        var line = p.StandardOutput.ReadLine();
        p.WaitForExit(3000);
        return ParseNvidia(line);
    }

    /// <summary>"8, 2504, 12227, 47, NVIDIA GeForce RTX 5070" (MiB) → GpuInfo. Público pra dar pra testar sem placa.</summary>
    public static GpuInfo? ParseNvidia(string? line)
    {
        var f = line?.Split(',').Select(s => double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
        if (f is not { Length: >= 4 } || f.Take(4).Any(double.IsNaN) || f[2] <= 0) return null;
        const double gib = 1024;
        var name = string.Join(",", line!.Split(',').Skip(4)).Trim();
        return new GpuInfo(f[0], Math.Round(100 * f[1] / f[2], 1), Math.Round(f[1] / gib, 1), Math.Round(f[2] / gib, 1), f[3], name == "" ? null : name);
    }
}

internal sealed record GpuInfo(double Usage, double VramPercent, double VramUsedGb, double VramTotalGb, double TempC, string? Name);
