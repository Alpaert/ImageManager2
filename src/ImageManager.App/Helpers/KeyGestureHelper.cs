using Avalonia.Input;

namespace ImageManager.App.Helpers;

public static class KeyGestureHelper
{
    /// <summary>Convert a KeyEventArgs to a gesture string like "Ctrl+F" or "Right"</summary>
    public static string KeyEventArgsToGesture(KeyEventArgs e)
    {
        var parts = new List<string>();

        var modifiers = e.KeyModifiers;
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");

        string keyName = e.Key.ToString();
        keyName = keyName switch
        {
            "D0" or "D1" or "D2" or "D3" or "D4" or "D5" or "D6" or "D7" or "D8" or "D9"
                => keyName[1..],
            "OemPlus" => "=",
            "OemMinus" => "-",
            "OemComma" => ",",
            "OemPeriod" => ".",
            "OemQuestion" => "/",
            "OemOpenBrackets" => "[",
            "OemCloseBrackets" => "]",
            "OemPipe" => "\\",
            "OemQuotes" => "'",
            "OemSemicolon" => ";",
            "OemTilde" => "`",
            _ => keyName
        };

        parts.Add(keyName);
        return string.Join("+", parts);
    }

    /// <summary>Check if a key event is a modifier-only press (should be ignored)</summary>
    public static bool IsModifierOnly(KeyEventArgs e) =>
        e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
}
