using Microsoft.Extensions.Logging;

namespace StreamFin;

public enum SourceHealth { Healthy, Down, Recovering }

public sealed record SourceStatus(SourceHealth State, DateTime Since, DateTime? NextCheck, string? LastError);

// The music catalog sits behind Cloudflare and goes away for minutes at a time (522, 502, or answers after 20 s), and
// on bad days it comes back for two minutes and goes again. After a few failures or slow answers it counts as down,
// and checks back after a growing pause (30 s up to 15 min). It is used again only after three quick answers in a row.
// A probed source is checked by a background prober alone, so no player ever waits on one of those checks; any other
// source lets one real request through per pause, and lets everything through while it recovers.
public sealed class CatalogBreaker(int limit = 3, TimeSpan? window = null, TimeSpan? slow = null, bool probed = false)
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15)];
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(20);
    // Healthy this long, an outage starts over at the shortest pause; a source that keeps flapping keeps backing off.
    private static readonly TimeSpan Settled = TimeSpan.FromMinutes(30);
    private const int Needed = 3;
    private readonly object gate = new();
    // Wide enough for three calls that each waited out Cloudflare's 40 s before failing.
    private readonly TimeSpan window = window ?? TimeSpan.FromMinutes(2);
    private readonly TimeSpan slow = slow ?? TimeSpan.FromSeconds(8);
    private SourceHealth state;
    private DateTime since = DateTime.UtcNow;
    private DateTime downSince;
    private DateTime next;
    private DateTime first;
    private int failures;
    private int good;
    private int step = -1;
    private string? lastError;

    public string Name { get; init; } = "source";
    public ILogger? Logger { get; init; }
    public bool Probed => probed;

    // Whether user requests may go to it now.
    public bool Usable(DateTime now)
    {
        lock (gate) return state == SourceHealth.Healthy || !probed && state == SourceHealth.Recovering;
    }

    // Whether a request would be let through now, without claiming the check.
    public bool Due(DateTime now)
    {
        lock (gate) return state == SourceHealth.Healthy || !probed && (state == SourceHealth.Recovering || now >= next);
    }

    // Whether a request may go out now. While down and not probed, the first caller after each pause goes out as the check.
    public bool Allow(DateTime now)
    {
        lock (gate)
        {
            if (state == SourceHealth.Healthy || !probed && state == SourceHealth.Recovering) return true;
            if (probed || now < next) return false;
            next = now + Backoff[step];
            return true;
        }
    }

    // A request that worked. One that took longer than slow counts as a failure. True when this changed its state.
    public bool Success(DateTime now, TimeSpan took = default, TimeSpan? slow = null)
    {
        if (took > (slow ?? this.slow)) return Failure(now, $"answered after {took.TotalSeconds:0} s");
        lock (gate) if (probed && state != SourceHealth.Healthy) return false;
        return Passed(now);
    }

    // A request that failed. On a probed source only the prober's checks count once it is down. True when this
    // changed its state.
    public bool Failure(DateTime now, string cause = "no answer")
    {
        lock (gate) if (probed && state != SourceHealth.Healthy) return false;
        return Failed(now, cause);
    }

    // The prober's check: null when it answered quickly enough, otherwise what went wrong.
    public bool Checked(DateTime now, string? problem) => problem is null ? Passed(now) : Failed(now, problem);

    public SourceStatus Status(DateTime now)
    {
        lock (gate) return new SourceStatus(state, state == SourceHealth.Healthy ? since : downSince, state == SourceHealth.Healthy ? null : next, lastError);
    }

    private bool Passed(DateTime now)
    {
        SourceHealth? changed = null;
        lock (gate)
        {
            if (state == SourceHealth.Healthy) failures = 0;
            else if (state == SourceHealth.Down) (state, good, next, changed) = (SourceHealth.Recovering, 1, now + Spacing, SourceHealth.Recovering);
            else if (++good >= Needed) (state, since, failures, changed) = (SourceHealth.Healthy, now, 0, SourceHealth.Healthy);
            else next = now + Spacing;
        }
        if (changed == SourceHealth.Recovering) Logger?.LogInformation("StreamFin {Name} answered a check; using it again after {More} more", Name, Needed - 1);
        else if (changed == SourceHealth.Healthy) Logger?.LogInformation("StreamFin {Name} answers again", Name);
        return changed is not null;
    }

    private bool Failed(DateTime now, string cause)
    {
        var changed = false;
        TimeSpan wait;
        lock (gate)
        {
            lastError = cause;
            if (state == SourceHealth.Healthy)
            {
                if (now - first > window) (first, failures) = (now, 0);
                if (++failures < limit) return false;
                step = step >= 0 && now - since < Settled ? Math.Min(step + 1, Backoff.Length - 1) : 0;
                downSince = now;
            }
            else step = Math.Min(step + 1, Backoff.Length - 1);
            changed = state != SourceHealth.Down;
            state = SourceHealth.Down;
            wait = Backoff[step];
            next = now + wait;
        }
        if (changed) Logger?.LogWarning("StreamFin {Name} is not responding ({Cause}); checking again in {Seconds} s", Name, cause, (int)wait.TotalSeconds);
        return changed;
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

    public static string Cause(Exception ex) => ex is SourceStatusException status ? "HTTP " + status.Status : ex is OperationCanceledException ? "no answer in time" : ex.GetType().Name;
}
