using System.Globalization;
using System.Text;

namespace Purg.Platform;

public static class DisplayNameSanitizer
{
    public static string Sanitize(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var builder = new StringBuilder(fileName.Length);
        foreach (var rune in fileName.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control ||
                rune.Value is 0x061C or 0x200E or 0x200F or
                    >= 0x202A and <= 0x202E or
                    >= 0x2066 and <= 0x2069)
            {
                continue;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }
}
