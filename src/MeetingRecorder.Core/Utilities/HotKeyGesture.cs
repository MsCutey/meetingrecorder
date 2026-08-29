namespace MeetingRecorder.Core.Utilities;

[Flags]
public enum HotKeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000
}

public readonly record struct HotKeyGesture(HotKeyModifiers Modifiers, uint VirtualKey)
{
    public static HotKeyGesture Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("热键不能为空。");
        }

        var modifiers = HotKeyModifiers.NoRepeat;
        uint key = 0;
        foreach (var rawPart in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL": modifiers |= HotKeyModifiers.Control; break;
                case "SHIFT": modifiers |= HotKeyModifiers.Shift; break;
                case "ALT": modifiers |= HotKeyModifiers.Alt; break;
                case "WIN" or "WINDOWS": modifiers |= HotKeyModifiers.Windows; break;
                default:
                    if (key != 0) throw new FormatException("热键只能包含一个普通按键。");
                    key = ParseKey(part);
                    break;
            }
        }

        if (key == 0 || (modifiers & ~HotKeyModifiers.NoRepeat) == HotKeyModifiers.None)
        {
            throw new FormatException("热键至少需要一个修饰键和一个普通按键。");
        }

        return new HotKeyGesture(modifiers, key);
    }

    private static uint ParseKey(string part)
    {
        if (part.Length == 1 && part[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return part[0];
        }

        if (part.Length is 2 or 3 && part[0] == 'F' && int.TryParse(part[1..], out var functionKey) && functionKey is >= 1 and <= 24)
        {
            return (uint)(0x70 + functionKey - 1);
        }

        throw new FormatException($"不支持的热键按键：{part}");
    }
}
