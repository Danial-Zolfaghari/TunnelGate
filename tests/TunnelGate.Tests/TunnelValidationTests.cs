using TunnelGate.Core;
using TunnelGate.Models;

namespace TunnelGate.Tests;

public sealed class TunnelValidationTests
{
    [Theory]
    [InlineData("localhost", true)]
    [InlineData("example.com", true)]
    [InlineData("192.0.2.10", true)]
    [InlineData("[2001:db8::1]", true)]
    [InlineData("-bad.example", false)]
    [InlineData("", false)]
    public void ValidHost_ReturnsExpected(string value, bool expected)
        => Assert.Equal(expected, TunnelValidation.ValidHost(value));

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::", true)]
    [InlineData("example.com", true)]
    [InlineData("bad host", false)]
    public void ValidBind_ReturnsExpected(string value, bool expected)
        => Assert.Equal(expected, TunnelValidation.ValidBind(value));

    [Fact]
    public void ValidProxyConfiguration_Passes()
    {
        var settings = new ProfileSettings { Reconnect = true, ReconnectDelay = 5 };
        var tunnel = new TunnelDefinition
        {
            Mode = "proxy",
            RemoteHost = "example.com",
            RemoteSshPort = 22,
            RemoteUser = "user",
            Password = "test-only-password",
            LocalHost = "127.0.0.1",
            LocalPort = 1080,
        };

        Assert.Null(TunnelValidation.Validate(settings, tunnel));
    }

    [Fact]
    public void MissingAuthentication_IsRejected()
    {
        var settings = new ProfileSettings();
        var tunnel = new TunnelDefinition
        {
            RemoteHost = "example.com",
            RemoteUser = "user",
            RemoteBind = "127.0.0.1",
            RemotePort = 9000,
            LocalHost = "127.0.0.1",
            LocalPort = 9001,
        };

        Assert.Equal("Password or key file is required.", TunnelValidation.Validate(settings, tunnel));
    }

    [Theory]
    [InlineData("0.0.0.0", "127.0.0.1")]
    [InlineData("::", "127.0.0.1")]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("192.0.2.5", "192.0.2.5")]
    public void ForwardDestinationHost_NormalizesWildcardAndLocalhost(string input, string expected)
        => Assert.Equal(expected, TunnelDefinition.ForwardDestinationHost(input));

    [Fact]
    public void AutoRestartInterval_IsCalculatedInSeconds()
    {
        var tunnel = new TunnelDefinition
        {
            AutoRestart = true,
            AutoRestartDays = 1,
            AutoRestartHours = 2,
            AutoRestartMinutes = 3,
        };

        Assert.Equal(93780, tunnel.AutoRestartIntervalSeconds());
    }
}
