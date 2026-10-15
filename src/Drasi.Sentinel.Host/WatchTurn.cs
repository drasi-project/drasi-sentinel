using System.Text.RegularExpressions;

namespace Drasi.Sentinel.Host;

public static class WatchTurn
{
    public static bool ContinuesOccupiedTask(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return true;
        if (IsAffirmation(trimmed))
            return true;
        if (RequestsDetection(trimmed))
            return false;
        return IsTaskManagement(trimmed);
    }

    private static bool RequestsDetection(string text) =>
        Regex.IsMatch(
            text,
            @"\b(?:when|whenever|once|until|while|if)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsTaskManagement(string text) =>
        Regex.IsMatch(
            text,
            @"\b(?:cancel|stop|pause|resume|list|show|rows|results|subscription|subscriptions|tasks|watches)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsAffirmation(string text)
    {
        var words = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 1 or > 3)
            return false;
        var normalized = string.Join(' ', words).TrimEnd('.', '!', '?');
        return Regex.IsMatch(
            normalized,
            @"^(?:yes|yeah|yep|ok|okay|sure|thanks|thank you|none|\d+\s*(?:s|m|h|d|sec|secs|seconds|min|mins|minutes|hour|hours|day|days))$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
