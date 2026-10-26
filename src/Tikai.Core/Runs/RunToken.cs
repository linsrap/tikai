using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Tikai.Core.Runs;

/// <summary>
/// The bearer token an agent uses for MCP: <c>tikai_run_</c> plus 32 random bytes in base64url.
/// The prefix lets secret scanners recognise a leaked token. Only its SHA-256 is stored; with 256 random bits
/// a slow password hash would add nothing.
/// </summary>
public static class RunToken
{
    public const string Prefix = "tikai_run_";

    private const int RandomBytes = 32;

    // base64url of 32 bytes, without padding.
    private static readonly int _encodedLength = Base64Url.GetEncodedLength(RandomBytes);

    public static string New() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomBytes));

    /// <summary>True when the value has the shape of a run token, so malformed values never reach the database.</summary>
    public static bool IsWellFormed(string? token) =>
        token is not null
        && token.Length == Prefix.Length + _encodedLength
        && token.StartsWith(Prefix, StringComparison.Ordinal)
        && Base64Url.IsValid(token.AsSpan(Prefix.Length), out var decoded)
        && decoded == RandomBytes;

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));
}
