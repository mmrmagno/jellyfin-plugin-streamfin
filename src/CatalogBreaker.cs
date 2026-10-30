namespace StreamFin;

// The music catalog sits behind Cloudflare and goes away for minutes at a time (522, 502, or answers after 20 s).
// After a few failures in a row callers stop waiting on it: stored copies answer at once, and one request per
// period checks whether it is back. A success closes it again.
public sealed class CatalogBreaker(int limit = 3, TimeSpan? window = null, TimeSpan? retry = null)
{
    private readonly object gate = new();
    // Wide enough for three calls that each waited out Cloudflare's 40 s before failing.
    private readonly TimeSpan window = window ?? TimeSpan.FromMinutes(2);
    private readonly TimeSpan retry = retry ?? TimeSpan.FromSeconds(20);
    private int failures;
    private DateTime first;
    private DateTime openUntil;

    public bool IsOpen(DateTime now)
    {
        lock (gate) return failures >= limit && now < openUntil;
    }

    // Whether the next check is due, without claiming it.
    public bool Due(DateTime now)
    {
        lock (gate) return failures < limit || now >= openUntil;
    }

    // Whether a call may go out now. While open, the first caller after each period goes out as the probe.
    public bool Allow(DateTime now)
    {
        lock (gate)
        {
            if (failures < limit) return true;
            if (now < openUntil) return false;
            openUntil = now + retry;
            return true;
        }
    }

    // True when the catalog was counted as down until now.
    public bool Success()
    {
        lock (gate)
        {
            var was = failures >= limit;
            failures = 0;
            return was;
        }
    }

    // True when this failure is the one that opened it, so the outage is logged once.
    public bool Failure(DateTime now)
    {
        lock (gate)
        {
            if (failures < limit && now - first > window) (first, failures) = (now, 0);
            if (++failures < limit) return false;
            openUntil = now + retry;
            return failures == limit;
        }
    }

    // What counts as the catalog being down: no answer in time, no connection, or a server error. A 404 or a
    // 429 is the catalog working as meant.
    public static bool IsOutage(Exception ex, CancellationToken caller) => ex switch
    {
        SourceStatusException status => status.Status >= 500,
        HttpRequestException or IOException => true,
        OperationCanceledException => !caller.IsCancellationRequested,
        _ => false
    };
}
