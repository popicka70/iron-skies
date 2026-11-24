using System;
using System.Buffers.Binary;
using System.Text;

namespace IronSkies.FlightModel.Net;

/// <summary>
/// LAN server discovery. Clients broadcast a query to the discovery port; each server
/// replies with its name, game port and population. The format is deliberately tiny and
/// versioned so a browser can ignore replies it does not understand.
/// </summary>
public static class DiscoveryProtocol
{
    public const int DefaultPort = 7778;
    public const int MaxNameLength = 64;

    private static ReadOnlySpan<byte> QueryMagic => "IRONSKIES?2"u8;
    private static ReadOnlySpan<byte> ResponseMagic => "IRONSKIES!2"u8;
    private const int ResponseHeader = 11 + 2 + 1 + 1 + 1 + 1;
    private const byte HasPasswordFlag = 1;

    public static byte[] EncodeQuery() => QueryMagic.ToArray();

    public static bool IsQuery(byte[] data) =>
        data.Length == QueryMagic.Length && data.AsSpan().SequenceEqual(QueryMagic);

    public static byte[] EncodeResponse(
        string name, int port, int players, int maxPlayers, bool hasPassword = false)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name ?? string.Empty);
        int nameLength = System.Math.Min(nameBytes.Length, MaxNameLength);

        byte[] data = new byte[ResponseHeader + nameLength];
        Span<byte> span = data;
        ResponseMagic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(11), (ushort)port);
        span[13] = (byte)System.Math.Clamp(players, 0, 255);
        span[14] = (byte)System.Math.Clamp(maxPlayers, 0, 255);
        span[15] = hasPassword ? HasPasswordFlag : (byte)0;
        span[16] = (byte)nameLength;
        nameBytes.AsSpan(0, nameLength).CopyTo(span.Slice(17));
        return data;
    }

    public static bool TryDecodeResponse(
        byte[] data, out string name, out int port, out int players, out int maxPlayers,
        out bool hasPassword)
    {
        name = string.Empty;
        port = 0;
        players = 0;
        maxPlayers = 0;
        hasPassword = false;

        if (data.Length < ResponseHeader || !data.AsSpan(0, 11).SequenceEqual(ResponseMagic))
        {
            return false;
        }

        int nameLength = data[16];
        if (data.Length < ResponseHeader + nameLength)
        {
            return false;
        }

        port = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(11));
        players = data[13];
        maxPlayers = data[14];
        hasPassword = (data[15] & HasPasswordFlag) != 0;
        name = Encoding.UTF8.GetString(data.AsSpan(17, nameLength));
        return port > 0;
    }
}
