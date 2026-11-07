using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ColorFinder;

public record ColorFormat(string Key, string Label, string Value, string Note);

/// <summary>Unpremultiplied, 8-bit sRGB channels and an independent 8-bit alpha channel.</summary>
public readonly record struct ColorValue(byte R, byte G, byte B, byte A = 255)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly Regex RgbFunction = new(
        @"^rgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RgbaFunction = new(
        @"^rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([0-9]+(?:\.[0-9]*)?|\.[0-9]+)\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BareRgb = new(
        @"^(\d+)\s*,\s*(\d+)\s*,\s*(\d+)$",
        RegexOptions.CultureInvariant);

    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    public string HexAlpha => $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    /// <summary>Parses hex, comma-separated integer RGB, or comma-syntax CSS rgb()/rgba().</summary>
    public static bool TryParse(string? text, out ColorValue color)
    {
        color = default;
        if (text is null) return false;
        text = text.Trim();
        if (text.Length == 0) return false;

        if (text[0] == '#')
        {
            var hex = text.Substring(1);
            if (hex.Length is not (3 or 4 or 6 or 8)) return false;
            foreach (var digit in hex)
                if (!Uri.IsHexDigit(digit)) return false;

            var channels = new byte[4];
            channels[3] = 255;
            var shortForm = hex.Length is 3 or 4;
            var count = shortForm ? hex.Length : hex.Length / 2;
            for (var i = 0; i < count; i++)
            {
                var channel = shortForm ? hex.Substring(i, 1) : hex.Substring(i * 2, 2);
                if (!byte.TryParse(channel, NumberStyles.AllowHexSpecifier, Invariant, out channels[i]))
                    return false;
                if (shortForm) channels[i] *= 17;
            }
            color = new ColorValue(channels[0], channels[1], channels[2], channels[3]);
            return true;
        }

        var rgba = RgbaFunction.Match(text);
        var rgb = rgba.Success ? rgba : RgbFunction.Match(text);
        if (!rgb.Success) rgb = BareRgb.Match(text);
        if (!rgb.Success ||
            !byte.TryParse(rgb.Groups[1].Value, NumberStyles.None, Invariant, out var r) ||
            !byte.TryParse(rgb.Groups[2].Value, NumberStyles.None, Invariant, out var g) ||
            !byte.TryParse(rgb.Groups[3].Value, NumberStyles.None, Invariant, out var b))
            return false;

        byte a = 255;
        if (rgba.Success)
        {
            if (!double.TryParse(rgba.Groups[4].Value, NumberStyles.AllowDecimalPoint, Invariant, out var alpha)
                || double.IsNaN(alpha) || double.IsInfinity(alpha) || alpha is < 0 or > 1)
                return false;
            a = (byte)Math.Round(alpha * 255, MidpointRounding.AwayFromZero);
        }
        color = new ColorValue(r, g, b, a);
        return true;
    }

    /// <summary>Decodes an 8-bit sRGB component to linear-light RGB in [0, 1].</summary>
    public static double ToLinear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    public IReadOnlyList<ColorFormat> GetFormats()
    {
        var r = R / 255.0;
        var g = G / 255.0;
        var b = B / 255.0;
        var a = A / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var lightness = (max + min) / 2;
        var hue = 0.0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * (((b - r) / delta) + 2);
            else hue = 60 * (((r - g) / delta) + 4);
            if (hue < 0) hue += 360;
        }
        var hslSaturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * lightness - 1));
        var hsvSaturation = max == 0 ? 0 : delta / max;
        var black = 1 - max;
        var cyan = max == 0 ? 0 : (max - r) / max;
        var magenta = max == 0 ? 0 : (max - g) / max;
        var yellow = max == 0 ? 0 : (max - b) / max;

        var hsl = A == 255
            ? $"hsl({F(hue, 2)}, {F(hslSaturation * 100, 2)}%, {F(lightness * 100, 2)}%)"
            : $"hsl({F(hue, 2)} {F(hslSaturation * 100, 2)}% {F(lightness * 100, 2)}% / {F(a)})";
        return new ColorFormat[]
        {
            new("hex", "HEX", Hex, "8-bit RGB in #RRGGBB order."),
            new("rgb", "RGB", $"rgb({R}, {G}, {B})", "sRGB channels from 0 to 255."),
            new("hsl", "HSL", hsl, "Hue in degrees; saturation and lightness in percent."),
            new("hsv", "HSV / HSB", $"hsv({F(hue, 2)}, {F(hsvSaturation * 100, 2)}%, {F(max * 100, 2)}%)", "Hue in degrees; saturation and value in percent. Not CSS syntax."),
            new("cmyk", "CMYK (approx.)", $"cmyk({F(cyan * 100, 2)}%, {F(magenta * 100, 2)}%, {F(yellow * 100, 2)}%, {F(black * 100, 2)}%)", "Mathematical approximation; no printer / ICC profile."),
            new("normalized", "sRGB 0–1", $"({F(r)}, {F(g)}, {F(b)}, {F(a)})", "Normalized sRGB RGBA; RGB remains gamma encoded."),
            new("linear", "Linear RGB 0–1", $"({F(ToLinear(R))}, {F(ToLinear(G))}, {F(ToLinear(B))}, {F(a)})", "sRGB transfer function decoded; alpha stays linear."),
        };
    }

    private static string F(double value, int decimals = 6) =>
        Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString("0." + new string('#', decimals), Invariant);
}
