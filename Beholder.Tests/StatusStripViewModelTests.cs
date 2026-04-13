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
            ProcessPath = "fake/firefox.exe",
            ProcessName = "firefox.exe",
            TotalBytesIn = 1000,
            TotalBytesOut = 2000,
            DeltaBytesIn = 100,
            DeltaBytesOut = 200,
        });
        batch.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/chrome.exe",
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
            ProcessPath = "fake/test.exe",
            DeltaBytesOut = 1024,
            DeltaBytesIn = 2048,
        });

        vm.UpdateFromBatch(batch);

        Assert.Equal("1.0 KB/s", vm.OutboundRateLabel);
        Assert.Equal("2.0 KB/s", vm.InboundRateLabel);
    }

    [Fact]
    public void UpdateFromBatch_IdleState_HasTrafficIsFalse() {
        var vm = CreateViewModel();
        var batch = new CounterBatch();
        batch.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/test.exe",
            DeltaBytesOut = 0,
            DeltaBytesIn = 0,
        });

        vm.UpdateFromBatch(batch);

        Assert.False(vm.HasTraffic);
    }

    [Fact]
    public void UpdateFromBatch_OutboundHeavy_OutboundRatioHigher() {
        var vm = CreateViewModel();
        var batch = new CounterBatch();
        batch.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/test.exe",
            DeltaBytesOut = 900,
            DeltaBytesIn = 100,
        });

        vm.UpdateFromBatch(batch);

        Assert.True(vm.HasTraffic);
        Assert.True(vm.OutboundRatio > vm.InboundRatio);
    }

    [Fact]
    public void UpdateFromBatch_InboundHeavy_InboundRatioHigher() {
        var vm = CreateViewModel();
        var batch = new CounterBatch();
        batch.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/test.exe",
            DeltaBytesOut = 100,
            DeltaBytesIn = 900,
        });

        vm.UpdateFromBatch(batch);

        Assert.True(vm.HasTraffic);
        Assert.True(vm.InboundRatio > vm.OutboundRatio);
    }

    [Fact]
    public void UpdateFromBatch_Smoothing_DoesNotJumpInstantly() {
        var vm = CreateViewModel();
        var batch = new CounterBatch();
        batch.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/test.exe",
            DeltaBytesOut = 1000,
            DeltaBytesIn = 0,
        });

        vm.UpdateFromBatch(batch);

        // Smoothing starts from 0.5 and LERPs toward 1.0 with factor 0.3,
        // so after one tick: 0.5 * 0.7 + 1.0 * 0.3 = 0.65
        Assert.InRange(vm.OutboundRatio, 0.60, 0.70);
    }

    [Fact]
    public void UpdateFromBatch_SparseBatches_RetainsTotalsFromPriorProcesses() {
        var vm = CreateViewModel();

        var batch1 = new CounterBatch();
        batch1.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/chrome.exe",
            TotalBytesIn = 1000, TotalBytesOut = 500,
            DeltaBytesIn = 100, DeltaBytesOut = 50,
        });
        batch1.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/firefox.exe",
            TotalBytesIn = 2000, TotalBytesOut = 1000,
            DeltaBytesIn = 200, DeltaBytesOut = 100,
        });
        vm.UpdateFromBatch(batch1);

        var batch2 = new CounterBatch();
        batch2.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/firefox.exe",
            TotalBytesIn = 2500, TotalBytesOut = 1200,
            DeltaBytesIn = 500, DeltaBytesOut = 200,
        });
        vm.UpdateFromBatch(batch2);

        Assert.Equal("3.4 KB", vm.InboundTotalLabel);
        Assert.Equal("1.7 KB", vm.OutboundTotalLabel);
    }

    [Fact]
    public void UpdateFromBatch_ProcessUpdate_UpsertsNotAccumulates() {
        var vm = CreateViewModel();

        var batch1 = new CounterBatch();
        batch1.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/chrome.exe",
            TotalBytesIn = 1000, TotalBytesOut = 500,
        });
        vm.UpdateFromBatch(batch1);

        var batch2 = new CounterBatch();
        batch2.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/chrome.exe",
            TotalBytesIn = 2000, TotalBytesOut = 1000,
        });
        vm.UpdateFromBatch(batch2);

        Assert.Equal("2.0 KB", vm.InboundTotalLabel);
        Assert.Equal("1000 B", vm.OutboundTotalLabel);
    }

    [Fact]
    public void UpdateFromBatch_DaemonReset_ClearsAccumulator() {
        var vm = CreateViewModel();

        var batch1 = new CounterBatch();
        batch1.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/chrome.exe",
            TotalBytesIn = 50_000, TotalBytesOut = 25_000,
        });
        vm.UpdateFromBatch(batch1);

        var batch2 = new CounterBatch();
        batch2.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/chrome.exe",
            TotalBytesIn = 100, TotalBytesOut = 50,
        });
        vm.UpdateFromBatch(batch2);

        Assert.Equal("100 B", vm.InboundTotalLabel);
        Assert.Equal("50 B", vm.OutboundTotalLabel);
    }

    [Fact]
    public void UpdateFromBatch_RatesReflectCurrentBatchOnly() {
        var vm = CreateViewModel();

        var batch1 = new CounterBatch();
        batch1.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/chrome.exe",
            DeltaBytesIn = 500, DeltaBytesOut = 250,
        });
        vm.UpdateFromBatch(batch1);

        var batch2 = new CounterBatch();
        batch2.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/firefox.exe",
            DeltaBytesIn = 100, DeltaBytesOut = 50,
        });
        vm.UpdateFromBatch(batch2);

        Assert.Equal("100 B/s", vm.InboundRateLabel);
        Assert.Equal("50 B/s", vm.OutboundRateLabel);
    }

    [Fact]
    public void UpdateFromBatch_EmptyBatch_RetainsTotals() {
        var vm = CreateViewModel();

        var batch1 = new CounterBatch();
        batch1.Snapshots.Add(new CounterSnapshot {
            ProcessPath = "fake/chrome.exe",
            TotalBytesIn = 1000, TotalBytesOut = 500,
            DeltaBytesIn = 100, DeltaBytesOut = 50,
        });
        vm.UpdateFromBatch(batch1);

        var batch2 = new CounterBatch();
        vm.UpdateFromBatch(batch2);

        Assert.Equal("1000 B", vm.InboundTotalLabel);
        Assert.Equal("500 B", vm.OutboundTotalLabel);
        Assert.Equal("0 B/s", vm.InboundRateLabel);
        Assert.Equal("0 B/s", vm.OutboundRateLabel);
    }
}
