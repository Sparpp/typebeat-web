using System.Globalization;

namespace Typebeat.Web.Scoring;

/// <summary>
/// The rate mods (Double Time, Nightcore, Half Time) and the one setting of theirs the server
/// cares about, <c>speed_change</c>.
///
/// <para>
/// Since the client ranks these at every speed, the rate is score-affecting data, not cosmetics: it
/// prices the play (<see cref="ModMultiplier"/>) and it has to be shown on the badge, or a "DT" on a
/// board is ambiguous between a 1.01x nudge and a 2.00x sprint. The client therefore pins
/// <c>settings.speed_change</c> onto every DT/NC/HT it submits, even at the default (its
/// <c>Mod.AlwaysSerializeSetting</c> hook).
/// </para>
///
/// <para>
/// The ranges mirror the client's sliders (<c>ModDoubleTime</c> / <c>ModHalfTime</c>
/// <c>SpeedChange</c>): DT/NC [1.01, 2.00], HT [0.50, 0.99], both stepping by 0.01. A submitted
/// value is snapped to 2 decimals and clamped into range before it is stored or priced, so a
/// tampered "speed_change": 40 buys a 2.00x play, not a 40x one.
/// </para>
///
/// <para>
/// Daycore ("DC") is deliberately NOT listed: type!beat's ruleset ships no Daycore mod (Half Time
/// carries the pitch toggle). An acronym that is not listed here keeps no settings at all and is
/// priced by the conservative unknown-mod cap.
/// </para>
/// </summary>
public static class RateMods
{
    /// <summary>The wire key the client sends the rate under (osu <c>APIMod.Settings</c>).</summary>
    public const string SPEED_CHANGE_KEY = "speed_change";

    /// <summary>A rate mod's reachable slider range and the client's default position on it.</summary>
    public readonly record struct Range(double Min, double Max, double Default);

    private static readonly Dictionary<string, Range> ranges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DT"] = new Range(1.01, 2.00, 1.50),
        ["NC"] = new Range(1.01, 2.00, 1.50),
        ["HT"] = new Range(0.50, 0.99, 0.75),
    };

    /// <summary>
    /// Double Time / Nightcore's base rate, 1.50x: the slider default, and the ONLY up-rate that
    /// earns pp (docs/pp.md). Every other DT rate still ranks on the score leaderboards, it just
    /// prices at 0 pp, which is what keeps the server's stored rate-adjusted star ratings down to
    /// one per direction.
    /// </summary>
    public static readonly double DoubleTimeBaseRate = ranges["DT"].Default;

    /// <summary>Half Time's base rate, 0.75x: the down-rate counterpart of <see cref="DoubleTimeBaseRate"/>.</summary>
    public static readonly double HalfTimeBaseRate = ranges["HT"].Default;

    /// <summary>The slowest rate any rate mod can be submitted at (Half Time's floor, 0.50x).</summary>
    public static readonly double SlowestRate = ranges.Values.Min(r => r.Min);

    /// <summary>The fastest rate any rate mod can be submitted at (Double Time's ceiling, 2.00x).</summary>
    public static readonly double FastestRate = ranges.Values.Max(r => r.Max);

    /// <summary>Whether the acronym is one of the rate mods whose speed_change is meaningful.</summary>
    public static bool IsRateMod(string? acronym) => acronym != null && ranges.ContainsKey(acronym.Trim());

    /// <summary>The acronym's slider range, or false for anything that is not a rate mod.</summary>
    public static bool TryGetRange(string? acronym, out Range range)
    {
        if (acronym != null)
            return ranges.TryGetValue(acronym.Trim(), out range);

        range = default;
        return false;
    }

    /// <summary>Snaps a raw rate to the slider's 0.01 precision and clamps it into the mod's range.</summary>
    public static double Normalize(in Range range, double raw)
        => Math.Clamp(Math.Round(raw, RateMultiplier.RATE_DECIMALS, MidpointRounding.AwayFromZero), range.Min, range.Max);

    /// <summary>
    /// Reads <c>speed_change</c> out of a submitted mod's settings bag: null when the mod is not a
    /// rate mod, when the key is absent, or when the value is not a finite number. A value that IS
    /// a number comes back snapped and clamped (see <see cref="Normalize"/>). Never throws; the
    /// submission path must not 500 on tamper-shaped input.
    /// </summary>
    public static double? ReadSpeedChange(string? acronym, IReadOnlyDictionary<string, object>? settings)
    {
        if (!TryGetRange(acronym, out var range))
            return null;

        if (settings == null || !settings.TryGetValue(SPEED_CHANGE_KEY, out object? raw) || raw == null)
            return null;

        try
        {
            double value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);

            return double.IsFinite(value) ? Normalize(range, value) : null;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The rate to ASSUME for a rate mod that carries no <c>speed_change</c>: the client default.
    /// Two cases produce one: a pre-task-27 client (which only omitted the key when the slider sat
    /// at the default, so the assumption is exact for every score already in the database), and a
    /// tampered payload (which the assumption prices at the cheap default rather than the dear one).
    /// </summary>
    public static double DefaultSpeed(string? acronym) => TryGetRange(acronym, out var range) ? range.Default : 1.0;

    /// <summary>
    /// The rate a whole submitted stack was played at, as the play-time gate needs it: 1.0 when the
    /// stack carries no rate mod, otherwise the SLOWEST rate in it.
    ///
    /// <para>
    /// Slowest, not fastest, because the gate divides its requirement by this number: a higher rate
    /// buys a shorter real-time bound, so the slowest member is the strictest reading of a stack.
    /// The client makes DT/NC/HT mutually exclusive, so a stack with two rate mods in it is
    /// tamper-shaped by construction and gets the reading that concedes the least. Each member is
    /// read exactly as it is priced (<see cref="ReadSpeedChange"/>): snapped, clamped, and falling
    /// back to the mod's default when the client sent no <c>speed_change</c>.
    /// </para>
    /// </summary>
    public static double EffectiveRate(IEnumerable<(string? Acronym, double? SpeedChange)> mods)
    {
        double? slowest = null;

        foreach (var (acronym, speedChange) in mods)
        {
            if (!TryGetRange(acronym, out var range))
                continue;

            double rate = speedChange is double submitted ? Normalize(range, submitted) : range.Default;

            slowest = slowest is double current ? Math.Min(current, rate) : rate;
        }

        return slowest ?? 1.0;
    }

    /// <summary>
    /// The rate as the game renders it on a mod icon: <c>{rate:N2}x</c>, invariant, e.g. "1.50x".
    /// Matches <c>TypeBeatModDoubleTime.ExtendedIconInformation</c> so a rate reads identically on
    /// the site and in the client.
    /// </summary>
    public static string Format(double speed) => speed.ToString("N2", CultureInfo.InvariantCulture) + "x";
}
