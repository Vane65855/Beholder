using Beholder.Protocol.Local;
using Beholder.Ui.Models;
using Beholder.Ui.Services;

namespace Beholder.Tests;

public class HistoricalChartLoaderTests {
    private static readonly TimeRangeSelection ShortRange =
        TimeRangeSelection.FromPreset(TimeRangePreset.Last1Hour);

    [Fact]
    public async Task LoadRangeAsync_EmptyTimeline_SkipsSummariesAndReturnsEmptyResult() {
        // When the aggregate timeline response is empty, the loader must not
        // fire the GetProcessSummaries RPC — the caller's empty-state render
        // doesn't need summaries, and skipping the second round-trip matters
        // on slow daemon links.
        var client = new FakeDaemonClient {
            AggregateTimelineResponse = new GetAggregateTimelineResponse(), // empty
            // Seed a summary response; the loader should NOT request it.
            ProcessSummariesResponse = new GetProcessSummariesResponse {
                Summaries = { new ProcessTrafficSummaryProto { ProcessPath = "sentinel" } },
            },
        };
        var loader = new HistoricalChartLoader(client);

        var result = await loader.LoadRangeAsync(ShortRange, CancellationToken.None);

        Assert.Empty(result.Points);
        Assert.Empty(result.Summaries);
        Assert.True(result.ResolutionMs > 0);
    }

    [Fact]
    public async Task LoadRangeAsync_PopulatedTimeline_ReturnsPointsAndSummaries() {
        var timeline = new GetAggregateTimelineResponse();
        timeline.Points.Add(new TrafficTimePoint {
            TimestampUnixNs = 1_000_000,
            BytesIn = 100,
            BytesOut = 50,
        });
        var summaries = new GetProcessSummariesResponse();
        summaries.Summaries.Add(new ProcessTrafficSummaryProto {
            ProcessPath = "firefox.exe",
            ProcessName = "firefox.exe",
            TotalBytesIn = 1_000,
            TotalBytesOut = 500,
        });
        var client = new FakeDaemonClient {
            AggregateTimelineResponse = timeline,
            ProcessSummariesResponse = summaries,
        };
        var loader = new HistoricalChartLoader(client);

        var result = await loader.LoadRangeAsync(ShortRange, CancellationToken.None);

        Assert.Single(result.Points);
        Assert.Equal(100, result.Points[0].BytesIn);
        Assert.Single(result.Summaries);
        Assert.Equal("firefox.exe", result.Summaries[0].ProcessPath);
    }

    [Fact]
    public async Task LoadProcessChartAsync_NullProcessPath_UsesAggregateTimeline() {
        // A null processPath ("All processes" selection) must route through
        // GetAggregateTimeline rather than GetProcessTimeline. The fake's
        // ProcessTimelineResponder captures the request; if it fires, the
        // test fails. The aggregate responder returns a sentinel point to
        // prove the aggregate path was taken.
        var aggregateCalled = false;
        var processCalled = false;
        var client = new FakeDaemonClient {
            AggregateTimelineResponder = (_, _) => {
                aggregateCalled = true;
                var response = new GetAggregateTimelineResponse();
                response.Points.Add(new TrafficTimePoint {
                    TimestampUnixNs = 1,
                    BytesIn = 42,
                });
                return response;
            },
            ProcessTimelineResponder = _ => {
                processCalled = true;
                return new GetProcessTimelineResponse();
            },
        };
        var loader = new HistoricalChartLoader(client);

        var result = await loader.LoadProcessChartAsync(
            ShortRange, processPath: null, CancellationToken.None);

        Assert.True(aggregateCalled);
        Assert.False(processCalled);
        Assert.Single(result.Points);
        Assert.Equal(42, result.Points[0].BytesIn);
    }

    [Fact]
    public async Task LoadProcessChartAsync_WithProcessPath_UsesProcessTimeline() {
        // A non-null processPath (specific process selected) must route through
        // GetProcessTimeline with the ProcessPath field populated, not through
        // GetAggregateTimeline.
        var aggregateCalled = false;
        string? capturedProcessPath = null;
        var client = new FakeDaemonClient {
            AggregateTimelineResponder = (_, _) => {
                aggregateCalled = true;
                return new GetAggregateTimelineResponse();
            },
            ProcessTimelineResponder = request => {
                capturedProcessPath = request.ProcessPath;
                var response = new GetProcessTimelineResponse();
                response.Points.Add(new TrafficTimePoint {
                    TimestampUnixNs = 1,
                    BytesIn = 99,
                });
                return response;
            },
        };
        var loader = new HistoricalChartLoader(client);

        var result = await loader.LoadProcessChartAsync(
            ShortRange, processPath: "C:/app/chrome.exe", CancellationToken.None);

        Assert.False(aggregateCalled);
        Assert.Equal("C:/app/chrome.exe", capturedProcessPath);
        Assert.Single(result.Points);
        Assert.Equal(99, result.Points[0].BytesIn);
    }
}
