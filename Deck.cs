using System.Text.Json;
using System.Text.Json.Nodes;

namespace PainelDeck;

/// <summary>
/// Lê o deck.json (botões, páginas, widgets, macros). Se o arquivo estiver com erro,
/// guarda o erro e continua usando a última versão boa, pra um JSON quebrado não apagar o painel.
/// </summary>
internal sealed class Deck
{
    private static readonly JsonDocumentOptions DocOpts = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonNodeOptions NodeOpts = new() { PropertyNameCaseInsensitive = false };

    private readonly object gate = new();
    private JsonObject current = new();

    public string FilePath { get; }
    public string? Error { get; private set; }

    public Deck(string root) => FilePath = Path.Combine(root, "deck.json");

    /// <summary>Relê o arquivo. Devolve false se deu erro (a versão anterior continua valendo).</summary>
    public bool Reload()
    {
        try
        {
            if (!File.Exists(FilePath)) throw new FileNotFoundException("deck.json não encontrado", FilePath);
            string text = ReadShared(FilePath);
            if (JsonNode.Parse(text, NodeOpts, DocOpts) is not JsonObject o)
                throw new InvalidDataException("o deck.json precisa ser um objeto { ... }");
            if (o["pages"] is not JsonArray pages || pages.Count == 0)
                throw new InvalidDataException("o deck.json precisa ter \"pages\" com pelo menos uma página");
            lock (gate) { current = o; Error = null; }
            return true;
        }
        catch (JsonException ex)
        {
            Error = $"deck.json com erro na linha {(ex.LineNumber ?? 0) + 1}: {ex.Message}";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        return false;
    }

    /// <summary>
    /// Grava o deck vindo do editor visual. Antes faz uma cópia em deck.backup.json.
    /// O bloco de comentários do topo (a "colinha" dos tipos de ação) é mantido;
    /// comentários no meio dos itens se perdem, porque o editor reescreve o JSON.
    /// </summary>
    public void Save(JsonNode deck)
    {
        if (deck is not JsonObject o || o["pages"] is not JsonArray pages || pages.Count == 0)
            throw new InvalidDataException("Deck inválido: precisa ter pelo menos uma página");

        string header = "";
        if (File.Exists(FilePath))
        {
            var old = ReadShared(FilePath);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(FilePath)!, "deck.backup.json"), old);
            header = string.Concat(old.Split('\n')
                .TakeWhile(l => l.TrimStart().StartsWith("//") || l.Trim().Length == 0)
                .Select(l => l.TrimEnd('\r') + Environment.NewLine));
        }

        var json = o.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // emojis e acentos legíveis
        });
        // o System.Text.Json escapa emoji (\uD83C\uDF99) mesmo no modo relaxado: desfaz pra ficar legível
        json = System.Text.RegularExpressions.Regex.Replace(json,
            @"(?<!\\)\\u(D[89AB][0-9A-F]{2})\\u(D[C-F][0-9A-F]{2})",
            m => new string(new[] { (char)Convert.ToInt32(m.Groups[1].Value, 16), (char)Convert.ToInt32(m.Groups[2].Value, 16) }),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, header + json + Environment.NewLine);
        File.Move(tmp, FilePath, overwrite: true);
        Reload();
    }

    /// <summary>JSON do deck inteiro, pra mandar pro painel.</summary>
    public JsonNode Snapshot()
    {
        lock (gate) return current.DeepClone();
    }

    /// <summary>Passos de uma macro com nome (seção "macros").</summary>
    public JsonElement? Macro(string name)
    {
        lock (gate)
        {
            if (current["macros"] is JsonObject macros && macros[name] is JsonArray steps)
                return JsonSerializer.SerializeToElement(steps);
        }
        return null;
    }

    /// <summary>Editores (VS Code, Notepad) às vezes ainda estão gravando quando o watcher avisa: tenta de novo.</summary>
    private static string ReadShared(string path)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException) when (i < 5)
            {
                Thread.Sleep(60);
            }
        }
    }
}
