using System;
using System.Security.Cryptography;
using System.Text;

namespace IronSkies.FlightModel.Net;

/// <summary>
/// Password challenge/response for dedicated servers. The server stores only a SHA-256
/// password hash; each connection gets a fresh random salt so a captured response cannot
/// be replayed against another connection.
/// </summary>
public static class NetAuth
{
    public const int SaltSize = 16;
    public const int HashSize = 32;

    public static byte[] HashPassword(string password) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(password ?? string.Empty));

    public static byte[] CreateSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public static byte[] ComputeResponse(
        ReadOnlySpan<byte> salt, ReadOnlySpan<byte> passwordHash)
    {
        if (salt.Length != SaltSize)
        {
            throw new ArgumentException("Unexpected salt length.", nameof(salt));
        }

        if (passwordHash.Length != HashSize)
        {
            throw new ArgumentException("Unexpected password hash length.", nameof(passwordHash));
        }

        Span<byte> buffer = stackalloc byte[SaltSize + HashSize];
        salt.CopyTo(buffer);
        passwordHash.CopyTo(buffer.Slice(SaltSize));
        return SHA256.HashData(buffer);
    }

    public static bool Verify(
        ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
}
