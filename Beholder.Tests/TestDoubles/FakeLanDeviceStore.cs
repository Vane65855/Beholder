using Beholder.Core;

namespace Beholder.Tests;

/// <summary>
/// In-memory <see cref="ILanDeviceStore"/> for tests. Mirrors the persisted
/// store's invariants: MAC is the identity key; upsert preserves FirstSeen on
/// the existing row; ListAsync returns rows ordered by LastSeen descending and
/// honours the SeenSince + Limit filters from <see cref="LanDeviceQuery"/>.
/// Used by RPC handler tests and any other code path that wants real list /
/// upsert semantics without the SQLite dependency.
/// </summary>
internal sealed class FakeLanDeviceStore : ILanDeviceStore {
    private readonly Dictionary<string, LanDevice> _byMac = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, LanDevice> CurrentDevices => _byMac;

    public Task<LanDevice?> GetByMacAsync(string mac, CancellationToken cancellationToken) {
        _byMac.TryGetValue(mac, out var device);
        return Task.FromResult(device);
    }

    public Task<LanDevice?> GetByIpAsync(string ip, CancellationToken cancellationToken) {
        var match = _byMac.Values.FirstOrDefault(d => d.Ip == ip);
        return Task.FromResult(match);
    }

    public Task<IReadOnlyList<LanDevice>> ListAsync(LanDeviceQuery query, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(query);
        IEnumerable<LanDevice> rows = _byMac.Values;
        if (query.SeenSince is { } cutoff) {
            rows = rows.Where(d => d.LastSeen >= cutoff);
        }
        rows = rows.OrderByDescending(d => d.LastSeen);
        if (query.Limit > 0) {
            rows = rows.Take(query.Limit);
        }
        IReadOnlyList<LanDevice> result = rows.ToList();
        return Task.FromResult(result);
    }

    public Task UpsertAsync(LanDevice device, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(device);
        if (_byMac.TryGetValue(device.Mac, out var existing)) {
            // Mirror SqliteLanDeviceStore's ON CONFLICT clause: FirstSeen and
            // Label survive the upsert. Scanner re-observations carry null
            // for the label; the user's prior label persists.
            _byMac[device.Mac] = device with {
                FirstSeen = existing.FirstSeen,
                Label = device.Label ?? existing.Label,
            };
        } else {
            _byMac[device.Mac] = device;
        }
        return Task.CompletedTask;
    }

    public Task SetLabelAsync(string mac, string? label, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);
        var normalized = string.IsNullOrWhiteSpace(label) ? null : label;
        if (_byMac.TryGetValue(mac, out var existing)) {
            _byMac[mac] = existing with { Label = normalized };
        }
        // No-op when MAC isn't in the store, mirroring the SQL store.
        return Task.CompletedTask;
    }

    /// <summary>Test-only helper: seed a device directly without going through the upsert path.</summary>
    public void Seed(LanDevice device) {
        ArgumentNullException.ThrowIfNull(device);
        _byMac[device.Mac] = device;
    }

    /// <summary>Test-only helper: clear all devices.</summary>
    public void Clear() => _byMac.Clear();
}
