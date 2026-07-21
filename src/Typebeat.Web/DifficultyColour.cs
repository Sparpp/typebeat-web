namespace Typebeat.Web;

/// <summary>
/// Maps a star rating to a colour along an osu!-style difficulty spectrum
/// (blue → cyan → green → yellow → orange → red → purple → indigo → black), for tinting the
/// ★ readouts and difficulty meters on the site. Continuous piecewise-linear interpolation, so
/// two maps a tenth of a star apart read as almost the same colour.
/// </summary>
public static class DifficultyColour
{
    // Control points (stars, r, g, b). Between them the colour is linearly interpolated.
    private static readonly (double Stars, byte R, byte G, byte B)[] spectrum =
    {
        (0.1, 66, 144, 251),
        (1.25, 79, 192, 255),
        (2.0, 79, 255, 213),
        (2.5, 124, 255, 79),
        (3.3, 246, 240, 92),
        (4.2, 255, 128, 104),
        (4.9, 255, 78, 111),
        (5.8, 198, 69, 184),
        (6.7, 101, 99, 222),
        (7.7, 24, 21, 142),
        (9.0, 0, 0, 0),
    };

    /// <summary>CSS hex colour (e.g. "#ff8068") for the given star rating.</summary>
    public static string ForStars(double stars)
    {
        if (double.IsNaN(stars) || stars <= spectrum[0].Stars)
            return Hex(spectrum[0]);

        for (int i = 1; i < spectrum.Length; i++)
        {
            var hi = spectrum[i];
            if (stars <= hi.Stars)
            {
                var lo = spectrum[i - 1];
                double t = (stars - lo.Stars) / (hi.Stars - lo.Stars);
                return Hex(Lerp(lo.R, hi.R, t), Lerp(lo.G, hi.G, t), Lerp(lo.B, hi.B, t));
            }
        }

        return Hex(spectrum[^1]);
    }

    private static byte Lerp(byte a, byte b, double t) => (byte)System.Math.Round(a + (b - a) * t);

    private static string Hex((double Stars, byte R, byte G, byte B) c) => Hex(c.R, c.G, c.B);

    private static string Hex(byte r, byte g, byte b) => $"#{r:x2}{g:x2}{b:x2}";
}
