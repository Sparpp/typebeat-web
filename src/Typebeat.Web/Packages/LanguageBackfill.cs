using Dapper;
using Typebeat.Web.Data;
using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Packages;

/// <summary>
/// Startup sweep that fills in <c>beatmapsets.language</c> for every set that does not have one
/// yet (019_language.sql). Same shape as <see cref="PaceBackfill"/>: one pass at boot, a per-row
/// try/catch so a bad row cannot abort the sweep, and one summary log line.
///
/// SOURCE OF TRUTH IS THE LYRICS ALREADY IN THE DATABASE. Every live difficulty's
/// <c>beatmaps.lyrics</c> (the full lyric text, one line per newline, original casing, written by
/// ingest and by <see cref="PaceBackfill"/>) is concatenated per set and handed to
/// <see cref="LanguageDetector"/>. No blobs are opened and no network call is made, so this is
/// pure CPU over data already loaded, which is why it can run unconditionally on every boot.
/// It must therefore run AFTER PaceBackfill in Program.cs: that is what fills the lyrics column
/// for rows written before 018.
///
/// WHAT IT WILL NOT DO:
///  * It never touches a row whose language is already set. A mapper's own answer (which arrives
///    with any post-task-58 upload, see PackageIngest) is final; the detector is only ever filling
///    a vacuum. The UPDATE re-checks <c>language = ''</c> in its WHERE so an upload committing
///    concurrently with this sweep still wins.
///  * It never guesses when the detector is not confident: an unclassifiable row is left at '' and
///    retried on the next boot (free, and it will succeed the moment the lyrics column is filled,
///    or once the mapper resubmits).
///  * A set with NO lyric text at all is left unset rather than marked 'instrumental'. Empty
///    lyrics are ambiguous between "genuinely has no words" and "PaceBackfill has not reached, or
///    cannot reach, this row" (missing blob, parse failure), and in a lyric-typing game a map with
///    zero lyric lines is not playable in the first place, so the ambiguity resolves the other way
///    almost always. 'instrumental' stays a value only a mapper can choose.
/// </summary>
public static class LanguageBackfill
{
    public static async Task RunAsync(Db db, ILogger logger, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        // filename IS NOT NULL = the difficulty is live in the current version (the repo-wide
        // marker); dropped diffs and blank BSS placeholder rows must not contribute text. The
        // newline join matches the per-difficulty column's own separator, so a multi-difficulty
        // set reads as one document.
        var candidates = (await conn.QueryAsync<(long SetId, string Lyrics)>(
                """
                SELECT s.id AS SetId,
                       coalesce(string_agg(b.lyrics, E'\n' ORDER BY b.id) FILTER (WHERE b.filename IS NOT NULL), '') AS Lyrics
                FROM beatmapsets s
                LEFT JOIN beatmaps b ON b.set_id = s.id
                WHERE s.language = ''
                GROUP BY s.id
                """))
            .ToList();

        if (candidates.Count == 0)
            return;

        int detected = 0, failed = 0;

        foreach (var row in candidates)
        {
            try
            {
                string? language = LanguageDetector.Detect(row.Lyrics);

                if (language == null)
                    continue;

                // The WHERE re-check is the concurrency guard: if a submission set a real language
                // between the SELECT above and here, this writes nothing.
                await conn.ExecuteAsync(
                    "UPDATE beatmapsets SET language = @language WHERE id = @setId AND language = ''",
                    new { setId = row.SetId, language });

                detected++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Language backfill failed for beatmap set {SetId}; will retry next startup.", row.SetId);
            }
        }

        logger.LogInformation(
            "Language backfill: {Detected}/{Candidates} sets classified ({Undetermined} left unset, {Failed} errored).",
            detected, candidates.Count, candidates.Count - detected - failed, failed);
    }
}
