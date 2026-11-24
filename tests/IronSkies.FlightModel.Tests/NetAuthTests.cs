using IronSkies.FlightModel.Net;

namespace IronSkies.FlightModel.Tests;

public sealed class NetAuthTests
{
    [Fact]
    public void ResponseIsDeterministicForSameSaltAndPassword()
    {
        byte[] hash = NetAuth.HashPassword("normandy-1944");
        byte[] salt = Enumerable.Range(0, NetAuth.SaltSize).Select(i => (byte)i).ToArray();

        byte[] first = NetAuth.ComputeResponse(salt, hash);
        byte[] second = NetAuth.ComputeResponse(salt, hash);

        Assert.Equal(NetAuth.HashSize, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ResponseDependsOnSaltAndPassword()
    {
        byte[] hash = NetAuth.HashPassword("normandy-1944");
        byte[] saltA = new byte[NetAuth.SaltSize];
        byte[] saltB = new byte[NetAuth.SaltSize];
        saltB[0] = 1;

        byte[] responseA = NetAuth.ComputeResponse(saltA, hash);
        byte[] responseB = NetAuth.ComputeResponse(saltB, hash);
        byte[] wrong = NetAuth.ComputeResponse(saltA, NetAuth.HashPassword("wrong"));

        Assert.NotEqual(responseA, responseB);
        Assert.NotEqual(responseA, wrong);
        Assert.True(NetAuth.Verify(responseA, responseA));
        Assert.False(NetAuth.Verify(responseA, wrong));
    }

    [Fact]
    public void AuthPacketsRoundTrip()
    {
        byte[] salt = NetAuth.CreateSalt();
        Assert.True(NetProtocol.TryDecodeAuthRequest(
            NetProtocol.EncodeAuthRequest(salt), out byte[] decodedSalt));
        Assert.Equal(salt, decodedSalt);

        byte[] hash = NetAuth.ComputeResponse(salt, NetAuth.HashPassword("x"));
        Assert.True(NetProtocol.TryDecodeAuthResponse(
            NetProtocol.EncodeAuthResponse(hash), out byte[] decodedHash));
        Assert.Equal(hash, decodedHash);

        Assert.True(NetProtocol.TryDecodeAuthFailed(
            NetProtocol.EncodeAuthFailed(1), out byte reason));
        Assert.Equal(1, reason);
    }
}
