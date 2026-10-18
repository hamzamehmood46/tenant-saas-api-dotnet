using System.Security.Cryptography;
using System.Text;

namespace TenantSaas.Api;

public static class KeyHasher
{
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>A new random key. Only its hash is stored, so it can be shown to the caller once and never again.</summary>
    public static string NewKey() =>
        "tsk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

public static class PlanLimits
{
    public static int RequestsPerMinute(Plan plan) => plan == Plan.Pro ? 200 : 20;
}

/// <summary>
/// Fixed one-minute windows, counted per tenant (not per key), so adding keys does not add capacity and one noisy
/// tenant cannot use up another's allowance. Uses an injected clock so tests can move time.
/// </summary>
public class TenantRateLimiter
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (long Window, int Count)> _counts = [];

    public TenantRateLimiter(TimeProvider time)
    {
        _time = time;
    }

    public (bool Allowed, int Limit, int Remaining, int RetryAfterSeconds) Take(Guid tenantId, Plan plan)
    {
        var limit = PlanLimits.RequestsPerMinute(plan);
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var window = now / 60;
        lock (_gate)
        {
            var (w, count) = _counts.TryGetValue(tenantId, out var c) ? c : (window, 0);
            if (w != window)
            {
                count = 0;
            }

            if (count >= limit)
            {
                _counts[tenantId] = (window, count);
                return (false, limit, 0, (int)((window + 1) * 60 - now));
            }

            _counts[tenantId] = (window, count + 1);
            return (true, limit, limit - count - 1, 0);
        }
    }
}
