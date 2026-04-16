using Core = Beholder.Core;
using Local = Beholder.Protocol.Local;

namespace Beholder.Protocol;

/// <summary>
/// Extension-method adapters that translate <see cref="Beholder.Core"/> domain
/// types to their generated <see cref="Beholder.Protocol.Local"/> protobuf
/// equivalents. Daemon-side only; the reverse direction (wire → domain) is
/// added in later phases as the UI and uplink client begin consuming events.
/// </summary>
internal static class ProtocolConverters {
    /// <summary>
    /// Converts a <see cref="DateTimeOffset"/> to Unix nanoseconds with
    /// millisecond precision (the IPC and storage convention). Avoids the
    /// ticks-arithmetic overflow hazards around extreme dates.
    /// </summary>
    public static long ToUnixTimeNanoseconds(this DateTimeOffset value)
        => value.ToUnixTimeMilliseconds() * 1_000_000L;

    // ---- Enum adapters (Core → Proto) ----

    public static Local.Direction ToProto(this Core.Direction source)
        => (Local.Direction)(int)source;

    public static Local.FirewallAction ToProto(this Core.FirewallAction source)
        => (Local.FirewallAction)(int)source;

    public static Local.RuleSource ToProto(this Core.RuleSource source)
        => (Local.RuleSource)(int)source;

    public static Local.AlertKind ToProto(this Core.AlertKind source)
        => (Local.AlertKind)(int)source;

    // ---- Enum adapters (Proto → Core) ----

    public static Core.Direction FromProto(this Local.Direction source)
        => (Core.Direction)(int)source;

    public static Core.FirewallAction FromProto(this Local.FirewallAction source)
        => (Core.FirewallAction)(int)source;

    public static Core.RuleSource FromProto(this Local.RuleSource source)
        => (Core.RuleSource)(int)source;

    // ---- Value-message adapters (Core → Proto) ----

    /// <summary>Maps a counter snapshot onto its wire equivalent.</summary>
    public static Local.CounterSnapshot ToProto(this Core.CounterSnapshot source) {
        ArgumentNullException.ThrowIfNull(source);
        var target = new Local.CounterSnapshot {
            ProcessName = source.ProcessName,
            ProcessPath = source.ProcessPath,
            TotalBytesIn = source.TotalBytesIn,
            TotalBytesOut = source.TotalBytesOut,
            DeltaBytesIn = source.DeltaBytesIn,
            DeltaBytesOut = source.DeltaBytesOut,
            ActiveConnectionCount = source.ActiveConnectionCount,
            TimestampUnixNs = source.Timestamp.ToUnixTimeNanoseconds(),
        };
        foreach (var kvp in source.BytesOutByCountry) {
            target.BytesOutByCountry[kvp.Key.Value] = kvp.Value;
        }
        return target;
    }

    /// <summary>Maps a firewall rule onto its wire equivalent.</summary>
    public static Local.FirewallRule ToProto(this Core.FirewallRule source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Local.FirewallRule {
            Id = source.Id,
            ProcessPath = source.ProcessPath,
            Direction = source.Direction.ToProto(),
            Action = source.Action.ToProto(),
            Source = source.Source.ToProto(),
            CreatedAtUnixNs = source.CreatedAt.ToUnixTimeNanoseconds(),
            UpdatedAtUnixNs = source.UpdatedAt.ToUnixTimeNanoseconds(),
        };
    }

    /// <summary>
    /// Maps an alert onto its wire equivalent. A null
    /// <see cref="Core.Alert.FirstViewedAt"/> becomes the sentinel 0 on the
    /// wire, matching the field comment in <c>beholder_local.proto</c>.
    /// </summary>
    public static Local.Alert ToProto(this Core.Alert source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Local.Alert {
            Seq = source.Seq,
            Kind = source.Kind.ToProto(),
            ProcessPath = source.ProcessPath,
            Summary = source.Summary,
            TimestampUnixNs = source.Timestamp.ToUnixTimeNanoseconds(),
            FirstViewedAtUnixNs = source.FirstViewedAt?.ToUnixTimeNanoseconds() ?? 0L,
        };
    }

