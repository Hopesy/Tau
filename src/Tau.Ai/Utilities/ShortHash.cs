using System.Text;

namespace Tau.Ai;

public static class ShortHash
{
    public static string Compute(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        unchecked
        {
            var h1 = 0xdeadbeefu;
            var h2 = 0x41c6ce57u;

            foreach (var ch in value)
            {
                var code = (uint)ch;
                h1 = (h1 ^ code) * 2654435761u;
                h2 = (h2 ^ code) * 1597334677u;
            }

            h1 = ((h1 ^ (h1 >> 16)) * 2246822507u) ^ ((h2 ^ (h2 >> 13)) * 3266489909u);
            h2 = ((h2 ^ (h2 >> 16)) * 2246822507u) ^ ((h1 ^ (h1 >> 13)) * 3266489909u);

            return ToBase36(h2) + ToBase36(h1);
        }
    }

    private static string ToBase36(uint value)
    {
        if (value == 0)
        {
            return "0";
        }

        Span<char> buffer = stackalloc char[7];
        var index = buffer.Length;
        while (value > 0)
        {
            var digit = value % 36;
            value /= 36;
            buffer[--index] = (char)(digit < 10 ? '0' + digit : 'a' + digit - 10);
        }

        return new string(buffer[index..]);
    }
}
