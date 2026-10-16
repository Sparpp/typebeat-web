using Dapper;
using Typebeat.Web.Auth;
using Typebeat.Web.Caching;
using Typebeat.Web.Data;
using Typebeat.Web.Ops;
using Typebeat.Web.Packages;
using Typebeat.Web.Scoring;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Social;

/// <summary>
/// The reviewer's status levers on a beatmapset (the set page's Rank/Unrank and Love/Unlove
/// buttons) and the <c>moderation_actions</c> row each one writes. One place owns who may move a
/// set, which moves exist, the UPDATE, its audit row and what a rank carries with it, so the page
/// handler only turns the <see cref="Outcome"/> into a response.
///
/// <para>
/// The review transitions are pending → ranked, ranked → pending, pending|unranked → loved and
/// loved → the set's intended_status (<see cref="Transition.To"/> null); there is no direct
/// ranked ↔ loved. Hidden and removed sets are untouchable from here (takedowns stay an admin-SQL
/// lever).
/// </para>
/// </summary>
public sealed class BeatmapsetReview(
    Db db, IFileStore fileStore, StartupSweepGate sweeps, CacheEviction eviction, ILogger<BeatmapsetReview> logger)
{
    /// <summary>One lever: the statuses it applies from, the status it sets (null: the set's
    /// intended_status) and the <c>moderation_actions.action</c> it records.</summary>
    public sealed record Transition(string[] From, string? To, string Action);

    public static readonly Transition Rank = new(["pending"], "ranked", "rank");

    public static readonly Transition Unrank = new(["ranked"], "pending", "unrank");

    public static readonly Transition Love = new(["pending", "unranked"], "loved", "love");

    // Unlove returns the set to its own intended_status (pending or unranked), so a loved set
    // goes back to whichever of the two it was before it was loved.
    public static readonly Transition Unlove = new(["loved"], null, "unlove");

    public enum Outcome
    {
        /// <summary>Not a reviewer, a reviewer acting on their own set, or no such set.</summary>
        NotFound,

        /// <summary>A rank while the startup sweeps are still running: nothing was changed.</summary>
        SweepsRunning,

        /// <summary>The set was not in a status this lever applies from (double-submit, stale tab).</summary>
        Unchanged,

        /// <summary>The status moved and everything that follows it ran.</summary>
        Applied,
    }

    /// <summary>
    /// Applies <paramref name="transition"/> to set <paramref name="id"/> for <paramref name="actor"/>.
    /// <see cref="Outcome.NotFound"/> for non-reviewers, and for a reviewer acting on their OWN set;
    /// the same nothing-to-see answer the buttons' absence gives them.
    /// </summary>
    public async Task<Outcome> ApplyAsync(AuthedUser? actor, long id, Transition transition, CancellationToken ct)
    {
        if (actor?.CanReviewMaps != true)
            return Outcome.NotFound;

        string[] from = transition.From;
        string? to = transition.To;
        string action = transition.Action;

        await using var conn = await db.OpenAsync(ct);

        // Review is someone else's judgement: a map_reviewer may not rank or unrank a set they
        // own. The exemption is by ROLE and not by name, so an administrator (who can already
        // reach every other moderation lever) may still flip their own set.
        // This lookup doubles as the existence check the changed == 0 branch below used to run
        // as a second query: owner_id is NOT NULL, so a null here means there is no such set.
        var owner = await conn.QuerySingleOrDefaultAsync<(long OwnerId, string Status)?>(
            "SELECT owner_id, status FROM beatmapsets WHERE id = @id", new { id });

        if (owner is null || (owner.Value.OwnerId == actor.Id && !actor.IsAdmin))
            return Outcome.NotFound;

        long ownerId = owner.Value.OwnerId;
        string oldStatus = owner.Value.Status;

        // Startup sweep gate (backlog 368): a rank carries the set's pending plays with the same
        // SetRankRefund and PpBackfill passes the boot chain may still be running, so until the chain
        // is through the rank is refused BEFORE anything changes (503, Retry-After), and the reviewer
        // simply presses again a minute later. Unranking carries nothing and is never held.
        if (to == "ranked" && !sweeps.RankOpen)
            return Outcome.SweepsRunning;

        // RETURNING the new status serves the audit note and, for Unlove (to == null), tells us
        // which intended_status the set went back to; null means the status guard matched nothing.
        string? newStatus = await conn.QuerySingleOrDefaultAsync<string?>(
            """
            UPDATE beatmapsets SET status = COALESCE(@to, intended_status), updated_at = now()
            WHERE id = @id AND status = ANY(@from)
            RETURNING status
            """,
            new { id, from, to });

        if (newStatus is null)
            return Outcome.Unchanged;

        // Record the review in the audit table. Until now nothing wrote here, so there was no
        // way to ask "which sets were ranked since X": beatmapsets carries only updated_at,
        // which any edit bumps. This row IS that history, and its monotonic id is what the
        // Discord bot's ranked-map feed cursors over (BuddyEndpoints), the same way the score
        // feed cursors over scores.id. Best-effort: the transition itself already committed,
        // and a failed audit insert must not 500 a successful rank.
        try
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO moderation_actions (actor_id, set_id, action, note)
                VALUES (@actorId, @id, @action, @note)
                """,
                new { actorId = actor.Id, id, action, note = $"{oldStatus} -> {newStatus}" });
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Set {SetId} transitioned {From}->{To} but the audit row failed.", id, oldStatus, newStatus);
        }

        if (to == "loved")
        {
            try
            {
                // Reopening the board must apply the same version policy as ranking, without
                // carrying pending plays or awarding pp to Respected maps.
                await SetRankClassicMark.RunForSetAsync(db, fileStore, logger, id, CancellationToken.None);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Set {SetId} was respected but marking its old-version plays failed; the next boot's sweep will retry.", id);
            }
        }

        if (to == "ranked")
            await carryPendingPlaysAsync(id);

        // Every cached read that shows the status (the set's API, the listing and landing
        // cards, the rankings, the owner's profile, every board and lookup memo) goes now
        // rather than up to a minute from now (backlog 366).
        await eviction.AfterSetStatusAsync(id, ownerId);

        return Outcome.Applied;
    }

    /// <summary>
    /// Carries the set's pending-era plays onto its ranked board IN THIS REQUEST (backlog 352):
    /// the boot sweep's own pass (<see cref="SetRankRefund.RunForSetAsync"/>, same plan, same
    /// version rule, narrowed to this set), then the boot pricing (<see cref="PpBackfill"/>,
    /// narrowed the same way), so the page the reviewer is redirected to already lists the plays
    /// with their pp. Unranking has no counterpart: a carried play keeps its flag, exactly as a
    /// play submitted while the set was ranked does.
    ///
    /// <para>
    /// IT ALSO PRICES LOVED-ERA PLAYS. A set reaches Rank from loved by way of Unlove (there is no
    /// direct loved → ranked), and the plays made while it was loved are already stored ranked, so
    /// the carry has nothing to flip on them; they were settled at 0 pp because loved earns none.
    /// Stamping the set's ranked rows stale before the pricing pass is what prices them now.
    /// </para>
    ///
    /// <para>
    /// THE OTHER DIRECTION RUNS TOO (backlog 398). Ranking a set after a re-upload must also MARK
    /// the plays made on the version that used to be ranked, or the newly-ranked board shows an old
    /// version's plays as if they were on the current map. <see cref="SetRankClassicMark.RunForSetAsync"/>
    /// is the same predicate pointed down: it appends the synthetic Classic mark ("CL") to those
    /// scores, reprices them to 0.95x, and stamps them for a reprice. It is NOT an unranking, so it
    /// touches both the ranked rows this board is about to show and any unranked ones on the set.
    /// The two passes do not fight: the carry-up takes UNRANKED rows and the mark rewrites mods and
    /// total without changing `ranked`, so a score is moved at most once per transition whichever
    /// order they ran in.
    /// </para>
    ///
    /// <para>
    /// BEST-EFFORT, like the audit row above: the rank has already committed, and a failure here
    /// must not 500 it. Each row commits on its own, so a failure part way leaves some rows marked
    /// and the rest for the next boot's sweep, which reconsiders every row. Run to completion even
    /// if the reviewer's browser goes away, for the same reason.
    /// </para>
    /// </summary>
    private async Task carryPendingPlaysAsync(long id)
    {
        // FIRST, and on its own: loved-era plays are stored ranked but settled at 0 pp (backlog:
        // loved status). The set is ranked now, so stamp its ranked rows stale for the one pricing
        // path. It runs before the mark and the carry so a throw in either cannot skip it: once
        // this commits, the PpBackfill below prices the rows, and if that never runs the next
        // boot's sweep does (the set is ranked by then, so nothing holds them back). Rows from an
        // earlier ranked spell are repriced to the price they already hold, which costs one formula
        // run per row and nothing else; the mark and the carry leave their own rows stale anyway.
        try
        {
            await using var conn = await db.OpenAsync(CancellationToken.None);
            await conn.ExecuteAsync(
                """
                UPDATE scores SET pp_version = 0
                WHERE ranked AND beatmap_id IN (SELECT id FROM beatmaps WHERE set_id = @id)
                """,
                new { id });
        }
        catch (Exception e)
        {
            // Nothing re-stamps these rows later: no sweep revisits a settled row on a set it has
            // no reason to suspect, so the set's loved-era plays stay unpriced until the next pp
            // version bump or a re-rank. Say so, rather than promise a retry.
            logger.LogWarning(e, "Set {SetId} was ranked but stamping its loved-era plays for pricing failed; they stay unpriced until the next pp version bump or a re-rank.", id);
        }

        try
        {
            // Downward first: mark plays on a version that is no longer the ranked one, so the board
            // this rank just reopened is honest about which plays were on a different map before the
            // carry adds the pending-era ones. PpBackfill then prices the marked rows, the carried
            // ones and the loved-era ones stamped above at once.
            await SetRankClassicMark.RunForSetAsync(db, fileStore, logger, id, CancellationToken.None);

            int carried = await SetRankRefund.RunForSetAsync(db, fileStore, logger, id, CancellationToken.None);

            await PpBackfill.RunAsync(db, logger, CancellationToken.None, setId: id);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Set {SetId} was ranked but settling its version-fresh plays failed; the next boot's sweep will retry.", id);
        }
    }
}
