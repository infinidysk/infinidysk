using System.Linq;
using System.Security.Cryptography;

namespace SharpCompress.Common.Rar;

internal interface ICryptKey
{
    // unrar MAXPASSWORD_RAR - 1, in code points as unrar counts with a 32-bit wchar_t.
    const int MaxPasswordChars = 127;

    ICryptoTransform Transformer(byte[] salt);

    static string LimitPassword(string? password)
    {
        if (password is not { Length: > MaxPasswordChars })
        {
            return password ?? "";
        }

        var length = password
            .EnumerateRunes()
            .Take(MaxPasswordChars)
            .Sum(rune => rune.Utf16SequenceLength);
        return password[..length];
    }
}
