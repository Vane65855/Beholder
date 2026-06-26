using System.Text;
using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="IChainExporter"/> for RPC tests that only care that
/// the handler reads the range and ferries the exporter's bytes + count back —
/// not the real signed-envelope format. Records the last call's arguments and
/// returns a trivial deterministic payload. Tests that exercise the real
/// envelope + signature use the production <c>ChainExporter</c> with a
/// <see cref="FakeCheckpointKeyProvider"/> directly.
/// </summary>
internal sealed class FakeChainExporter : IChainExporter {
    public int CallCount { get; private set; }
    public long LastFromSeq { get; private set; }
    public long LastToSeq { get; private set; }
    public int LastRowCount { get; private set; }

    /// <summary>Optional hook: when set, Export throws this.</summary>
    public Exception? Exception { get; set; }

    public byte[] Export(
        IReadOnlyList<EventLogRow> rows, long fromSeq, long toSeq,
        DateTimeOffset exportedAt, string daemonVersion
    ) {
        if (Exception is not null) throw Exception;
        CallCount++;
        LastFromSeq = fromSeq;
        LastToSeq = toSeq;
        LastRowCount = rows.Count;
        return Encoding.UTF8.GetBytes($"{{\"fake\":true,\"events\":{rows.Count}}}");
    }
}
