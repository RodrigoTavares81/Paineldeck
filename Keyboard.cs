using System.Runtime.InteropServices;
using static PainelDeck.Native;

namespace PainelDeck;

/// <summary>Envia teclas via SendInput (funciona com a maioria dos apps e jogos).</summary>
internal static class Keyboard
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    private static readonly Dictionary<string, ushort> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12,
        ["win"] = 0x5B, ["windows"] = 0x5B,
        ["enter"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["printscreen"] = 0x2C, ["pause"] = 0x13, ["capslock"] = 0x14,
        ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE,
        ["num0"] = 0x60, ["num1"] = 0x61, ["num2"] = 0x62, ["num3"] = 0x63, ["num4"] = 0x64,
        ["num5"] = 0x65, ["num6"] = 0x66, ["num7"] = 0x67, ["num8"] = 0x68, ["num9"] = 0x69,
        // mídia
        ["playpause"] = 0xB3, ["next"] = 0xB0, ["prev"] = 0xB1, ["stop"] = 0xB2,
        ["volup"] = 0xAF, ["voldown"] = 0xAE, ["volmute"] = 0xAD,
    };

    private static readonly HashSet<ushort> Extended = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x2C, 0x5B,
        0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3,
    };

    public static ushort ParseKey(string raw)
    {
        var name = raw.Trim();
        if (Names.TryGetValue(name, out var vk)) return vk;
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        if (name.Length > 1 && (name[0] is 'f' or 'F') && int.TryParse(name[1..], out var n) && n is >= 1 and <= 24)
            return (ushort)(0x6F + n);
        throw new ArgumentException($"Tecla desconhecida: '{raw}'");
    }

    /// <summary>Ex.: "ctrl+shift+m", "win+shift+s", "f13", "playpause".</summary>
    public static void Combo(string combo, int holdMs = 30)
    {
        var keys = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(ParseKey).ToArray();
        if (keys.Length == 0) return;

        Send(keys.Select(k => Key(k, up: false)));
        Thread.Sleep(holdMs); // alguns jogos ignoram tecla "instantânea"
        Send(keys.Reverse().Select(k => Key(k, up: true)));
    }

    /// <summary>Digita texto (qualquer caractere, inclusive acentos).</summary>
    public static void Type(string text)
    {
        foreach (var ch in text)
        {
            if (ch == '\n') { Combo("enter", 10); continue; }
            if (ch == '\r') continue;
            Send(new[] { Unicode(ch, false), Unicode(ch, true) });
        }
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKeyW(vk, 0),
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0),
            }
        }
    };

    private static INPUT Unicode(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) }
        }
    };

    private static void Send(IEnumerable<INPUT> inputs)
    {
        var arr = inputs.ToArray();
        if (arr.Length > 0) SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }
}
