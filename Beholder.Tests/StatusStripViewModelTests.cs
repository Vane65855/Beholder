using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using Beholder.Ui.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public class StatusStripViewModelTests {
    private static StatusStripViewModel CreateViewModel() {
        var fakeClient = new FakeDaemonClient();
        var subscriber = new DaemonStreamSubscriber(
            fakeClient,
            NullLogger<DaemonStreamSubscriber>.Instance);
        return new StatusStripViewModel(subscriber);
    }

    [Fact]
    public void Ctor_NullSubscriber_Throws() =>
        Assert.Throws<ArgumentNullException>("subscriber",
            () => new StatusStripViewModel(null!));

    [Fact]
    public void UpdateFromBatch_AggregatesAcrossProcesses_UpdatesTotals() {
        var vm = CreateViewModel();
        var batch = new CounterBatch();
        batch.Snapshots.Add(new CounterSnapshot {
            ProcessName = "firefox.exe",
            TotalBytesIn = 1000,
            TotalBytesOut = 2000,
            DeltaBytesIn = 100,
            DeltaBytesOut = 200,
        });
        batch.Snapshots.Add(new CounterSnapshot {
            ProcessName = "chrome.exe",
            TotalBytesIn = 3000,
            TotalBytesOut = 4000,
            DeltaBytesIn = 300,
            DeltaBytesOut = 400,
        });

        vm.UpdateFromBatch(batch);

        Assert.Equal("5.9 KB", vm.OutboundTotalLabel);
        Assert.Equal("3.9 KB", vm.InboundTotalLabel);
        Assert.Equal("9.8 KB", vm.WanTotalLabel);
    }

    [Fact]
    public void UpdateFromBatch_UpdatesRateLabels() {
        var vm = CreateViewModel();
        var batch = new CounterBatch();
        batch.Snapshots.Add(new CounterSnapshot {
            DeltaBytesOut = 1024,
            DeltaBytesIn = 2048,
        });

        vm.UpdateFromBatch(batch);

        Assert.Equal("1.0 KB/s", vm.OutboundRateLabel);
        Assert.Equal("2.0 KB/s", vm.InboundRateLabel);
    }

    [Fact]
    public void WanThroughputHistory_CapsAt60Samples() {
        var vm = CreateViewModel();

        for (int i = 0; i < 100; i++) {
            var batch = new CounterBatch();
            batch.Snapshots.Add(new CounterSnapshot {
                DeltaBytesIn = i * 100,
                DeltaBytesOut = i * 100,
            });
            vm.UpdateFromBatch(batch);
        }

        Assert.Equal(60, vm.SparklineSampleCount);
    }

    [Fact]
    public void UpdateFromBatch_GeneratesSparklinePoints() {
        var vm = CreateViewModel();

        for (int i = 0; i < 5; i++) {
            var batch = new CounterBatch();
            batch.Snapshots.Add(new CounterSnapshot {
                DeltaBytesIn = (i + 1) * 100,
                DeltaBytesOut = 0,
            });
            vm.UpdateFromBatch(batch);
        }

        Assert.False(string.IsNullOrEmpty(vm.WanSparklinePoints));
        Assert.Contains(",", vm.WanSparklinePoints);
    }
}
