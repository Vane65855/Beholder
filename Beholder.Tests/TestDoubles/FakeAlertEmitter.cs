using Beholder.Core;
using Beholder.Daemon.Pipeline;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IAlertEmitter"/>. Records every emit call so
/// tests can assert kind, processPath, and summary; returns sequential
/// integer seqs starting at 1, which mirrors the real chain's behavior on a
/// fresh database. Set <see cref="Exception"/> to force the next emit call
/// to throw — used by failure-path tests.
/// </summary>
internal sealed class FakeAlertEmitter : IAlertEmitter {
    private long _nextSeq = 1;

    public List<AlertEmission> Emissions { get; } = new();
    public Exception? Exception { get; set; }

    public Task<long> EmitAlertAsync(
        AlertKind kind, string processPath, string summary, CancellationToken cancellationToken
    ) {
        if (Exception is not null) throw Exception;
        var seq = _nextSeq++;
        Emissions.Add(new AlertEmission(seq, kind, processPath, summary));
        return Task.FromResult(seq);
    }

    public sealed record AlertEmission(long Seq, AlertKind Kind, string ProcessPath, string Summary);
}
