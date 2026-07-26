using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Scoring;

namespace Typebeat.Web;

/// <summary>
/// One mod badge on a score display: the acronym, plus the track rate when the mod is a rate mod.
///
/// <para>
/// The rate is part of the badge because type!beat ranks DT/NC/HT at every speed and pays each one
/// differently, so a bare "DT" would be ambiguous between a 1.01x nudge and a 2.00x sprint. The
/// client welds the same number to its own mod icons; the site shows it in the same
/// <c>{rate:N2}x</c> form so a play reads identically in both places.
/// </para>
/// </summary>
/// <param name="Acronym">Uppercased mod acronym as stored in the scores.mods jsonb.</param>
/// <param name="Rate">
/// Track rate for a rate mod, else null. Never null for DT/NC/HT: a row with no stored
/// <c>speed_change</c> reads as the client default (see <see cref="ScoreMods.Parse"/>).
/// </param>
public readonly record struct ScoreMod(string Acronym, double? Rate)
{
    /// <summary>CSS modifier suffix (mod-icon--{class}) grouping the acronym by mod type.</summary>
    public string CategoryClass => ModInfo.CategoryClass(Acronym);

    /// <summary>The rate as the game renders it ("1.50x"), or null for a mod with no rate.</summary>
    public string? RateLabel => Rate is double rate ? RateMods.Format(rate) : null;

    /// <summary>Tooltip: the full mod name, carrying the rate when there is one.</summary>
    public string Title => RateLabel is null ? ModInfo.Name(Acronym) : ModInfo.Name(Acronym) + " " + RateLabel;

    /// <summary>
    /// How far the rate is pushed, 0 (as good as no-mod) to 1 (the slider's far end), used to deepen
    /// the badge's rate pill so an extreme rate reads at a glance without reading the number. 0 for
    /// a mod with no rate.
    /// </summary>
    public double RateIntensity
    {
        get
        {
            if (Rate is not double rate || !RateMods.TryGetRange(Acronym, out var range))
                return 0;

            double furthest = Math.Max(Math.Abs(range.Max - 1), Math.Abs(range.Min - 1));

            return furthest <= 0 ? 0 : Math.Clamp(Math.Abs(rate - 1) / furthest, 0, 1);
        }
    }

    /// <summary>The intensity as a CSS custom-property value.</summary>
    public string RateIntensityCss => RateIntensity.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Reads the <c>scores.mods</c> jsonb (osu's APIMod list, <c>[{"acronym":"DT","settings":{...}}]</c>)
/// into the badges the score displays render.
/// </summary>
public static class ScoreMods
{
    private static readonly IReadOnlyList<ScoreMod> none = Array.Empty<ScoreMod>();

    /// <summary>
    /// Parses a stored mods blob. Entries without an acronym are dropped; a rate mod's rate comes
    /// from <c>settings.speed_change</c>, snapped and clamped to its slider range.
    ///
    /// <para>
    /// HISTORIC ROWS: scores submitted before the rate was ranked carry no settings at all and
    /// cannot be backfilled. Their rate mods read as the client DEFAULT (DT/NC 1.50x, HT 0.75x) and
    /// render exactly like any other rate, with no "assumed" marker: under the old rules a ranked
    /// bare DT could ONLY have been 1.50x, so the number is not a guess, it is the only rate that
    /// row could have been played at.
    /// </para>
    ///
    /// Malformed JSON yields an empty list rather than throwing; a score display must never 500 on
    /// one bad row.
    /// </summary>
    public static IReadOnlyList<ScoreMod> Parse(string? modsJson)
    {
        if (string.IsNullOrWhiteSpace(modsJson))
            return none;

        JArray array;

        try
        {
            array = JArray.Parse(modsJson);
        }
        catch (JsonException)
        {
            return none;
        }

        var mods = new List<ScoreMod>(array.Count);

        foreach (var entry in array)
        {
            string? acronym = ReadString(entry?["acronym"]);

            if (string.IsNullOrWhiteSpace(acronym))
                continue;

            acronym = acronym.Trim().ToUpperInvariant();

            double? rate = null;

            if (RateMods.TryGetRange(acronym, out var range))
            {
                double? stored = ReadDouble(entry?["settings"]?[RateMods.SPEED_CHANGE_KEY]);
                rate = stored is double value ? RateMods.Normalize(range, value) : range.Default;
            }

            mods.Add(new ScoreMod(acronym, rate));
        }

        return mods;
    }

    private static string? ReadString(JToken? token)
        => token is { Type: JTokenType.String } ? token.Value<string>() : null;

    private static double? ReadDouble(JToken? token)
    {
        if (token is not { Type: JTokenType.Float or JTokenType.Integer })
            return null;

        double value = token.Value<double>();

        return double.IsFinite(value) ? value : null;
    }
}
