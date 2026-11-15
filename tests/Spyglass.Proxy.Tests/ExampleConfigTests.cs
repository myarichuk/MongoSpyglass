using Spyglass.Proxy.Config;
using Xunit;

namespace Spyglass.Proxy.Tests;

/// <summary>The files in deploy/examples are documentation people copy: they must load as written.</summary>
public sealed class ExampleConfigTests
{
    private static string Examples
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var v = Path.Combine(d.FullName, "deploy", "examples");
                if (Directory.Exists(v)) return v;
            }
            throw new DirectoryNotFoundException("deploy/examples");
        }
    }

    [Fact]
    public void Local_example_loads()
    {
        var c = ConfigLoader.Load(["--config", Path.Combine(Examples, "proxy.local.yaml")],
            new Dictionary<string, string?> { ["SPYGLASS_TOKEN"] = "t" });
        Assert.Equal(27018, c.Listen.BasePort);
        Assert.Equal(DownstreamTlsMode.None, c.Listen.Tls.Mode);
        Assert.Equal("t", c.Collector.Token);
    }

    [Fact]
    public void Staging_example_loads_with_its_password_variable_as_a_value_not_a_key()
    {
        // SPYGLASS_PROXY_KEY_PASSWORD is referenced as ${...} by the file: it must not be read as an (unknown) config key
        var c = ConfigLoader.Load(["--config", Path.Combine(Examples, "proxy.staging.yaml")],
            new Dictionary<string, string?> { ["SPYGLASS_TOKEN"] = "t", ["SPYGLASS_PROXY_KEY_PASSWORD"] = "pw" });
        Assert.Equal(DownstreamTlsMode.Tls, c.Listen.Tls.Mode);
        Assert.Equal("pw", c.Listen.Tls.KeyPassword);
        Assert.Equal("/etc/spyglass/certs/corp-ca.pem", c.Upstream.Tls.CaFile);
        Assert.Equal("/etc/spyglass/certs/corp-ca.pem", c.Collector.CaFile);
        Assert.Equal(TriState.True, c.Upstream.Tls.Enabled);
    }

    [Fact]
    public void An_unreferenced_unknown_spyglass_variable_is_still_an_error()
    {
        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Load(["--upstream.uri", "mongodb://h:1/"],
            new Dictionary<string, string?> { ["SPYGLASS_PROXY_KEY_PASSWORD"] = "pw" }));
        Assert.DoesNotContain("pw", e.Message);
    }
}
