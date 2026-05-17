using System.Net;
using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IHostnameProbe"/>. Tests set
/// <see cref="Responder"/> to control what each per-IP call returns;
/// defaults to returning null. <see cref="CallCount"/> tracks total
/// invocations. Mirrors the <c>FakeLanDeviceProbe</c> shape.
/// </summary>
internal sealed class FakeHostnameProbe : IHostnameProbe {
    public FakeHostnameProbe(string protocolName) {
        ProtocolName = protocolName;
    }

    public string ProtocolName { get; }

    public Func<IPAddress, CancellationToken, Task<string?>>? Responder { get; set; }
    public int CallCount;

    public Task<string?> ResolveAsync(IPAddress ip, CancellationToken cancellationToken) {
        Interlocked.Increment(ref CallCount);
        return Responder?.Invoke(ip, cancellationToken) ?? Task.FromResult<string?>(null);
    }
}
