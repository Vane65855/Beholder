using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="ILanDeviceProbe"/>. Test sets
/// <see cref="Responder"/> to control the observations the next
/// <see cref="ScanAsync"/> returns; defaults to an empty list. Mirrors the
/// <c>FakeFlowSource</c> / <c>FakeReverseDnsResolver</c> shape — minimal,
/// controllable, no real I/O.
/// </summary>
internal sealed class FakeLanDeviceProbe : ILanDeviceProbe {
    public Func<CancellationToken, Task<IReadOnlyList<LanDeviceObservation>>>? Responder { get; set; }
    public int ScanCount;

    public Task<IReadOnlyList<LanDeviceObservation>> ScanAsync(CancellationToken cancellationToken) {
        Interlocked.Increment(ref ScanCount);
        return Responder?.Invoke(cancellationToken)
            ?? Task.FromResult<IReadOnlyList<LanDeviceObservation>>([]);
    }
}
