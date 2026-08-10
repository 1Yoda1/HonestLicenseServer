using System.Collections.Concurrent;

namespace HonestLicenseServer.Infrastructure;

public sealed class LoginAttemptLimiter
{
    private const int PermitLimit = 20;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, Counter> _counters =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryAcquire(string login, DateTime utcNow)
    {
        var key = login.Trim().ToUpperInvariant();
        var counter = _counters.GetOrAdd(key, _ => new Counter(utcNow));
        lock (counter)
        {
            if (utcNow - counter.WindowStartedAtUtc >= Window)
            {
                counter.WindowStartedAtUtc = utcNow;
                counter.Attempts = 0;
            }

            counter.Attempts++;
            return counter.Attempts <= PermitLimit;
        }
    }

    public void Reset(string login) => _counters.TryRemove(login.Trim().ToUpperInvariant(), out _);

    private sealed class Counter(DateTime windowStartedAtUtc)
    {
        public DateTime WindowStartedAtUtc { get; set; } = windowStartedAtUtc;
        public int Attempts { get; set; }
    }
}
