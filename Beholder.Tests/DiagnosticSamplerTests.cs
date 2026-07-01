using Beholder.Daemon;
using Beholder.Daemon.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Beholder.Tests;

/// <summary>
/// Covers the opt-in diagnostic sampler (Phase 12.3): a sample reads storage
/// stats and is failure-tolerant, and the sampler stays silent when disabled.
/// The timer cadence is .NET's PeriodicTimer; the per-tick work is SampleAsync,
/// exercised directly here. The 24-hour soak itself is a manual run.
/// </summary>
public class DiagnosticSamplerTests {
    private static DiagnosticSampler Build(FakeStorageStatsProvider stats, bool enabled) =>
        new(Options.Create(new DiagnosticsOptions { Enabled = enabled, IntervalSeconds = 1 }),
            stats, TimeProvider.System, NullLogger<DiagnosticSampler>.Instance);

    [Fact]
    public async Task SampleAsync_ReadsStorageStats() {
        var stats = new FakeStorageStatsProvider();
        var sampler = Build(stats, enabled: true);

        await sampler.SampleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, stats.CallCount);
    }

    [Fact]
    public async Task SampleAsync_WhenStorageStatsThrow_DoesNotPropagate() {
        var stats = new FakeStorageStatsProvider { Exception = new InvalidOperationException("db locked") };
        var sampler = Build(stats, enabled: true);

        await sampler.SampleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, stats.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_NeverSamples() {
        var stats = new FakeStorageStatsProvider();
        var sampler = Build(stats, enabled: false);

        await sampler.StartAsync(TestContext.Current.CancellationToken);
        await sampler.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, stats.CallCount);
    }
}
