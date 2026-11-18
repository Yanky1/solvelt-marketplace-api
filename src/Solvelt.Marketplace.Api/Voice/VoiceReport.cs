using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Staff;

namespace Solvelt.Marketplace.Api.Voice;

public sealed record VoiceTierReport(string Tier, IReadOnlyList<string> Models, int Calls, int FinishedCalls, long TotalSeconds, long EstimatedCostMicros,
    long CostPerMinuteMicros, long AverageLatencyMs, int LatencySamples, double OrderCompletionPercent, double TransferPercent, double AbandonedPercent, double EndedOffTopicPercent);

public sealed record VoiceReportView(int Days, DateTimeOffset Since, IReadOnlyList<VoiceTierReport> Tiers);

/// <summary>Full vs mini over the last N days, from the call rows (counters only). Cost in micro-dollars (1 USD = 1,000,000); latency is caller-stops to first agent audio.</summary>
public sealed class VoiceReportService(MarketplaceDb db, TimeProvider time)
{
    public async Task<VoiceReportView> BuildAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 365);
        var since = time.GetUtcNow().AddDays(-days);
        var rows = await db.VoiceCalls.AsNoTracking().Where(c => c.StartedAt >= since).OrderByDescending(c => c.StartedAt).Take(50_000).ToListAsync(ct);
        var tiers = rows.GroupBy(c => c.Tier).OrderBy(g => g.Key).Select(g =>
        {
            var finished = g.Where(c => c.EndedAt != null).ToList();
            var seconds = finished.Sum(c => (long)c.DurationSeconds);
            var cost = finished.Sum(c => c.EstimatedCostMicros);
            var samples = g.Sum(c => c.LatencySamples);
            double Pct(int n) => finished.Count == 0 ? 0 : Math.Round(100.0 * n / finished.Count, 1);
            return new VoiceTierReport(g.Key, g.Select(c => c.Model).Distinct().Order().ToList(), g.Count(), finished.Count, seconds, g.Sum(c => c.EstimatedCostMicros),
                seconds == 0 ? 0 : cost * 60 / seconds, samples == 0 ? 0 : g.Sum(c => c.LatencyTotalMs) / samples, samples,
                Pct(finished.Count(c => c.Outcome == VoiceOutcome.OrderPlaced)), Pct(finished.Count(c => c.Outcome == VoiceOutcome.Transferred)), Pct(finished.Count(c => c.Outcome == VoiceOutcome.Abandoned)), Pct(finished.Count(c => c.Outcome == VoiceOutcome.EndedOffTopic)));
        }).ToList();
        return new VoiceReportView(days, since, tiers);
    }
}

public static class VoiceReportEndpoints
{
    public static IEndpointRouteBuilder MapStaffVoice(this IEndpointRouteBuilder app)
    {
        app.StaffGroup().MapGet("/voice/report", async (int? days, VoiceReportService s, CancellationToken ct) => Results.Json(await s.BuildAsync(days ?? 30, ct))).StaffCan(StaffPermissions.VoiceView);
        return app;
    }
}
