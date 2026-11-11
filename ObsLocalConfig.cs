using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PainelDeck;

/// <summary>
/// Configuração do servidor WebSocket do próprio OBS (%APPDATA%\obs-studio\plugin_config\obs-websocket\config.json).
/// O painel lê daqui a porta e a senha (ninguém precisa copiar pro settings.json) e, quando é ele que abre o OBS,
/// liga o servidor antes se estiver desligado. Com "obs": { "url" / "password" } preenchidos no settings.json, vale o settings.
/// </summary>
internal static class ObsLocalConfig
{
    public const string DefaultUrl = "ws://127.0.0.1:4455";

    public sealed record Info(bool Exists, bool Enabled, int Port, bool Auth, string Password);

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "obs-studio", "plugin_config", "obs-websocket", "config.json");

    public static Info Read()
    {
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject o)
                return new Info(true, Bool(o["server_enabled"]), o["server_port"] is JsonValue p && p.TryGetValue<int>(out var port) ? port : 4455,
                    Bool(o["auth_required"]), o["server_password"] is JsonValue s && s.TryGetValue<string>(out var pw) ? pw : "");
        }
        catch { /* arquivo ilegível: segue com o padrão */ }
        return new Info(false, false, 4455, false, "");
    }

    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>Endereço e senha pra conectar: os do settings.json se foram mudados, senão os do próprio OBS.</summary>
    public static (string Url, string Password) Resolve(string url, string password)
    {
        if (!string.Equals(url, DefaultUrl, StringComparison.OrdinalIgnoreCase)) return (url, password);
        var info = Read();
        if (!info.Exists) return (url, password);
        return ($"ws://127.0.0.1:{info.Port}", password != "" ? password : info.Auth ? info.Password : "");
    }

    /// <summary>
    /// Liga o servidor WebSocket na configuração do OBS. Só funciona com o OBS FECHADO (aberto, ele regrava o arquivo
    /// ao sair). Se o OBS nunca teve essa configuração, cria uma com senha aleatória (que só o painel precisa saber).
    /// </summary>
    public static void EnableServer()
    {
        try
        {
            JsonObject o;
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject existing)
            {
                if (Bool(existing["server_enabled"])) return;
                o = existing;
                o["server_enabled"] = true;
            }
            else
            {
                o = new JsonObject
                {
                    ["alerts_enabled"] = false, ["auth_required"] = true, ["first_load"] = false, ["server_enabled"] = true,
                    ["server_password"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(), ["server_port"] = 4455,
                };
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* sem acesso: o erro de conexão explica o que fazer na mão */ }
    }

    /// <summary>Troca o erro técnico de conexão por o que fazer, quando dá pra saber o motivo.</summary>
    public static string Explain(string error)
    {
        if (error.Contains("senha", StringComparison.OrdinalIgnoreCase)) return error;
        var info = Read();
        if (!info.Exists || !info.Enabled)
            return "O servidor WebSocket do OBS está desligado. No OBS: Ferramentas → Configurações do servidor WebSocket → "
                 + "Ativar servidor WebSocket → Aplicar. (Ou feche o OBS e mande gravar de novo: o painel liga sozinho.)";
        return $"O OBS não respondeu na porta {info.Port}. Feche e abra o OBS de novo; se continuar, confira em "
             + "Ferramentas → Configurações do servidor WebSocket se o servidor está ativado.";
    }
}
