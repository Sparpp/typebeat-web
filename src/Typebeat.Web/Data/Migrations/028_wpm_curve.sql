-- typebeat-web migration 028: the map's PEAK typing pace and the shape of that pace over map time
-- (LyricPace v11). The game computes the same three numbers locally from the beatmap it has loaded
-- (typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricWpmCurve.cs, mirrored here as
-- Packages/Lyrics/LyricWpmCurve.cs); these columns exist so the SET PAGE can draw the same graph
-- without reparsing a .osu blob on every request. Deliberately NOT on the wire: the client already
-- has the map, so shipping the curve to it would only create a second thing to keep in step.
--
-- None of this can be computed in SQL. LyricWpmCurve sweeps a 30-cell rolling window over the cell
-- target times it reconstructs from the stored .osu lyric data, exactly as 009 / 016 / 018 / 020
-- could not do their backfills here either. So this migration only adds the columns, and
-- Packages/PaceBackfill.cs reparses the stored blobs at startup and fills them; the LyricPace
-- VERSION bump to 11 is what makes that sweep revisit every existing row.
--
-- NULL, and no DEFAULT, rather than 0: "no reading" has to stay distinguishable from a genuine 0
-- WPM, and two different rows are NULL here. One the backfill has not reached yet; one it HAS
-- reached and found unmeasurable (fewer than LyricWpmCurve.WINDOW_CELLS = 30 typeable cells, or no
-- span at all, which it reports as IsEmpty). Both mean the same thing to a reader, "there is no
-- pace curve for this difficulty", and the set page renders a note in place of the graph for both.
ALTER TABLE beatmaps ADD COLUMN peak_wpm double precision;
ALTER TABLE beatmaps ADD COLUMN peak_cpm double precision;

-- real[], not double precision[] and not jsonb. These are display values a bar graph rounds to
-- integers, so float4's ~7 significant digits are several more than anything reads, at half the
-- bytes; and it stays an ARRAY because it is a fixed-shape keyless numeric vector, which is what
-- arrays are for, and Npgsql hands it to Dapper as a float[] with no parsing step. Length is
-- LyricWpmCurve.DEFAULT_CURVE_POINTS (100), but nothing in SQL or on the page depends on that:
-- a shorter or longer array renders as however many bars it holds.
ALTER TABLE beatmaps ADD COLUMN wpm_curve real[];
