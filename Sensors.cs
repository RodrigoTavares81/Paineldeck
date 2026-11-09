using System.Text.Json;
using LibreHardwareMonitor.Hardware;

namespace PainelDeck;

/// <summary>
/// Ajudante de sensores: o próprio PainelDeck.exe rodando com "--sensors", como administrador, por uma tarefa
/// agendada no logon (scripts\instalar-sensores.ps1 cria; pede admin uma vez só). O Windows só deixa ler o sensor
/// de verdade da CPU com admin + driver (PawnIO), e o painel não roda como admin, então o ajudante lê com a
/// LibreHardwareMonitorLib e grava em %ProgramData%\PainelDeck\Sensores\sensores.json a cada 2s; o painel só lê o arquivo.
/// O ajudante roda de uma cópia em %ProgramData%\PainelDeck\Sensores\app pra não travar os arquivos na atualização.
/// </summary>
internal static class Sensors
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PainelDeck", "Sensores");
    public static string DataFile => Path.Combine(Dir, "sensores.json");

    private static readonly string[] TempNames = { "CPU Package", "Core (Tctl/Tdie)", "CPU (Tctl/Tdie)", "Tctl/Tdie", "Tdie", "Tctl", "Core Max", "Core Average" };

    // ---------- lado do ajudante (admin) ----------
    public static void Run()
    {
        using var single = new Mutex(true, "Global\\PainelDeck.Sensores", out bool first);
        if (!first) return;
        Directory.CreateDirectory(Dir);
        var log = Path.Combine(Dir, "sensores.log");
        void Log(string m) { try { File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {m}{Environment.NewLine}"); } catch { } }
        if (File.Exists(log) && new FileInfo(log).Length > 256 * 1024) try { File.Delete(log); } catch { }

        var computer = new Computer { IsCpuEnabled = true };
        try { computer.Open(); }
        catch (Exception e) { Log("não abriu os sensores: " + e.Message); }
        Log("ajudante iniciado");

        string? lastError = null;
        while (true)
        {
            object data;
            try
            {
                var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                if (cpu == null) throw new InvalidOperationException("nenhuma CPU encontrada");
                cpu.Update();
                var all = cpu.Sensors.Concat(cpu.SubHardware.SelectMany(s => { s.Update(); return s.Sensors; })).ToList();
                var temps = all.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 125).ToList();
                ISensor? temp = null;
                foreach (var n in TempNames)
                {
                    temp = temps.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                    if (temp != null) break;
                }
                temp ??= temps.OrderByDescending(s => s.Value).FirstOrDefault();
                var power = all.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))?.Value;
                var clocks = all.Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase) && s.Value > 0).Select(s => (double)s.Value!.Value).ToList();
                // sem temperatura nenhuma quase sempre é o driver PawnIO faltando
                var error = temp == null ? (PawnIoInstalled() ? "a CPU não informou temperatura" : "driver PawnIO não instalado") : null;
                if (error != lastError) { Log(error ?? $"lendo {temp!.Name} de {cpu.Name}"); lastError = error; }
                data = new
                {
                    at = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                    cpu = cpu.Name,
                    temp = temp?.Value is float t ? Math.Round(t, 1) : (double?)null,
                    sensor = temp?.Name,
                    power = power is float p ? Math.Round(p, 1) : (double?)null,
                    clockMhz = clocks.Count > 0 ? Math.Round(clocks.Average()) : (double?)null,
                    error,
                };
            }
            catch (Exception e)
            {
                if (e.Message != lastError) { Log("erro: " + e.Message); lastError = e.Message; }
                data = new { at = DateTimeOffset.Now.ToUnixTimeMilliseconds(), error = e.Message };
            }
            try
            {
                var tmp = DataFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data));
                File.Move(tmp, DataFile, overwrite: true);
            }
            catch { /* o painel lendo bem nessa hora: tenta de novo daqui a 2s */ }
            Thread.Sleep(2000);
        }
    }

    private static bool PawnIoInstalled()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
        return k != null;
    }

    // ---------- lado do painel ----------
    internal sealed record Reading(double? Temp, string? Sensor, double? Power, double? ClockMhz, string? Error);

    private static Reading? cached;
    private static long cachedAt;

    /// <summary>Última leitura do ajudante (null se ele não está instalado ou parou há mais de 10s).</summary>
    public static Reading? Read()
    {
        var now = Environment.TickCount64;
        if (now - cachedAt < 1000) return cached;
        cachedAt = now;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(DataFile));
            var r = doc.RootElement;
            var at = r.TryGetProperty("at", out var a) ? a.GetInt64() : 0;
            if (DateTimeOffset.Now.ToUnixTimeMilliseconds() - at > 10_000) return cached = null;
            double? D(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return cached = new Reading(D("temp"), S("sensor"), D("power"), D("clockMhz"), S("error"));
        }
        catch (FileNotFoundException) { return cached = null; }
        catch (DirectoryNotFoundException) { return cached = null; }
        catch { return cached; } // arquivo sendo trocado: fica com a anterior
    }
}
