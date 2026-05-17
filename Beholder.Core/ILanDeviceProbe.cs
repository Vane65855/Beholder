namespace Beholder.Core;

/// <summary>
/// Discovers devices on the local subnet. Implementations probe via one or more
/// platform-specific protocols (ARP / mDNS / NetBIOS on Windows) and return the
/// merged observations as a single batch per scan.
/// </summary>
public interface ILanDeviceProbe {
    /// <summary>
    /// Performs one full scan of the local subnet. Returns one observation per
    /// device that responded. Returns an empty list when no NIC is available or
    /// the local network has no responders; per-IP probe failures are absorbed
    /// silently into "no observation for that IP." Throws only on fundamental
    /// setup failures (e.g. OS API unavailable on the running platform), which
    /// the calling scheduler logs and treats as a skipped tick.
    /// </summary>
    Task<IReadOnlyList<LanDeviceObservation>> ScanAsync(CancellationToken cancellationToken);
}
