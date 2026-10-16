namespace Drasi.Sentinel.Application;

public static class ModelReply
{
    public static string SingleCopy(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
            return trimmed;

        var lines = trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 1 && lines.All(line => line == lines[0]))
            return lines[0];

        for (var i = 0; i < trimmed.Length; i++)
        {
            if (!char.IsWhiteSpace(trimmed[i]))
                continue;
            var end = i;
            while (end < trimmed.Length && char.IsWhiteSpace(trimmed[end]))
                end++;
            if (end >= trimmed.Length)
                break;
            var left = trimmed[..i].Trim();
            var right = trimmed[end..].Trim();
            if (left.Length > 0 && left == right)
                return left;
            i = end - 1;
        }

        return trimmed;
    }
}
