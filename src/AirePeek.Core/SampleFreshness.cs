namespace AirePeek.Core;

public enum FreshnessState { Never, Fresh, Stale, Disconnected }

/// <summary>A per-source freshness indicator. A recent queue sample never freshens a GPU sample.</summary>
public sealed record SampleFreshness(FreshnessState State, TimeSpan? Age, string Label)
{
    public bool NeedsAttention => State is FreshnessState.Stale or FreshnessState.Disconnected;

    /// <summary>Two missed sampling intervals mark the data stale; disconnection is visible immediately.</summary>
    public static SampleFreshness Evaluate(DateTimeOffset? sampledAt, TimeSpan interval, DateTimeOffset now, bool disconnected = false)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        if (sampledAt is null)
            return disconnected ? new(FreshnessState.Disconnected, null, "Disconnected") : new(FreshnessState.Never, null, "Not sampled");

        // Wall-clock adjustments must not produce negative freshness text or treat future timestamps as stale.
        var age = now > sampledAt.Value ? now - sampledAt.Value : TimeSpan.Zero;
        var ageText = age.TotalSeconds < 60 ? $"{(long)age.TotalSeconds}s"
            : age.TotalMinutes < 60 ? $"{(long)age.TotalMinutes}m"
            : age.TotalHours < 24 ? $"{(long)age.TotalHours}h" : $"{(long)age.TotalDays}d";
        if (disconnected) return new(FreshnessState.Disconnected, age, $"Disconnected · {ageText}");
        return age >= interval + interval
            ? new(FreshnessState.Stale, age, $"Stale · {ageText}")
            : new(FreshnessState.Fresh, age, ageText);
    }
}
