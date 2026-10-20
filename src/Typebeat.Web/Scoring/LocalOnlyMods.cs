using Newtonsoft.Json.Linq;

namespace Typebeat.Web.Scoring;

/// <summary>
/// THE MODS WHOSE PLAYS NEVER REACH THE SERVER, mirroring the client's <c>Mod.LocalOnly</c>
/// (backlog 332) and, since PR 43, its per-map <c>Mod.IsLocalOnlyFor</c>. The game keeps a play
/// holding one on the device (no score token, no submission, no online board for a selection that
/// contains it). Today that is one mod, <c>PG</c> (Polyglot, the game's <c>TypeBeatModPolyglot</c>),
/// and only ON A CHINESE OR KOREAN MAP (<see cref="POLYGLOT_LOCAL_ONLY_LANGUAGES"/>, the game's
/// <c>IsLocalOnlyFor</c>). Everywhere else PR 43 made it a RANKED mod at 1.0x: it is priced
/// (<see cref="ModMultiplier"/>), stored, ranked and judged against the Polyglot half of the map's
/// rating matrix (<see cref="Packages.Lyrics.PolyglotSummary"/>).
///
/// <para>
/// This is NOT <see cref="UnrankedMods"/>. An unranked mod's play is still STORED (unranked, in
/// the player's history); a local-only mod's play has no business being stored at all, because the
/// stock client never sends one. A submission carrying it can only come from a modified client, so
/// both the token route and the submit route REFUSE it outright (422 with the usual <c>error</c>
/// body) rather than filing it anywhere: no score row, no rating matrix cell, no board.
/// </para>
///
/// <para>
/// Like <see cref="UnrankedMods"/> this is a deny list, so a new local-only client mod must be added
/// here too, or a modified client could submit it and have it stored.
/// </para>
/// </summary>
public static class LocalOnlyMods
{
    /// <summary>The acronyms local only on EVERY map (none since PR 43), compared case-insensitively as the wire spells them.</summary>
    public static readonly IReadOnlySet<string> ACRONYMS =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Polyglot mod's acronym, local only on <see cref="POLYGLOT_LOCAL_ONLY_LANGUAGES"/>.</summary>
    public const string POLYGLOT_ACRONYM = "PG";

    /// <summary>The map languages (stored canonical names) on which Polyglot stays local only.</summary>
    public static readonly IReadOnlySet<string> POLYGLOT_LOCAL_ONLY_LANGUAGES =
        new HashSet<string>(StringComparer.Ordinal) { "chinese", "korean" };

    /// <summary>The <c>error</c> text a refused token or submission carries.</summary>
    public const string REFUSAL = "local-only mods (Polyglot on a Chinese or Korean map) are never submitted";

    /// <summary>Whether this acronym belongs to a mod whose plays stay on the device on EVERY map.</summary>
    public static bool IsLocalOnly(string? acronym)
        => !string.IsNullOrWhiteSpace(acronym) && ACRONYMS.Contains(acronym.Trim());

    /// <summary>
    /// Whether this acronym's plays stay on the device on a map whose language is
    /// <paramref name="mapLanguage"/> (a stored canonical name, or <c>""</c> when unknown): the
    /// always-local ones, and Polyglot on a Chinese or Korean map.
    /// </summary>
    public static bool IsLocalOnlyOn(string? acronym, string? mapLanguage)
        => IsLocalOnly(acronym)
           || string.Equals(acronym?.Trim(), POLYGLOT_ACRONYM, StringComparison.OrdinalIgnoreCase)
           && mapLanguage != null && POLYGLOT_LOCAL_ONLY_LANGUAGES.Contains(mapLanguage);

    /// <summary>Whether any member of this stack is local only on a map in <paramref name="mapLanguage"/>.</summary>
    public static bool StackIsLocalOnly(IEnumerable<string?> acronyms, string? mapLanguage)
        => acronyms.Any(a => IsLocalOnlyOn(a, mapLanguage));

    /// <summary>
    /// Whether any of the raw <c>mods</c> form values on a score-token request names a mod local only
    /// on this map. The stock client sends no mods on that request at all (CreateSoloScoreRequest
    /// carries version_hash, beatmap_hash and ruleset_id only), so this is purely the defensive half:
    /// it reads whatever shape a modified client might send, a bare acronym, a comma or space
    /// separated list, or a JSON array of acronyms or <c>{"acronym": ...}</c> objects, and an
    /// unparseable value is read as plain text rather than failing the request.
    /// </summary>
    public static bool AnyLocalOnly(IEnumerable<string?> formValues, string? mapLanguage)
        => formValues.SelectMany(acronymsIn).Any(a => IsLocalOnlyOn(a, mapLanguage));

    private static IEnumerable<string?> acronymsIn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        string trimmed = value.Trim();

        if (trimmed.StartsWith('['))
        {
            try
            {
                return JArray.Parse(trimmed).Select(t => t.Type == JTokenType.Object ? (string?)t["acronym"] : t.Type == JTokenType.String ? (string?)t : null).ToList();
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // Not JSON after all: fall through to the plain-text reading.
            }
        }

        return trimmed.Split([',', ' ', ';', '[', ']', '"'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
