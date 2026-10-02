namespace RoboRightClick.Core;

/// <summary>A key combination the user may not choose, and why.</summary>
public sealed record ReservedHotkey(bool Shift, string Key, string Reason)
{
    public string Text => HotkeySpec.FormatParts(Shift, Key);
}

/// <summary>
/// The Robo-Paste keyboard shortcut ("pasteHotkey" in config.json). Ctrl is always part of
/// it, Shift optionally, and exactly one key: A-Z, 0-9 or F1-F12. Alt and the Windows key
/// are refused: Ctrl+Alt is AltGr on many European layouts, so a Ctrl+Alt combination would
/// take characters people type, and the Windows key belongs to the shell.
/// </summary>
/// <remarks>
/// The letter keys are virtual keys, so on a non-US layout "V" is the key that produces V,
/// wherever it sits. Parse and Format round-trip; Format is the canonical form that is saved.
/// </remarks>
public sealed record HotkeySpec
{
    public const string DefaultText = "Ctrl+Shift+V";

    /// <summary>Longest config text accepted; anything longer is invalid (and so off).</summary>
    public const int MaxTextLength = 32;

    /// <summary>The shortcut a missing "pasteHotkey" means.</summary>
    public static readonly HotkeySpec Default = new(shift: true, "V", 'V');

    private HotkeySpec(bool shift, string key, int virtualKey)
    {
        Shift = shift;
        Key = key;
        VirtualKey = virtualKey;
    }

    /// <summary>Shift is part of the combination (Ctrl always is).</summary>
    public bool Shift { get; }

    /// <summary>The key token in canonical form: "V", "5", "F3".</summary>
    public string Key { get; }

    /// <summary>The Windows virtual-key code of <see cref="Key"/> (VK_A-VK_Z, VK_0-VK_9, VK_F1-VK_F12).</summary>
    public int VirtualKey { get; }

    public string Format() => FormatParts(Shift, Key);

    public override string ToString() => Format();

    internal static string FormatParts(bool shift, string key) => shift ? "Ctrl+Shift+" + key : "Ctrl+" + key;

    /// <summary>
    /// Every combination that is refused, with the reason shown to the user. Each one already
    /// does something in File Explorer or in the text fields inside it, and the hook takes the
    /// key before Explorer sees it.
    /// </summary>
    public static readonly IReadOnlyList<ReservedHotkey> Reserved = BuildReserved();

