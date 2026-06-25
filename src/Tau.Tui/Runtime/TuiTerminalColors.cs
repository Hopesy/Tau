using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Tau.Tui.Runtime;

public readonly record struct TuiRgbColor(int R, int G, int B);

public enum TuiTerminalColorScheme
{
    Dark,
    Light
}

public static class TuiTerminalColors
{
    private static readonly Regex Osc11BackgroundColorResponseRegex =
        new("^\u001b\\]11;([^\u0007\u001b]*)(?:\u0007|\u001b\\\\)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex TerminalColorSchemeReportRegex =
        new("^\u001b\\[\\?997;(1|2)n$", RegexOptions.CultureInvariant);

    public static bool IsOsc11BackgroundColorResponse(string data) =>
        Osc11BackgroundColorResponseRegex.IsMatch(data);

    public static TuiRgbColor? ParseOsc11BackgroundColor(string data)
    {
        var match = Osc11BackgroundColorResponseRegex.Match(data);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Value.Trim();
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (Regex.IsMatch(hex, "^[0-9a-f]{6}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            {
                return HexToRgb(hex);
            }

            if (Regex.IsMatch(hex, "^[0-9a-f]{12}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            {
                var r = ParseOscHexChannel(hex[..4]);
                var g = ParseOscHexChannel(hex[4..8]);
                var b = ParseOscHexChannel(hex[8..12]);
                return r is not null && g is not null && b is not null
                    ? new TuiRgbColor(r.Value, g.Value, b.Value)
                    : null;
            }

            return null;
        }

        var rgbValue = Regex.Replace(value, "^rgba?:", string.Empty, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var channels = rgbValue.Split('/');
        if (channels.Length < 3)
        {
            return null;
        }

        var red = ParseOscHexChannel(channels[0]);
        var green = ParseOscHexChannel(channels[1]);
        var blue = ParseOscHexChannel(channels[2]);
        return red is not null && green is not null && blue is not null
            ? new TuiRgbColor(red.Value, green.Value, blue.Value)
            : null;
    }

    public static TuiTerminalColorScheme? ParseTerminalColorSchemeReport(string data)
    {
        var match = TerminalColorSchemeReportRegex.Match(data);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Value == "2"
            ? TuiTerminalColorScheme.Light
            : TuiTerminalColorScheme.Dark;
    }

    private static TuiRgbColor HexToRgb(string hex) =>
        new(
            int.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static int? ParseOscHexChannel(string channel)
    {
        if (channel.Length == 0 || !Regex.IsMatch(channel, "^[0-9a-f]+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
        {
            return null;
        }

        var value = BigInteger.Parse("0" + channel, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var max = BigInteger.Pow(16, channel.Length) - BigInteger.One;
        if (max <= BigInteger.Zero)
        {
            return null;
        }

        return (int)((value * 255 * 2 + max) / (max * 2));
    }
}
