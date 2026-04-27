using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using Beholder.Ui.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public class FirewallActivityViewModelTests {
    private static (FirewallActivityViewModel Vm, FakeDaemonClient Client, DaemonStreamSubscriber Subscriber)
    CreateVm(GetFirewallActivityResponse? response = null) {
        var client = new FakeDaemonClient();
        if (response is not null) client.FirewallActivityResponse = response;
        var subscriber = new DaemonStreamSubscriber(
            client, TimeProvider.System, NullLogger<DaemonStreamSubscriber>.Instance);
        var vm = new FirewallActivityViewModel(client, subscriber);
        return (vm, client, subscriber);
    }

    [Fact]
    public async Task ActivateAsync_EmptyResponse_ShowsEmptyState() {
        var (vm, _, _) = CreateVm();

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(vm.Events);
        Assert.True(vm.ShowEmptyState);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task ActivateAsync_PopulatedResponse_AddsRows() {
        var response = new GetFirewallActivityResponse();
        response.Events.Add(new FirewallActivityEvent {
            Seq = 1,
            Kind = FirewallActivityKind.RuleCreated,
            ProcessPath = @"C:\bin\curl.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
            TimestampUnixNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L,
        });
        var (vm, _, _) = CreateVm(response);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        var single = Assert.Single(vm.Events);
        Assert.Equal(1, single.Seq);
        Assert.Equal("RULE", single.KindLabel);
        Assert.Contains("curl.exe", single.Description);
        Assert.True(vm.HasEvents);
        Assert.False(vm.ShowEmptyState);
    }

    [Fact]
    public async Task ActivateAsync_RpcFailure_SetsError() {
        var client = new FakeDaemonClient {
            FirewallActivityException = new InvalidOperationException("boom"),
        };
        var subscriber = new DaemonStreamSubscriber(
            client, TimeProvider.System, NullLogger<DaemonStreamSubscriber>.Instance);
        var vm = new FirewallActivityViewModel(client, subscriber);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.HasError);
        Assert.False(vm.ShowEmptyState);
    }

    [Fact]
    public async Task ActivateAsync_IsIdempotent() {
        var response = new GetFirewallActivityResponse();
        response.Events.Add(new FirewallActivityEvent {
            Seq = 1,
            Kind = FirewallActivityKind.RuleCreated,
            ProcessPath = @"C:\a.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
            TimestampUnixNs = 1_000_000_000L,
        });
        var (vm, _, _) = CreateVm(response);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Single(vm.Events);
    }

    [Fact]
    public void FirewallActivityRow_FromProto_RuleCreatedDecodesCorrectly() {
        var proto = new FirewallActivityEvent {
            Seq = 7,
            Kind = FirewallActivityKind.RuleCreated,
            ProcessPath = @"C:\app\firefox.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
            TimestampUnixNs = 1_000_000_000L,
        };

        var row = FirewallActivityRow.FromProto(proto);

        Assert.Equal(7, row.Seq);
        Assert.Equal("RULE", row.KindLabel);
        Assert.Equal("info", row.KindBadgeClass);
        Assert.Contains("firefox.exe", row.Description);
        Assert.Contains("out", row.Description);
        Assert.Contains("block", row.Description);
    }

    [Fact]
    public void FirewallActivityRow_FromProto_EnforcementToggleOff_UsesDangerBadge() {
        var proto = new FirewallActivityEvent {
            Seq = 1,
            Kind = FirewallActivityKind.EnforcementToggled,
            EnforcementEnabled = false,
            TimestampUnixNs = 1_000_000_000L,
        };

        var row = FirewallActivityRow.FromProto(proto);

        Assert.Equal("ENFORCE", row.KindLabel);
        Assert.Equal("danger", row.KindBadgeClass);
        Assert.Contains("OFF", row.Description);
    }

    [Fact]
    public void FirewallActivityRow_FromProto_EnforcementToggleOn_UsesInfoBadge() {
        var proto = new FirewallActivityEvent {
            Seq = 1,
            Kind = FirewallActivityKind.EnforcementToggled,
            EnforcementEnabled = true,
            TimestampUnixNs = 1_000_000_000L,
        };

        var row = FirewallActivityRow.FromProto(proto);

        Assert.Equal("info", row.KindBadgeClass);
        Assert.Contains("ON", row.Description);
    }

    [Fact]
    public void FirewallActivityRow_FromProto_RuleRemoved_UsesMutedBadge() {
        var proto = new FirewallActivityEvent {
            Seq = 1,
            Kind = FirewallActivityKind.RuleRemoved,
            ProcessPath = @"C:\a.exe",
            Direction = Direction.Outbound,
            TimestampUnixNs = 1_000_000_000L,
        };

        var row = FirewallActivityRow.FromProto(proto);

        Assert.Equal("muted", row.KindBadgeClass);
        Assert.Contains("removed", row.Description);
    }
}