    /// <summary>
    /// Maps a chain verification result onto its wire equivalent. A null
    /// <see cref="Core.ChainVerificationResult.FailedAtSeq"/> becomes the
    /// sentinel 0, and a null <see cref="Core.ChainVerificationResult.ErrorMessage"/>
    /// becomes the empty string — proto3 has no native nullable primitives.
    /// </summary>
    public static Local.VerifyChainResponse ToProto(this Core.ChainVerificationResult source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Local.VerifyChainResponse {
            IsValid = source.IsValid,
            RowsVerified = source.RowsVerified,
            FailedAtSeq = source.FailedAtSeq ?? 0,
            ErrorMessage = source.ErrorMessage ?? "",
        };
    }

    // ---- Traffic query adapters (Core → Proto) ----

    /// <summary>Maps a traffic time point onto its wire equivalent.</summary>
    public static Local.TrafficTimePoint ToProto(this Core.TrafficTimePoint source) {
        return new Local.TrafficTimePoint {
            TimestampUnixNs = source.Timestamp.ToUnixTimeNanoseconds(),
            BytesIn = source.BytesIn,
            BytesOut = source.BytesOut,
        };
    }

    /// <summary>Maps a destination summary onto its wire equivalent.</summary>
    public static Local.DestinationSummary ToProto(this Core.DestinationSummary source) {
        return new Local.DestinationSummary {
            RemoteAddress = source.RemoteAddress,
            Hostname = source.Hostname ?? "",
            Country = source.Country.ToString(),
            TotalBytesIn = source.TotalBytesIn,
            TotalBytesOut = source.TotalBytesOut,
            ConnectionCount = source.ConnectionCount,
        };
    }

    /// <summary>Maps a country traffic summary onto its wire equivalent.</summary>
    public static Local.CountryTrafficSummary ToProto(this Core.CountryTrafficSummary source) {
        return new Local.CountryTrafficSummary {
            Country = source.Country.ToString(),
            TotalBytesIn = source.TotalBytesIn,
            TotalBytesOut = source.TotalBytesOut,
        };
    }

    /// <summary>Maps a process traffic summary onto its wire equivalent.</summary>
    public static Local.ProcessTrafficSummaryProto ToProto(this Core.ProcessTrafficSummary source) {
        return new Local.ProcessTrafficSummaryProto {
            ProcessPath = source.ProcessPath,
            ProcessName = source.ProcessName,
            TotalBytesIn = source.TotalBytesIn,
            TotalBytesOut = source.TotalBytesOut,
        };
    }

    // ---- Traffic query adapters (Proto → Core) ----

    /// <summary>Converts Unix nanoseconds (millisecond precision) back to a DateTimeOffset.</summary>
    public static DateTimeOffset FromUnixTimeNanoseconds(this long unixNs)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixNs / 1_000_000L);

    /// <summary>Maps a wire traffic time point to its domain equivalent.</summary>
    public static Core.TrafficTimePoint ToDomain(this Local.TrafficTimePoint source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Core.TrafficTimePoint(
            timestamp: source.TimestampUnixNs.FromUnixTimeNanoseconds(),
            bytesIn: source.BytesIn,
            bytesOut: source.BytesOut);
    }

    /// <summary>Maps a wire destination summary to its domain equivalent.</summary>
    public static Core.DestinationSummary ToDomain(this Local.DestinationSummary source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Core.DestinationSummary(
            remoteAddress: source.RemoteAddress,
            hostname: string.IsNullOrEmpty(source.Hostname) ? null : source.Hostname,
            country: ParseCountryCode(source.Country),
            totalBytesIn: source.TotalBytesIn,
            totalBytesOut: source.TotalBytesOut,
            connectionCount: source.ConnectionCount);
    }

    /// <summary>Maps a wire country traffic summary to its domain equivalent.</summary>
    public static Core.CountryTrafficSummary ToDomain(this Local.CountryTrafficSummary source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Core.CountryTrafficSummary(
            country: ParseCountryCode(source.Country),
            totalBytesIn: source.TotalBytesIn,
            totalBytesOut: source.TotalBytesOut);
    }

    private static Core.CountryCode ParseCountryCode(string value) {
        return value switch {
            "--" => Core.CountryCode.Local,
            "??" => Core.CountryCode.Unknown,
            _ => Core.CountryCode.FromAlpha2(value)
        };
    }
}
