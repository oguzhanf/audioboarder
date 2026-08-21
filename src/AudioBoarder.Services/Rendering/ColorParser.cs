using AudioBoarder.Core.Rendering;
using SkiaSharp;

namespace AudioBoarder.Services.Rendering;

internal static class ColorParser
{
    public static SKColor Parse(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return SKColors.Black;
        var trimmed = hex.TrimStart('#');
        if (trimmed.Length == 6)
            trimmed = "FF" + trimmed;
        if (uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var packed))
        {
            var a = (byte)((packed >> 24) & 0xFF);
            var r = (byte)((packed >> 16) & 0xFF);
            var g = (byte)((packed >> 8) & 0xFF);
            var b = (byte)(packed & 0xFF);
            return new SKColor(r, g, b, a);
        }
        return SKColors.Black;
    }

    public static SKColor Of(string hex) => Parse(hex);
}