    /// <summary>
    /// Parses a config value. Empty (or only spaces) is a deliberate "off": no spec and no
    /// problem. Anything else that is not a valid, unreserved combination gives a problem and
    /// no spec, which the settings reader turns into "off" as well (never the default: that
    /// would switch on a keyboard hook the user tried to switch off).
    /// </summary>
    public static (HotkeySpec? Spec, string? Problem) Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxTextLength)
        {
            return (null, $"is longer than {MaxTextLength} characters");
        }
        if (text.Trim().Length == 0)
        {
            return (null, null);
        }

        var tokens = text.Split('+');
        bool ctrl = false, shift = false;
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i].Trim();
            var isLast = i == tokens.Length - 1;
            if (token.Length == 0)
            {
                return (null, "has an empty part; write it like Ctrl+Shift+V");
            }
            if (RefusedModifierReason(token) is { } refused)
            {
                return (null, refused);
            }
            if (IsToken(token, "Ctrl") || IsToken(token, "Control"))
            {
                if (ctrl)
                {
                    return (null, "names Ctrl twice");
                }
                if (isLast)
                {
                    return (null, "needs a key after the modifiers, for example Ctrl+Shift+V");
                }
                ctrl = true;
                continue;
            }
            if (IsToken(token, "Shift"))
            {
                if (shift)
                {
                    return (null, "names Shift twice");
                }
                if (isLast)
                {
                    return (null, "needs a key after the modifiers, for example Ctrl+Shift+V");
                }
                shift = true;
                continue;
            }

            var key = KeyFor(token);
            if (key is null)
            {
                return (null, $"has a key this app does not support ('{Shorten(token)}'); use A-Z, 0-9 or F1-F12");
            }
            if (!isLast)
            {
                return (null, "must have exactly one key, and it comes last, for example Ctrl+Shift+V");
            }
            if (key.Value.Name == "F10")
            {
                // Windows delivers F10 as a system key (it opens the menu bar), with or without
                // Ctrl, so the hook's WM_KEYDOWN rule would never see it.
                return (null, "cannot use F10: Windows treats it as the menu key");
            }
            if (!ctrl)
            {
                return (null, "must include Ctrl");
            }
            if (ReservedReason(shift, key.Value.Name) is { } reason)
            {
                return (null, $"is {FormatParts(shift, key.Value.Name)}, which is taken: {reason}");
            }
            return (new HotkeySpec(shift, key.Value.Name, key.Value.VirtualKey), null);
        }
        // Every token was a modifier; the last-token checks above already returned.
        return (null, "needs a key after the modifiers, for example Ctrl+Shift+V");
    }

    /// <summary>The reason a combination is reserved, or null when it is free.</summary>
    public static string? ReservedReason(bool shift, string key) =>
        Reserved.FirstOrDefault(r => r.Shift == shift && string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))?.Reason;

    private static bool IsToken(string token, string name) => string.Equals(token, name, StringComparison.OrdinalIgnoreCase);

    private static string? RefusedModifierReason(string token)
    {
        if (IsToken(token, "Alt") || IsToken(token, "AltGr"))
        {
            return "cannot use Alt: Ctrl+Alt is AltGr on many keyboard layouts and types characters";
        }
        if (IsToken(token, "Win") || IsToken(token, "Windows") || IsToken(token, "Meta"))
        {
            return "cannot use the Windows key: its shortcuts belong to Windows";
        }
        return null;
    }

    private static (string Name, int VirtualKey)? KeyFor(string token)
    {
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                // VK_A..VK_Z and VK_0..VK_9 equal the ASCII codes of the upper-case character.
                return (c.ToString(), c);
            }
            return null;
        }
        if (token.Length is 2 or 3 && token[0] is 'F' or 'f'
            && int.TryParse(token.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            && n is >= 1 and <= 12
            && token[1] != '0')
        {
            const int vkF1 = 0x70;
            return ("F" + n.ToString(System.Globalization.CultureInfo.InvariantCulture), vkF1 + n - 1);
        }
        return null;
    }

    /// <summary>Problems go into toasts and Settings; a pasted novel must not.</summary>
    private static string Shorten(string token) => token.Length <= 12 ? token : token[..12] + "…";

    private static List<ReservedHotkey> BuildReserved()
    {
        var list = new List<ReservedHotkey>
        {
            new(false, "A", "Select all"),
            new(false, "C", "Copy"),
            new(false, "D", "Delete to the Recycle Bin"),
            new(false, "E", "File Explorer's search box"),
            new(false, "F", "File Explorer's search box"),
            new(false, "L", "File Explorer's address bar"),
            new(false, "N", "New File Explorer window"),
            new(false, "R", "Refresh"),
            new(false, "T", "New tab in File Explorer"),
            new(false, "V", "Paste"),
            new(false, "W", "Close the window"),
            new(false, "X", "Cut"),
            new(false, "Y", "Redo"),
            new(false, "Z", "Undo"),
            new(false, "F1", "Shows or hides the ribbon in File Explorer"),
            new(false, "F4", "Closes the tab or window"),
            new(true, "C", "Copy as path in File Explorer"),
            new(true, "N", "New folder"),
            new(true, "E", "Expands File Explorer's navigation pane to the open folder"),
            new(true, "T", "Reopens a closed tab in many apps"),
        };
        for (var digit = '1'; digit <= '9'; digit++)
        {
            list.Add(new(false, digit.ToString(), "Switches tabs in many apps"));
            list.Add(new(true, digit.ToString(), "Changes the File Explorer view (icons, list, details)"));
        }
        return list;
    }
}
