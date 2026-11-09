using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PainelDeck;

/// <summary>
/// Dados do PC pro painel, sem admin: tempo ligado, rede, disco e temperatura da CPU.
/// Rede e disco são taxas (precisam de duas leituras): Read() é chamado a cada 1s pelo timer do status.
/// </summary>
internal sealed class SysInfo : IDisposable
{
    private readonly Pdh pdh = new();
    private readonly IntPtr diskRead, diskWrite, diskIdle, thermal, thermalHp;

    public SysInfo()
    {
        diskRead = pdh.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        diskWrite = pdh.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        diskIdle = pdh.Add(@"\PhysicalDisk(_Total)\% Idle Time");
        // zonas térmicas do ACPI: o único sensor de temperatura que o Windows expõe sem admin.
        // Em muitos PCs é a temperatura da CPU (ou bem perto); em algumas placas não existe ou fica parada.
        thermalHp = pdh.Add(@"\Thermal Zone Information(*)\High Precision Temperature"); // décimos de Kelvin
        thermal = pdh.Add(@"\Thermal Zone Information(*)\Temperature");                  // Kelvin
        pdh.Collect();
    }

    // ---------- tempo ligado ----------
    // TickCount64 = desde o último boot (com a "inicialização rápida" do Windows, desligar não zera; reiniciar zera)
    public static long UptimeSec => Environment.TickCount64 / 1000;

    // ---------- rede ----------
    private long netRx, netTx, netAt;
    private NetworkInterface[]? nics;
    private long nicsAt;

    private (double down, double up)? Net()
    {
        var now = Environment.TickCount64;
        if (nics == null || now - nicsAt > 30000)
        {
            nicsAt = now;
            nics = NetworkInterface.GetAllNetworkInterfaces().Where(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
                // switch virtual do Hyper-V/WSL repete o tráfego da placa de verdade
                !n.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase) &&
                !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                !n.Description.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) &&
                !n.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        long rx = 0, tx = 0;
        foreach (var n in nics)
        {
            try { var s = n.GetIPStatistics(); rx += s.BytesReceived; tx += s.BytesSent; } catch { }
        }
        var dt = (now - netAt) / 1000.0;
        (double, double)? r = netAt == 0 || dt <= 0 || rx < netRx || tx < netTx ? null : ((rx - netRx) / dt, (tx - netTx) / dt);
        (netRx, netTx, netAt) = (rx, tx, now);
        return r;
    }

    // ---------- discos ----------
    private object[]? drives;
    private long drivesAt;

