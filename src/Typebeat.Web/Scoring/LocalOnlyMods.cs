using Newtonsoft.Json.Linq;

namespace Typebeat.Web.Scoring;

/// <summary>
/// THE MODS WHOSE PLAYS NEVER REACH THE SERVER, mirroring the client's <c>Mod.LocalOnly</c>
/// (backlog 332). Today that is one mod, <c>PG</c> (Polyglot, the game's
/// <c>TypeBeatModPolyglot</c>): the lyric is typed in its ORIGINAL script through the operating
/// system's text input, so the cells, the input model and the judgement all differ from what every
/// other play on the map is scored against, and the game keeps such a play on the device (no score
/// token, no submission, no online board for a selection that contains it).
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
/// Like <see cref="UnrankedMods"/> this is a deny list, so a new local-only mod the client ships
/// has to be added here too, or a modified client could submit it and have it stored.
/// </para>
/// </summary>
public static class LocalOnlyMods
{
    /// <summary>The acronyms themselves, compared case-insensitively as the wire spells them.</summary>
    public static readonly IReadOnlySet<string> ACRONYMS =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PG" };

    /// <summary>The <c>error</c> text a refused token or submission carries.</summary>
    public const string REFUSAL = "local-only mods (Polyglot) are never submitted";

    /// <summary>Whether this acronym belongs to a mod whose plays stay on the device.</summary>
    public static bool IsLocalOnly(string? acronym)
        => !string.IsNullOrWhiteSpace(acronym) && ACRONYMS.Contains(acronym.Trim());

    /// <summary>Whether any member of this stack is local only.</summary>
    public static bool StackIsLocalOnly(IEnumerable<string?> acronyms) => acronyms.Any(IsLocalOnly);

    /// <summary>
    /// Whether any of the raw <c>mods</c> form values on a score-token request names a local-only
    /// mod. The stock client sends no mods on that request at all (CreateSoloScoreRequest carries
    /// version_hash, beatmap_hash and ruleset_id only), so this is purely the defensive half: it
    /// reads whatever shape a modified client might send, a bare acronym, a comma or space
    /// separated list, or a JSON array of acronyms or <c>{"acronym": ...}</c> objects, and an
    /// unparseable value is read as plain text rather than failing the request.
    /// </summary>
    public static bool AnyLocalOnly(IEnumerable<string?> formValues)
        => formValues.SelectMany(acronymsIn).Any(IsLocalOnly);

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
