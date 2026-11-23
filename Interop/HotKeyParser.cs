using System.Windows.Input;

namespace Glance.Interop;

/// <summary>Turns text such as "Ctrl+Alt+U" or "Win+Shift+F9" into RegisterHotKey arguments.</summary>
internal static class HotKeyParser
{
    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Native.MOD_CONTROL; continue;
                case "alt": modifiers |= Native.MOD_ALT; continue;
                case "shift": modifiers |= Native.MOD_SHIFT; continue;
                case "win" or "windows": modifiers |= Native.MOD_WIN; continue;
            }

            // Only one non-modifier key is allowed.
            if (virtualKey != 0 || !TryParseKey(part, out var key)) return false;
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        }

        return virtualKey != 0;
    }

    private static bool TryParseKey(string text, out Key key)
    {
        if (text.Length == 1 && char.IsAsciiDigit(text[0]))
        {
            key = Key.D0 + (text[0] - '0');
            return true;
        }

        // Covers letters, F1 to F24, Space, Tab, Home, Pause, OemTilde and the rest of the Key enum.
        return Enum.TryParse(text, ignoreCase: true, out key) && Enum.IsDefined(key) && key != Key.None;
    }
}