    private object[] Drives()
    {
        if (drives != null && Environment.TickCount64 - drivesAt < 30000) return drives;
        drivesAt = Environment.TickCount64;
        const double gb = 1024d * 1024 * 1024;
        drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d =>
            {
                try { return (object)new { name = d.Name.TrimEnd('\\'), label = d.VolumeLabel, freeGb = Math.Round(d.AvailableFreeSpace / gb, 1), totalGb = Math.Round(d.TotalSize / gb, 1) }; }
                catch { return null!; }
            })
            .Where(d => d != null).Take(4).ToArray();
        return drives;
    }

    // ---------- temperatura da CPU ----------
    // 1º LibreHardwareMonitor/OpenHardwareMonitor abertos (sensor de verdade, via WMI);
    // 2º zona térmica do ACPI (aproximado). Sem nenhum: null.
    private double? lhmTemp;
    private string? lhmSrc;
    private int lhmBusy;
    private long lhmNext;

    private (double temp, string src)? CpuTemp()
    {
        if (Environment.TickCount64 >= lhmNext && Interlocked.Exchange(ref lhmBusy, 1) == 0)
        {
            Task.Run(() =>
            {
                try
                {
                    (lhmTemp, lhmSrc) = QueryHardwareMonitor();
                    // não achou: tenta de novo daqui a 1 min (o programa pode abrir depois); achou: a cada 3s
                    lhmNext = Environment.TickCount64 + (lhmTemp == null ? 60000 : 3000);
                }
                catch { lhmTemp = null; lhmNext = Environment.TickCount64 + 60000; }
                finally { lhmBusy = 0; }
            });
        }
        if (lhmTemp is double t) return (Math.Round(t), lhmSrc ?? "sensor");

        var hp = pdh.Array(thermalHp);
        var k = hp.Count > 0 ? hp.Select(v => v / 10).ToList() : pdh.Array(thermal);
        var c = k.Select(v => v - 273.15).Where(v => v is > 5 and < 120).ToList();
        return c.Count > 0 ? (Math.Round(c.Max()), "acpi") : null;
    }

    private static readonly string[] CpuSensorNames = { "CPU Package", "Core (Tctl/Tdie)", "CPU (Tctl/Tdie)", "Tctl/Tdie", "Core Max", "Core Average", "CPU Cores" };

    private static (double?, string?) QueryHardwareMonitor()
    {
        foreach (var ns in new[] { "LibreHardwareMonitor", "OpenHardwareMonitor" })
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher($@"root\{ns}",
                    "SELECT Name, Value, Parent FROM Sensor WHERE SensorType = 'Temperature'");
                var cpu = new List<(string name, double value)>();
                foreach (var o in searcher.Get())
                {
                    var parent = o["Parent"] as string ?? "";
                    if (!parent.Contains("cpu", StringComparison.OrdinalIgnoreCase)) continue; // /amdcpu/0, /intelcpu/0
                    cpu.Add((o["Name"] as string ?? "", Convert.ToDouble(o["Value"])));
                }
                if (cpu.Count == 0) continue;
                foreach (var want in CpuSensorNames)
                {
                    var hit = cpu.FirstOrDefault(s => s.name.Equals(want, StringComparison.OrdinalIgnoreCase));
                    if (hit.name != null && hit.value > 0) return (hit.value, ns);
                }
                var max = cpu.Max(s => s.value);
                if (max > 0) return (max, ns);
            }
            catch { /* namespace não existe: programa fechado ou não instalado */ }
        }
        return (null, null);
    }

    /// <summary>Uma leitura de tudo (chamar 1x por segundo).</summary>
    public object Read()
    {
        pdh.Collect();
        var net = Net();
        double? read = pdh.Value(diskRead), write = pdh.Value(diskWrite), idle = pdh.Value(diskIdle);
        var temp = CpuTemp();
        return new
        {
            uptimeSec = UptimeSec,
            cpuTemp = temp?.temp,
            cpuTempSrc = temp?.src,
            net = net is { } n ? new { down = Math.Round(n.down), up = Math.Round(n.up) } : null,
            disk = read != null || write != null
                ? new { read = Math.Round(read ?? 0), write = Math.Round(write ?? 0), busy = idle is double i ? Math.Round(Math.Clamp(100 - i, 0, 100)) : (double?)null }
                : null,
            drives = Drives(),
        };
    }

    public void Dispose() => pdh.Dispose();
}

/// <summary>Contadores de desempenho do Windows (PDH) sem pacote extra. Contador que não existe vira IntPtr.Zero e lê null.</summary>
internal sealed class Pdh : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000, PDH_MORE_DATA = 0x800007D2;
    private readonly IntPtr query;

    [StructLayout(LayoutKind.Explicit)]
    private struct FmtValue { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double Double; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FmtValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);

    public Pdh() { try { PdhOpenQueryW(null, IntPtr.Zero, out query); } catch { query = IntPtr.Zero; } }

    public IntPtr Add(string path)
    {
        if (query == IntPtr.Zero) return IntPtr.Zero;
        try { return PdhAddEnglishCounterW(query, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }

    public void Collect() { if (query != IntPtr.Zero) PdhCollectQueryData(query); }

    public double? Value(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        return PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) == 0 && v.CStatus <= 1 ? v.Double : null;
    }

    /// <summary>Contador com "(*)": um valor por instância.</summary>
    public List<double> Array(IntPtr counter)
    {
        var list = new List<double>();
        if (counter == IntPtr.Zero) return list;
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero) != PDH_MORE_DATA || size == 0) return list;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out var count, buf) != 0) return list;
            // PDH_FMT_COUNTERVALUE_ITEM_W = { wchar* nome; PDH_FMT_COUNTERVALUE valor (16 bytes, alinhado em 8) } = 24 bytes
            const int item = 24;
            for (int i = 0; i < count; i++)
            {
                var p = buf + i * item + 8;
                if ((uint)Marshal.ReadInt32(p) <= 1) list.Add(BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p + 8)));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public void Dispose() { if (query != IntPtr.Zero) PdhCloseQuery(query); }
}
