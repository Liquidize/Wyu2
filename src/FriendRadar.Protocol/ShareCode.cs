using System.Security.Cryptography;
using System.Text;

namespace FriendRadar.Protocol;

/// <summary>
/// Human typable identifier used to invite somebody. Crockford base32 so it survives being read out in
/// voice chat: no I/L/O/U, and the decoder folds the usual look-alikes back together.
/// </summary>
public static class ShareCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string Prefix = "FR";
    private const int SecretBytes = 10;   // 80 bits -> 16 base32 characters
    private const int GroupSize = 4;

    /// <summary>Mints a fresh random code, e.g. <c>FR-9GQ4-K72M-XPZ1-D8V0</c>.</summary>
    public static string Generate()
        => Format(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>Formats raw bytes as a grouped, prefixed code.</summary>
    public static string Format(ReadOnlySpan<byte> data)
    {
        var raw = Encode(data);
        var sb = new StringBuilder(Prefix);
        for (var i = 0; i < raw.Length; i += GroupSize)
        {
            sb.Append('-');
            sb.Append(raw, i, Math.Min(GroupSize, raw.Length - i));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Strips formatting so codes can be compared regardless of how they were typed. Returns null when the
    /// input cannot be a share code at all.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var sb = new StringBuilder(24);
        foreach (var raw in input)
        {
            var c = char.ToUpperInvariant(raw);
            switch (c)
            {
                case '-' or ' ' or '_' or '.':
                    continue;
                case 'O':
                    c = '0';
                    break;
                case 'I' or 'L':
                    c = '1';
                    break;
                case 'U':
                    c = 'V';
                    break;
            }

            if (Alphabet.IndexOf(c) < 0)
                return null;

            sb.Append(c);
        }

        // Users usually paste the code with its prefix; the prefix itself is not part of the secret but it
        // is made of valid base32 characters, so only drop it when the remaining length still fits.
        var text = sb.ToString();
        if (text.Length == (Prefix.Length + (SecretBytes * 8 / 5)) && text.StartsWith("FR", StringComparison.Ordinal))
            text = text[Prefix.Length..];

        return text.Length == SecretBytes * 8 / 5 ? text : null;
    }

    /// <summary>True when two codes refer to the same secret, ignoring formatting.</summary>
    public static bool Matches(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        return na is not null && nb is not null && string.Equals(na, nb, StringComparison.Ordinal);
    }

    private static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 0x1F]);
            }
        }

        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);

        return sb.ToString();
    }
}
