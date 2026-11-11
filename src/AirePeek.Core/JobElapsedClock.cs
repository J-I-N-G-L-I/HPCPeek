using System.Globalization;

namespace AirePeek.Core;

public sealed record JobElapsedReading(string Text, bool IsKnown, bool IsEstimated);

/// <summary>Interpolates a sampled Slurm elapsed duration locally; this never reads or changes the job.</summary>
public sealed class JobElapsedClock
{
    private const long MaxSeconds = long.MaxValue / TimeSpan.TicksPerSecond;
    private DateTimeOffset? sampledAt;
    private SlurmJob? sampledJob;
    private long? sampledSeconds;
    private long lastEstimatedSeconds;
    private bool running;

    public void Update(SlurmJob job, DateTimeOffset? sampleTime)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (sampledAt == sampleTime && sampledJob?.Id == job.Id && sampledJob.State == job.State && sampledJob.Elapsed == job.Elapsed) return;
        sampledJob = job;
        sampledAt = sampleTime;
        sampledSeconds = TryParse(job.Elapsed, out var seconds) ? seconds : null;
        lastEstimatedSeconds = sampledSeconds ?? 0;
        // COMPLETING and SUSPENDED jobs must not accumulate local run time.
        running = job.State is "RUNNING" or "R";
    }

    public JobElapsedReading Read(DateTimeOffset now, bool live)
    {
        if (sampledSeconds is not { } seconds) return new("Unknown", false, false);
        bool estimate = live && running && sampledAt.HasValue;
        if (estimate)
        {
            // A backwards wall-clock correction never rewinds a local estimate. A new server sample may correct it.
            long delta = now > sampledAt!.Value ? (now - sampledAt.Value).Ticks / TimeSpan.TicksPerSecond : 0;
            seconds = Math.Max(lastEstimatedSeconds, seconds + Math.Min(delta, MaxSeconds - seconds));
            lastEstimatedSeconds = seconds;
        }
        return new(Format(seconds), true, estimate);
    }

    /// <summary>Parses %M output: minutes:seconds, hours:minutes:seconds, or days-hours:minutes:seconds.</summary>
    public static bool TryParse(string? value, out long seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        int dash = text.IndexOf('-');
        long days = 0;
        if (dash >= 0)
        {
            if (text.LastIndexOf('-') != dash || !Number(text[..dash], out days)) return false;
            text = text[(dash + 1)..];
        }
        var parts = text.Split(':');
        if (parts.Length is not (2 or 3) || dash >= 0 && parts.Length != 3) return false;
        if (!Number(parts[^1], out var sec) || sec > 59 || !Number(parts[^2], out var minutes)) return false;
        long hours = 0;
        if (parts.Length == 3 && (!Number(parts[0], out hours) || minutes > 59 || dash >= 0 && hours > 23)) return false;
        try { seconds = checked(days * 86400 + hours * 3600 + minutes * 60 + sec); }
        catch (OverflowException) { return false; }
        if (seconds > MaxSeconds) { seconds = 0; return false; }
        return true;
    }

    private static bool Number(string text, out long value) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static string Format(long seconds)
    {
        long days = seconds / 86400;
        var clock = $"{seconds / 3600 % 24:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
        return days > 0 ? $"{days}-{clock}" : clock;
    }
}
