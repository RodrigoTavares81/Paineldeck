using System.Globalization;
using System.Text.Json;

namespace PainelDeck;

/// <summary>
/// Tempo de jogo: soma os segundos jogados por dia e por jogo em playtime.json (na pasta do app, fica só no seu PC).
/// O <see cref="GameMode"/> chama <see cref="Tick"/> a cada ~2s enquanto um jogo está aberto; tempo parado
/// (sem mouse/teclado por "idleMinutes") não conta. Grava a cada minuto, então um crash perde no máximo 1 min.
/// </summary>
internal sealed class Playtime : IDisposable
{
    private const int KeepDays = 400, KeepSessions = 300;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly string file;
    private readonly object gate = new();
    private Data data;
    private string? game;
    private DateTimeOffset sessionStart;
    private double sessionSec;
    private long lastTick, lastSave;
    private bool dirty;

    public sealed class Data
    {
        /// <summary>"2026-11-08" → { "For Honor": 4380 } (segundos)</summary>
        public Dictionary<string, Dictionary<string, double>> Days { get; set; } = new();
        public List<Session> Sessions { get; set; } = new();
    }

    public sealed class Session
    {
        public string Game { get; set; } = "";
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset End { get; set; }
        public double Sec { get; set; }
    }

    public Playtime(string root)
    {
        file = Path.Combine(root, "playtime.json");
        data = Load();
    }

    /// <summary>Segundos jogados na sessão atual (sem o tempo parado).</summary>
    public double SessionSec { get { lock (gate) return sessionSec; } }

    public void Begin(string name)
    {
        lock (gate)
        {
            game = name;
            sessionStart = DateTimeOffset.Now;
            sessionSec = 0;
            lastTick = Environment.TickCount64;
        }
    }

    public void Tick(bool idle)
    {
        lock (gate)
        {
            if (game == null) return;
            var now = Environment.TickCount64;
            // passos grandes = o PC dormiu/travou: não conta
            var dt = Math.Clamp((now - lastTick) / 1000.0, 0, 10);
            lastTick = now;
            if (!idle && dt > 0)
            {
                sessionSec += dt;
                var day = Today(data);
                day[game] = day.GetValueOrDefault(game) + dt;
                dirty = true;
            }
            if (dirty && now - lastSave > 60_000) SaveLocked();
        }
    }

    public void End()
    {
        lock (gate)
        {
            if (game == null) return;
            if (sessionSec >= 60)
            {
                data.Sessions.Add(new Session { Game = game, Start = sessionStart, End = DateTimeOffset.Now, Sec = Math.Round(sessionSec) });
                if (data.Sessions.Count > KeepSessions) data.Sessions.RemoveRange(0, data.Sessions.Count - KeepSessions);
            }
            game = null;
            sessionSec = 0;
            SaveLocked();
        }
    }

    public double TodaySec(string name)
    {
        lock (gate) return data.Days.TryGetValue(Key(DateTime.Today), out var d) ? d.GetValueOrDefault(name) : 0;
    }

    /// <summary>Resposta do Bridge.call("playtime"): hoje, os últimos 7 dias, os mais jogados e as últimas sessões.</summary>
    public object Summary()
    {
        lock (gate)
        {
            var today = DateTime.Today;
            object Games(IEnumerable<KeyValuePair<string, double>> g) =>
                g.GroupBy(kv => kv.Key).Select(x => new { name = x.Key, sec = Math.Round(x.Sum(kv => kv.Value)) })
                 .Where(x => x.sec >= 1).OrderByDescending(x => x.sec).ToArray();
            Dictionary<string, double> Day(DateTime d) => data.Days.TryGetValue(Key(d), out var v) ? v : new();

            var week = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6)).Select(d => new
            {
                date = Key(d),
                label = d.ToString("ddd", new CultureInfo("pt-BR")).TrimEnd('.'),
                today = d == today,
                total = Math.Round(Day(d).Values.Sum()),
                games = Games(Day(d)),
            }).ToArray();
            var last7 = Enumerable.Range(0, 7).SelectMany(i => Day(today.AddDays(-i)));
            var last30 = Enumerable.Range(0, 30).SelectMany(i => Day(today.AddDays(-i)));
            return new
            {
                today = new { total = Math.Round(Day(today).Values.Sum()), games = Games(Day(today)) },
                week,
                weekTotal = Math.Round(week.Sum(w => w.total)),
                top = Games(last7),
                monthTotal = Math.Round(last30.Sum(kv => kv.Value)),
                sessions = data.Sessions.AsEnumerable().Reverse().Take(8)
                    .Select(s => new { game = s.Game, start = s.Start.ToString("o"), sec = s.Sec }).ToArray(),
                playing = game,
            };
        }
    }

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Dictionary<string, double> Today(Data d)
    {
        var k = Key(DateTime.Today);
        if (!d.Days.TryGetValue(k, out var day)) d.Days[k] = day = new Dictionary<string, double>();
        return day;
    }

    private Data Load()
    {
        try
        {
            if (File.Exists(file)) return JsonSerializer.Deserialize<Data>(File.ReadAllText(file), Json) ?? new Data();
        }
        catch
        {
            // arquivo estragado: guarda de lado em vez de apagar o histórico
            try { File.Move(file, file + ".estragado-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
        }
        return new Data();
    }

    private void SaveLocked()
    {
        lastSave = Environment.TickCount64;
        dirty = false;
        try
        {
            var cut = Key(DateTime.Today.AddDays(-KeepDays));
            foreach (var k in data.Days.Keys.Where(k => string.CompareOrdinal(k, cut) < 0).ToList()) data.Days.Remove(k);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json));
            File.Move(tmp, file, true);
        }
        catch { /* disco cheio/arquivo preso: tenta de novo no próximo minuto */ dirty = true; }
    }

    public void Dispose() => End();
}
