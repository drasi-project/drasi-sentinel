namespace Drasi.Sentinel.Domain;

public abstract record Lifetime
{
    public bool IsDue(DateTimeOffset now) => Instant is { } instant && instant <= now;

    public virtual DateTimeOffset? Instant => null;
}

public sealed record Unbounded : Lifetime;

public sealed record Until : Lifetime
{
    public Until(DateTimeOffset instant)
    {
        if (instant == default)
            throw new ArgumentOutOfRangeException(nameof(instant));
        Instant = instant.ToUniversalTime();
    }

    public override DateTimeOffset? Instant { get; }
}
