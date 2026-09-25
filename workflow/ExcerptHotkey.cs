namespace KnowledgeClipper.Workflow;

public readonly record struct ExcerptHotkey(uint Modifiers, uint VirtualKey)
{
    public static ExcerptHotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Length > 4) throw new InvalidDataException("快捷键需包含 Ctrl 或 Alt，以及 Space、字母、数字或 F1–F12。");
        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            uint bit = part.ToUpperInvariant() switch { "CTRL" => 2, "ALT" => 1, "SHIFT" => 4, _ => 0 };
            if (bit == 0 || (modifiers & bit) != 0) throw new InvalidDataException("快捷键修饰键无效。");
            modifiers |= bit;
        }
        if ((modifiers & 3) == 0) throw new InvalidDataException("快捷键必须包含 Ctrl 或 Alt。");
        var key = parts[^1].ToUpperInvariant();
        uint virtualKey;
        if (key == "SPACE") virtualKey = 0x20;
        else if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) virtualKey = key[0];
        else if (key.StartsWith('F') && int.TryParse(key[1..], out var number) && number is >= 1 and <= 12)
            virtualKey = (uint)(0x70 + number - 1);
        else throw new InvalidDataException("快捷键仅支持 Space、字母、数字或 F1–F12。");
        return new(modifiers, virtualKey);
    }
}
