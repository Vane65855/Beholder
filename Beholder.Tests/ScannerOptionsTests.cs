using Beholder.Daemon;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Beholder.Tests;

public sealed class ScannerOptionsTests {
    [Fact]
    public void Configure_NoSection_AppliesDefaults() {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.Configure<ScannerOptions>(config.GetSection("Scanner"));
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<ScannerOptions>>().Value;

        Assert.Equal(300, options.ScanIntervalSeconds);
    }

    [Fact]
    public void Configure_SectionOverridesDefault() {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Scanner:ScanIntervalSeconds"] = "60",
            })
            .Build();
        var services = new ServiceCollection();
        services.Configure<ScannerOptions>(config.GetSection("Scanner"));
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<ScannerOptions>>().Value;

        Assert.Equal(60, options.ScanIntervalSeconds);
    }
}
