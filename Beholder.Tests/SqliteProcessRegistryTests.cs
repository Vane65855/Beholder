using Beholder.Core;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public sealed class SqliteProcessRegistryTests : IDisposable {
    private static readonly DateTimeOffset DefaultTimestamp = new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly SqliteProcessRegistry _registry;

    public SqliteProcessRegistryTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();
        _registry = new SqliteProcessRegistry(new ConnectionFactory(_databasePath, pooling: false));
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Constructor_NullConnectionFactory_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => new SqliteProcessRegistry(null!));
    }

    [Fact]
    public async Task GetByPathAsync_NullPath_ThrowsArgumentNullException() {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _registry.GetByPathAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task RegisterAsync_NullInfo_ThrowsArgumentNullException() {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _registry.RegisterAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task RegisterAsync_NewProcess_Inserts() {
        var info = MakeProcessInfo();

        await _registry.RegisterAsync(info, CancellationToken.None);

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(info.Path, fetched.Path);
        Assert.Equal(info.DisplayName, fetched.DisplayName);
        Assert.Equal(info.Sha256, fetched.Sha256);
        Assert.Equal(info.FirstSeen, fetched.FirstSeen);
        Assert.Equal(info.LastSeen, fetched.LastSeen);
        Assert.Equal(info.LastHashedAt, fetched.LastHashedAt);
    }

    [Fact]
    public async Task RegisterAsync_ExistingProcess_UpdatesAllMutableColumns() {
        var firstTime = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var laterTime = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero);
        var laterHashedAt = new DateTimeOffset(2026, 4, 10, 1, 0, 0, TimeSpan.Zero);
        var newHash = new byte[32];
        Array.Fill(newHash, (byte)0x01);

        await _registry.RegisterAsync(
            MakeProcessInfo(
                displayName: "curl",
                sha256: null,
                lastSeen: firstTime,
                lastHashedAt: null),
            CancellationToken.None
        );
        await _registry.RegisterAsync(
            MakeProcessInfo(
                displayName: "curl-updated",
                sha256: newHash,
                lastSeen: laterTime,
                lastHashedAt: laterHashedAt),
            CancellationToken.None
        );

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal("curl-updated", fetched.DisplayName);
        Assert.Equal(newHash, fetched.Sha256);
        Assert.Equal(laterTime, fetched.LastSeen);
        Assert.Equal(laterHashedAt, fetched.LastHashedAt);
    }

    [Fact]
    public async Task RegisterAsync_PreservesFirstSeen_OnUpdate() {
        var originalFirstSeen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var attemptedNewFirstSeen = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await _registry.RegisterAsync(
            MakeProcessInfo(firstSeen: originalFirstSeen),
            CancellationToken.None
        );
        await _registry.RegisterAsync(
            MakeProcessInfo(firstSeen: attemptedNewFirstSeen),
            CancellationToken.None
        );

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(originalFirstSeen, fetched.FirstSeen);
    }

    [Fact]
    public async Task GetByPathAsync_NotFound_ReturnsNull() {
        var fetched = await _registry.GetByPathAsync("/never/registered", CancellationToken.None);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task GetByPathAsync_WithSha256_RoundTrips() {
        var hash = new byte[32];
        Array.Fill(hash, (byte)0xAB);

        await _registry.RegisterAsync(MakeProcessInfo(sha256: hash), CancellationToken.None);

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.NotNull(fetched.Sha256);
        Assert.True(hash.AsSpan().SequenceEqual(fetched.Sha256));
    }

    [Fact]
    public async Task GetByPathAsync_WithNullSha256_RoundTrips() {
        await _registry.RegisterAsync(MakeProcessInfo(sha256: null), CancellationToken.None);

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Null(fetched.Sha256);
    }

    [Fact]
    public async Task GetByPathAsync_WithLastHashedAt_RoundTrips() {
        var hashedAt = new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);

        await _registry.RegisterAsync(MakeProcessInfo(lastHashedAt: hashedAt), CancellationToken.None);

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(hashedAt, fetched.LastHashedAt);
    }

    [Fact]
    public async Task GetByPathAsync_WithNullLastHashedAt_RoundTrips() {
        await _registry.RegisterAsync(MakeProcessInfo(lastHashedAt: null), CancellationToken.None);

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Null(fetched.LastHashedAt);
    }

    [Fact]
    public async Task ListAllAsync_MultipleProcesses_ReturnsAllOrderedByLastSeen() {
        var t1 = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 4, 5, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero);

        await _registry.RegisterAsync(MakeProcessInfo(path: "/usr/bin/curl", lastSeen: t1), CancellationToken.None);
        await _registry.RegisterAsync(MakeProcessInfo(path: "/usr/bin/wget", lastSeen: t2), CancellationToken.None);
        await _registry.RegisterAsync(MakeProcessInfo(path: "/usr/bin/ssh", lastSeen: t3), CancellationToken.None);

        var all = await _registry.ListAllAsync(CancellationToken.None);

        Assert.Equal(3, all.Count);
        Assert.Equal("/usr/bin/ssh", all[0].Path);
        Assert.Equal("/usr/bin/wget", all[1].Path);
        Assert.Equal("/usr/bin/curl", all[2].Path);
    }

    [Fact]
    public async Task ListAllAsync_EmptyTable_ReturnsEmptyList() {
        var all = await _registry.ListAllAsync(CancellationToken.None);

        Assert.NotNull(all);
        Assert.Empty(all);
    }

    [Fact]
    public async Task RegisterAsync_DefensiveCopySha256_MutatingOriginalDoesNotAffectStored() {
        var original = new byte[32];
        Array.Fill(original, (byte)0x11);

        await _registry.RegisterAsync(MakeProcessInfo(sha256: original), CancellationToken.None);
        original[0] = 0xFF;

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.NotNull(fetched.Sha256);
        Assert.Equal((byte)0x11, fetched.Sha256[0]);
    }

    // ---- Phase 7.5: identity columns + FindByLogicalIdentity ----

    [Fact]
    public async Task RegisterAsync_StoresIdentityColumns_RoundTrip() {
        var info = MakeProcessInfo(
            companyName: "Discord, Inc.",
            productName: "Discord",
            installRoot: @"C:\Users\X\AppData\Local\Discord",
            certSubjectCn: "CN=Discord Inc.",
            certIssuerCn: "CN=DigiCert",
            signatureStatus: SignatureValidationStatus.Valid);

        await _registry.RegisterAsync(info, CancellationToken.None);

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal("Discord, Inc.", fetched.CompanyName);
        Assert.Equal("Discord", fetched.ProductName);
        Assert.Equal(@"C:\Users\X\AppData\Local\Discord", fetched.InstallRoot);
        Assert.Equal("CN=Discord Inc.", fetched.CertSubjectCn);
        Assert.Equal("CN=DigiCert", fetched.CertIssuerCn);
        Assert.Equal(SignatureValidationStatus.Valid, fetched.SignatureStatus);
    }

    [Fact]
    public async Task RegisterAsync_NullIdentityColumns_RoundTripAsNull() {
        // Pre-7.5 row equivalent: no identity metadata. Defaults already
        // null on MakeProcessInfo overload.
        var info = MakeProcessInfo();

        await _registry.RegisterAsync(info, CancellationToken.None);

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Null(fetched.CompanyName);
        Assert.Null(fetched.ProductName);
        Assert.Null(fetched.InstallRoot);
        Assert.Null(fetched.CertSubjectCn);
        Assert.Null(fetched.CertIssuerCn);
        Assert.Null(fetched.SignatureStatus);
    }

    [Fact]
    public async Task FindByLogicalIdentityAsync_MatchingIdentity_ReturnsRow() {
        await _registry.RegisterAsync(MakeProcessInfo(
            path: @"C:\Users\X\AppData\Local\Discord\app-1.0.9225\Discord.exe",
            companyName: "Discord, Inc.", productName: "Discord",
            installRoot: @"C:\Users\X\AppData\Local\Discord"), CancellationToken.None);

        var found = await _registry.FindByLogicalIdentityAsync(
            "Discord, Inc.", "Discord", @"C:\Users\X\AppData\Local\Discord",
            CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(@"C:\Users\X\AppData\Local\Discord\app-1.0.9225\Discord.exe", found.Path);
    }

    [Fact]
    public async Task FindByLogicalIdentityAsync_NoMatch_ReturnsNull() {
        await _registry.RegisterAsync(MakeProcessInfo(
            path: @"C:\Users\X\AppData\Local\Discord\app-1.0.9225\Discord.exe",
            companyName: "Discord, Inc.", productName: "Discord",
            installRoot: @"C:\Users\X\AppData\Local\Discord"), CancellationToken.None);

        var found = await _registry.FindByLogicalIdentityAsync(
            "Discord, Inc.", "Discord", @"C:\Program Files\Discord",
            CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task FindByLogicalIdentityAsync_InstallRootCaseInsensitive_StillMatches() {
        // Windows paths are case-insensitive. The query uses COLLATE NOCASE
        // on install_root so a registered row with mixed-case path is still
        // findable from a lookup using a different case.
        await _registry.RegisterAsync(MakeProcessInfo(
            path: @"C:\Users\X\AppData\Local\Discord\app-1.0.9225\Discord.exe",
            companyName: "Discord, Inc.", productName: "Discord",
            installRoot: @"C:\Users\X\AppData\Local\Discord"), CancellationToken.None);

        var found = await _registry.FindByLogicalIdentityAsync(
            "Discord, Inc.", "Discord", @"c:\users\x\appdata\local\discord",
            CancellationToken.None);

        Assert.NotNull(found);
    }

    [Fact]
    public async Task Initialize_SchemaMigration_IsIdempotent() {
        // Call Initialize twice on the same database. Second call must
        // succeed without errors — the ALTER TABLE migration is gated on
        // existing column presence.
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();

        // Sanity check: the registry still works after re-init.
        var info = await _registry.GetByPathAsync("/never/registered", CancellationToken.None);
        Assert.Null(info);
    }

    private static ProcessInfo MakeProcessInfo(
        string path = "/usr/bin/curl",
        string displayName = "curl",
        byte[]? sha256 = null,
        DateTimeOffset? firstSeen = null,
        DateTimeOffset? lastSeen = null,
        DateTimeOffset? lastHashedAt = null,
        string? companyName = null,
        string? productName = null,
        string? installRoot = null,
        string? certSubjectCn = null,
        string? certIssuerCn = null,
        SignatureValidationStatus? signatureStatus = null
    ) => new ProcessInfo(
        path: path,
        displayName: displayName,
        sha256: sha256,
        firstSeen: firstSeen ?? DefaultTimestamp,
        lastSeen: lastSeen ?? DefaultTimestamp,
        lastHashedAt: lastHashedAt,
        companyName: companyName,
        productName: productName,
        installRoot: installRoot,
        certSubjectCn: certSubjectCn,
        certIssuerCn: certIssuerCn,
        signatureStatus: signatureStatus
    );
}
