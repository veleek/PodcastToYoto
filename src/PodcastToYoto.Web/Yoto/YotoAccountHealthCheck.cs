using Microsoft.Extensions.Diagnostics.HealthChecks;
using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Yoto;

/// <summary>Healthy when the DB is reachable and Yoto is connected; Degraded when Yoto needs attention.</summary>
public sealed class YotoAccountHealthCheck(YotoAccountService account) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var s = await account.GetStatusAsync(ct);
            return s.Status switch
            {
                AccountStatus.Connected => HealthCheckResult.Healthy("Yoto connected"),
                AccountStatus.NeedsReauth => HealthCheckResult.Degraded($"Yoto needs re-login: {s.LastError}"),
                _ => HealthCheckResult.Degraded("Yoto not connected"),
            };
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database unavailable", ex);
        }
    }
}
