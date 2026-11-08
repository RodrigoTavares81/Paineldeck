using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace PainelDeck;

internal sealed record NewsItem(string Title, string Source, string Url, DateTimeOffset? Published);

/// <summary>
/// Notícias por RSS/Atom (seção opcional "news" do deck.json). Busca em segundo plano, guarda em memória,
/// e se a busca falhar mantém o último resultado bom. Feito no C# porque os feeds não liberam CORS pro painel.
/// </summary>
internal sealed class News
{
    private const string DefaultFeed = "https://news.google.com/rss/search?q=%22intelig%C3%AAncia+artificial%22+OR+OpenAI+OR+Anthropic+OR+Gemini+OR+Nvidia+IA+when:2d&hl=pt-BR&gl=BR&ceid=BR:pt-419";
    private static readonly HttpClient Http = CreateHttp();

    private readonly object gate = new();
    private Task? running;
    private List<NewsItem> items = new();
    private DateTimeOffset? updated;
    private string? error;
    private string cfgKey = "";

    private sealed record Config(string Title, string[] Feeds, int Max, int RefreshMinutes);

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("PainelDeck/1.0");
        return h;
    }

    /// <summary>Resposta da chamada "news". Só espera a rede se ainda não tiver nada em cache.</summary>
    public async Task<object> GetAsync(JsonNode deck, bool force)
    {
        var cfg = ReadConfig(deck);
        var key = string.Join("\n", cfg.Feeds) + "|" + cfg.Max;
        Task? wait;
        lock (gate)
        {
            var stale = force || key != cfgKey || updated == null || DateTimeOffset.Now - updated > TimeSpan.FromMinutes(cfg.RefreshMinutes);
            if (stale && running == null)
            {
                cfgKey = key;
                running = Task.Run(() => FetchAsync(cfg));
            }
            wait = items.Count == 0 ? running : null;
        }
        if (wait != null) await wait;
        lock (gate)
        {
            return new
            {
                title = cfg.Title,
                items = items.Select(i => new { title = i.Title, source = i.Source, url = i.Url, published = i.Published?.ToString("o") }).ToArray(),
                updated = updated?.ToString("o"),
                error,
            };
        }
    }

    private async Task FetchAsync(Config cfg)
    {
        var all = new List<NewsItem>();
        var errors = new List<string>();
        await Task.WhenAll(cfg.Feeds.Select(async url =>
        {
            try
            {
                var xml = await Http.GetStringAsync(url);
                var got = Parse(xml);
                lock (all) all.AddRange(got);
            }
            catch (Exception ex) { lock (errors) errors.Add($"{new Uri(url).Host}: {ex.Message}"); }
        }));
        var result = all.GroupBy(i => i.Title.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderByDescending(i => i.Published ?? DateTimeOffset.MinValue).Take(cfg.Max).ToList();
        lock (gate)
        {
            if (result.Count > 0) { items = result; updated = DateTimeOffset.Now; }
            else if (updated == null) updated = DateTimeOffset.Now - TimeSpan.FromMinutes(cfg.RefreshMinutes) + TimeSpan.FromMinutes(1); // falhou sem cache: tenta de novo em ~1 min
            error = errors.Count > 0 ? string.Join("; ", errors) : result.Count == 0 ? "Nenhuma notícia nos feeds" : null;
            running = null;
        }
    }

    /// <summary>RSS 2.0 (item) ou Atom (entry). Público pra dar pra testar sem rede.</summary>
    public static List<NewsItem> Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var list = new List<NewsItem>();
        foreach (var it in doc.Descendants("item"))
        {
            var title = (string?)it.Element("title") ?? "";
            var source = ((string?)it.Element("source"))?.Trim() ?? "";
            // Google News: "Título - Fonte"
            if (source != "" && title.EndsWith(" - " + source)) title = title[..^(source.Length + 3)];
            var link = ((string?)it.Element("link"))?.Trim() ?? "";
            if (source == "" && Uri.TryCreate(link, UriKind.Absolute, out var u)) source = u.Host.Replace("www.", "");
            list.Add(new NewsItem(title.Trim(), source, link, Date((string?)it.Element("pubDate"))));
        }
        foreach (var e in doc.Descendants(atom + "entry"))
        {
            var link = e.Elements(atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value ?? "";
            var source = Uri.TryCreate(link, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "";
            list.Add(new NewsItem(((string?)e.Element(atom + "title") ?? "").Trim(), source, link,
                Date((string?)e.Element(atom + "updated") ?? (string?)e.Element(atom + "published"))));
        }
        return list.Where(i => i.Title != "").ToList();
    }

    private static DateTimeOffset? Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().Replace(" GMT", " +0000").Replace(" UTC", " +0000");
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) ? d : null;
    }

    private static Config ReadConfig(JsonNode deck)
    {
        var n = deck["news"] as JsonObject;
        string Str(string k, string def) => n?[k] is JsonValue v && v.TryGetValue<string>(out var s) && s != "" ? s : def;
        int Int(string k, int def, int min, int max) => n?[k] is JsonValue v && v.TryGetValue<int>(out var i) ? Math.Clamp(i, min, max) : def;
        var feeds = (n?["feeds"] as JsonArray)?.Select(f => f is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https").Select(s => s!).ToArray();
        return new Config(Str("title", "IA"), feeds is { Length: > 0 } ? feeds : new[] { DefaultFeed }, Int("max", 8, 1, 50), Int("refreshMinutes", 20, 5, 1440));
    }
}
