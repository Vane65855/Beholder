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

    public static Core.AlertKind FromProto(this Local.AlertKind source)
        => (Core.AlertKind)(int)source;

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
    /// Maps a LAN device onto its wire equivalent. Null
    /// <see cref="Core.LanDevice.Vendor"/> / <see cref="Core.LanDevice.Hostname"/>
    /// become the empty string on the wire — proto3 has no nullable string
    /// primitive and the precedent (<see cref="Core.Alert.Summary"/> /
    /// <see cref="Core.DestinationSummary.Hostname"/>) is empty-string-as-null.
    /// Timestamps are emitted at millisecond precision via the shared
    /// <see cref="ToUnixTimeNanoseconds(DateTimeOffset)"/> helper.
    /// </summary>
    public static Local.LanDevice ToProto(this Core.LanDevice source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Local.LanDevice {
            Mac = source.Mac,
            Ip = source.Ip,
            Vendor = source.Vendor ?? "",
            Hostname = source.Hostname ?? "",
            FirstSeenUnixNs = source.FirstSeen.ToUnixTimeNanoseconds(),
            LastSeenUnixNs = source.LastSeen.ToUnixTimeNanoseconds(),
            Label = source.Label ?? "",
        };
    }

    /// <summary>
    /// Maps a wire LAN device to its domain equivalent. Empty-string
    /// <c>vendor</c> / <c>hostname</c> / <c>label</c> become null per the
    /// inverse of <see cref="ToProto(Core.LanDevice)"/>.
    /// </summary>
    public static Core.LanDevice ToDomain(this Local.LanDevice source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Core.LanDevice(
            Mac: source.Mac,
            Ip: source.Ip,
            Vendor: string.IsNullOrEmpty(source.Vendor) ? null : source.Vendor,
            Hostname: string.IsNullOrEmpty(source.Hostname) ? null : source.Hostname,
            FirstSeen: source.FirstSeenUnixNs.FromUnixTimeNanoseconds(),
            LastSeen: source.LastSeenUnixNs.FromUnixTimeNanoseconds(),
            Label: string.IsNullOrEmpty(source.Label) ? null : source.Label);
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
            AnchorSeq = source.AnchorSeq ?? 0,
            AnchorKeyId = source.AnchorKeyId ?? "",
        };
    }

    /// <summary>
    /// Maps a table stats row onto its wire equivalent.
    /// </summary>
    public static Local.TableStats ToProto(this Core.TableStats source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Local.TableStats {
            Name = source.Name,
            RowCount = source.RowCount,
        };
    }

    /// <summary>
    /// Maps a chain status snapshot onto its wire equivalent. Inverses are
    /// straightforward: null becomes the all-zero/empty shape with
    /// <c>has_chain_status = false</c> on the enclosing response (see
    /// <see cref="ToProto(Core.StorageStats)"/>).
    /// </summary>
    public static Local.ChainStatus ToProto(this Core.ChainStatus source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Local.ChainStatus {
            LastVerifiedUnixNs = source.LastVerifiedAt.ToUnixTimeNanoseconds(),
            IsValid = source.Result.IsValid,
            RowsVerified = source.Result.RowsVerified,
            FailedAtSeq = source.Result.FailedAtSeq ?? 0,
            ErrorMessage = source.Result.ErrorMessage ?? "",
        };
    }

    /// <summary>
    /// Maps a manual application-identity rule (Phase 13.6) onto its wire
    /// equivalent. The optional <see cref="Core.AppIdentityRule.DisplayName"/>
    /// becomes the empty string on the wire (proto3 has no nullable string
    /// primitive; the empty-string-as-null precedent matches
    /// <see cref="Core.Alert.Summary"/> / <see cref="Core.LanDevice.Hostname"/>).
    /// </summary>
    public static Local.AppIdentityRule ToProto(this Core.AppIdentityRule source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Local.AppIdentityRule {
            Id = source.Id,
            AnchorPath = source.AnchorPath,
            Filename = source.Filename,
            DisplayName = source.DisplayName ?? string.Empty,
            CreatedAtUnixNs = source.CreatedAt.ToUnixTimeNanoseconds(),
        };
    }

    /// <summary>
    /// Maps a Recording settings snapshot onto its wire value message.
    /// </summary>
    public static Local.RecordingSettingsValues ToProto(this Core.RecordingSettingsSnapshot source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Local.RecordingSettingsValues {
            FilterSelfTraffic = source.FilterSelfTraffic,
        };
    }

    /// <summary>
    /// Maps a wire Recording settings value message back to the domain snapshot.
    /// </summary>
    public static Core.RecordingSettingsSnapshot ToDomain(this Local.RecordingSettingsValues source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Core.RecordingSettingsSnapshot(source.FilterSelfTraffic);
    }

    /// <summary>
    /// Maps an Alert settings snapshot onto its wire value message (Phase 13.3).
    /// </summary>
    public static Local.AlertSettingsValues ToProto(this Core.AlertSettingsSnapshot source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Local.AlertSettingsValues {
            EnableNewProcessDetection = source.EnableNewProcessDetection,
            EnableHashChangeDetection = source.EnableHashChangeDetection,
            EnableChainIntegrityMonitor = source.EnableChainIntegrityMonitor,
        };
    }

    /// <summary>
    /// Maps a wire Alert settings value message back to the domain snapshot.
    /// </summary>
    public static Core.AlertSettingsSnapshot ToDomain(this Local.AlertSettingsValues source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Core.AlertSettingsSnapshot(
            EnableNewProcessDetection: source.EnableNewProcessDetection,
            EnableHashChangeDetection: source.EnableHashChangeDetection,
            EnableChainIntegrityMonitor: source.EnableChainIntegrityMonitor);
    }

    /// <summary>
    /// Maps a Scanner settings snapshot onto its wire value message (Phase 13.4).
    /// </summary>
    public static Local.ScannerSettingsValues ToProto(this Core.ScannerSettingsSnapshot source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Local.ScannerSettingsValues {
            EnableHostnameResolution = source.EnableHostnameResolution,
        };
    }

    /// <summary>
    /// Maps a wire Scanner settings value message back to the domain snapshot.
    /// </summary>
    public static Core.ScannerSettingsSnapshot ToDomain(this Local.ScannerSettingsValues source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Core.ScannerSettingsSnapshot(source.EnableHostnameResolution);
    }

    /// <summary>
    /// Maps a Hostname Resolution settings snapshot onto its wire value message.
    /// </summary>
    public static Local.HostnameResolutionSettingsValues ToProto(this Core.HostnameResolutionSettingsSnapshot source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Local.HostnameResolutionSettingsValues {
            EnablePreload = source.EnablePreload,
            EnableReverseDnsFallback = source.EnableReverseDnsFallback,
            EnableSniCapture = source.EnableSniCapture,
        };
    }

    /// <summary>
    /// Maps a wire Hostname Resolution settings value message back to the
    /// domain snapshot.
    /// </summary>
    public static Core.HostnameResolutionSettingsSnapshot ToDomain(this Local.HostnameResolutionSettingsValues source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Core.HostnameResolutionSettingsSnapshot(
            EnablePreload: source.EnablePreload,
            EnableReverseDnsFallback: source.EnableReverseDnsFallback,
            EnableSniCapture: source.EnableSniCapture);
    }

    /// <summary>
    /// Maps a storage stats snapshot onto its wire equivalent. The
    /// nullability of <see cref="Core.StorageStats.ChainStatus"/> is encoded
    /// via the <c>has_chain_status</c> boolean on the response so the UI can
    /// distinguish "never verified this session" (no value) from "verified
    /// successfully with zero rows" (real value with zero seq).
    /// </summary>
    public static Local.GetStorageStatsResponse ToProto(this Core.StorageStats source) {
        ArgumentNullException.ThrowIfNull(source);
        var response = new Local.GetStorageStatsResponse {
            DatabasePath = source.DatabasePath,
            DatabaseBytesTotal = source.DatabaseBytesTotal,
            HasChainStatus = source.ChainStatus is not null,
            ChainFirstEventUnixNs = source.ChainFirstEventAt?.ToUnixTimeNanoseconds() ?? 0,
            DaemonStartedUnixNs = source.DaemonStartedAt.ToUnixTimeNanoseconds(),
            LanDeviceCount = source.LanDeviceCount,
            LatestCheckpointSeq = source.LatestCheckpointSeq ?? 0,
            LatestCheckpointUnixNs = source.LatestCheckpointAt?.ToUnixTimeNanoseconds() ?? 0,
            LatestCheckpointKeyId = source.LatestCheckpointKeyId ?? "",
        };
        foreach (var table in source.Tables) response.Tables.Add(table.ToProto());
        if (source.ChainStatus is not null) response.ChainStatus = source.ChainStatus.ToProto();
        return response;
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

    /// <summary>Maps a protocol breakdown summary onto its wire equivalent.</summary>
    public static Local.ProtocolBreakdownSummary ToProto(this Core.ProtocolBreakdownSummary source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Local.ProtocolBreakdownSummary {
            ProtocolName = source.ProtocolName,
            Transport = source.Transport,
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

    /// <summary>Maps a wire protocol breakdown summary to its domain equivalent.</summary>
    public static Core.ProtocolBreakdownSummary ToDomain(this Local.ProtocolBreakdownSummary source) {
        ArgumentNullException.ThrowIfNull(source);
        return new Core.ProtocolBreakdownSummary(
            protocolName: source.ProtocolName,
            transport: source.Transport,
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
