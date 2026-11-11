using static PainelDeck.Native;

namespace PainelDeck;

/// <summary>
/// Atalhos globais (funcionam com qualquer janela em foco), lidos de "hotkeys" no settings.json.
/// Usa uma janela só de mensagens, então não depende do foco nem do modo edição do painel.
/// </summary>
internal sealed class HotKeys : NativeWindow, IDisposable
{
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    private readonly Dictionary<int, string> names = new();

    /// <summary>Nome do atalho apertado (ex.: "rest").</summary>
    public event Action<string>? Pressed;

    /// <summary>Atalhos que o Windows recusou (outro programa já usa a combinação).</summary>
    public List<string> Failed { get; } = new();

    public HotKeys(IReadOnlyDictionary<string, string> map)
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
        int id = 1;
        foreach (var (name, combo) in map)
        {
            if (string.IsNullOrWhiteSpace(combo)) continue; // "" = atalho desligado
            try
            {
                var (mods, vk) = Parse(combo);
                if (RegisterHotKey(Handle, id, mods | MOD_NOREPEAT, vk)) names[id++] = name;
                else Failed.Add($"{name} ({combo})");
            }
            catch (ArgumentException) { Failed.Add($"{name} ({combo})"); }
        }
    }

    /// <summary>"ctrl+alt+h" → (MOD_CONTROL|MOD_ALT, tecla H).</summary>
    public static (uint mods, uint vk) Parse(string combo)
    {
        uint mods = 0, vk = 0;
        foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win" or "windows": mods |= MOD_WIN; break;
                default:
                    if (vk != 0) throw new ArgumentException($"Atalho com duas teclas normais: '{combo}'");
                    vk = Keyboard.ParseKey(part);
                    break;
            }
        }
        if (vk == 0) throw new ArgumentException($"Atalho sem tecla: '{combo}'");
        return (mods, vk);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && names.TryGetValue((int)m.WParam, out var name)) Pressed?.Invoke(name);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in names.Keys) UnregisterHotKey(Handle, id);
        names.Clear();
        DestroyHandle();
    }
}
