using System.Security.Cryptography;

namespace Shiny.AiConversation.Infrastructure;

/// <summary>
/// ULID-style ids: 26 Crockford base32 characters, 48-bit millisecond timestamp then 80 random bits,
/// so ids sort by creation time.
/// </summary>
static class IdGenerator
{
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New(TimeProvider? timeProvider = null)
    {
        var ms = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        Span<char> chars = stackalloc char[26];
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(ms & 31)];
            ms >>= 5;
        }

        // 80 random bits -> 16 characters, 5 bits each
        var bitBuffer = 0;
        var bitCount = 0;
        var c = 10;
        foreach (var b in random)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                chars[c++] = Alphabet[(bitBuffer >> bitCount) & 31];
            }
        }
        return new string(chars);
    }
}

/// <summary>
/// Identifies this process run. A <see cref="MessageStatus.Streaming"/> message written by another run
/// belongs to a process that died mid-turn.
/// </summary>
static class ProcessRun
{
    public static string Id { get; } = Guid.NewGuid().ToString("N");
}
