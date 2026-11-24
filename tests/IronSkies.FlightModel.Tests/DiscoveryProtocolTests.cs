using IronSkies.FlightModel.Net;

namespace IronSkies.FlightModel.Tests;

public sealed class DiscoveryProtocolTests
{
    [Fact]
    public void QueryRoundTrips()
    {
        byte[] query = DiscoveryProtocol.EncodeQuery();
        Assert.True(DiscoveryProtocol.IsQuery(query));
        Assert.False(DiscoveryProtocol.IsQuery(Array.Empty<byte>()));
        Assert.False(DiscoveryProtocol.IsQuery(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void ResponseRoundTrips()
    {
        byte[] data = DiscoveryProtocol.EncodeResponse(
            "Normandy Training", 7777, 12, 64);

        Assert.True(DiscoveryProtocol.TryDecodeResponse(
            data, out string name, out int port, out int players, out int maxPlayers));
        Assert.Equal("Normandy Training", name);
        Assert.Equal(7777, port);
        Assert.Equal(12, players);
        Assert.Equal(64, maxPlayers);
    }

    [Fact]
    public void LongNamesAreTruncated()
    {
        string longName = new string('x', DiscoveryProtocol.MaxNameLength + 10);
        byte[] data = DiscoveryProtocol.EncodeResponse(longName, 8000, 1, 16);

        Assert.True(DiscoveryProtocol.TryDecodeResponse(
            data, out string name, out _, out _, out _));
        Assert.Equal(DiscoveryProtocol.MaxNameLength, name.Length);
    }

    [Fact]
    public void RejectsMalformedOrForeignPackets()
    {
        Assert.False(DiscoveryProtocol.TryDecodeResponse(
            new byte[] { 1, 2, 3 }, out _, out _, out _, out _));

        byte[] data = DiscoveryProtocol.EncodeResponse("x", 7777, 0, 64);
        data[0] ^= 0xFF;
        Assert.False(DiscoveryProtocol.TryDecodeResponse(
            data, out _, out _, out _, out _));
    }
}
