using System.Globalization;
using System.Text.RegularExpressions;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public sealed record TtlPolicy(TimeSpan? Maximum)
{
    public static readonly TtlPolicy Uncapped = new((TimeSpan?)null);
}

public static class TtlToken
{
    private static readonly Regex Duration = new(
        "^(?<count>[1-9][0-9]{0,8})(?<unit>[smhd])$",
        RegexOptions.CultureInvariant);

    public static Lifetime Admit(string? token, TtlPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(token))
            throw new SentinelException("ttl_required", "Pass ttl. Ask the caller how long to keep the watch, then stop.");

        var text = token.Trim();
        if (text.Equals("none", StringComparison.Ordinal))
        {
            if (policy.Maximum is { } maximum)
                throw new SentinelException("ttl_unbounded", $"ttl none is not allowed. The longest lifetime is {Format(maximum)}.");
            return new Unbounded();
        }

        if (ParseDuration(text) is not { } duration)
            throw new SentinelException("ttl_invalid", "ttl must be none or a positive integer plus s, m, h, or d.");
        if (policy.Maximum is { } cap && duration > cap)
            throw new SentinelException("ttl_exceeds_max", $"ttl {text} is longer than the maximum {Format(cap)}.");

        DateTimeOffset instant;
        try
        {
            instant = now.ToUniversalTime() + duration;
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new SentinelException("ttl_invalid", "ttl is too far in the future.");
        }

        return new Until(instant);
    }

    public static TimeSpan? ParseMaximum(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var text = token.Trim();
        if (text.Equals("none", StringComparison.Ordinal))
            return null;
        return ParseDuration(text)
            ?? throw new FormatException("max_ttl must be none or a positive integer plus s, m, h, or d.");
    }

    public static string Format(TimeSpan duration)
    {
        if (duration.Ticks % TimeSpan.TicksPerDay == 0 && duration.TotalDays >= 1)
            return ((long)duration.TotalDays).ToString(CultureInfo.InvariantCulture) + "d";
        if (duration.Ticks % TimeSpan.TicksPerHour == 0 && duration.TotalHours >= 1)
            return ((long)duration.TotalHours).ToString(CultureInfo.InvariantCulture) + "h";
        if (duration.Ticks % TimeSpan.TicksPerMinute == 0 && duration.TotalMinutes >= 1)
            return ((long)duration.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
        return ((long)duration.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
    }

    public static void EnsureSubscriptionFits(Lifetime query, Lifetime subscription)
    {
        if (query is not Until queryUntil)
            return;
        if (subscription is Unbounded || subscription is Until watch && watch.Instant > queryUntil.Instant)
            throw new SentinelException("ttl_exceeds_query", "The watch cannot last longer than the query. Pass a shorter ttl.");
    }

    private static TimeSpan? ParseDuration(string text)
    {
        var match = Duration.Match(text);
        if (!match.Success)
            return null;
        var count = long.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value switch
        {
            "s" => TimeSpan.FromSeconds(count),
            "m" => TimeSpan.FromMinutes(count),
            "h" => TimeSpan.FromHours(count),
            "d" => TimeSpan.FromDays(count),
            _ => null,
        };
    }
}
