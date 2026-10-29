namespace Typebeat.Web.Packages.Lyrics
{
    /// <summary>Which holds a word honours (game's <c>Gameplay/HeldWord.cs</c>), read by <see cref="JudgedCells"/>.</summary>
    internal static class HeldWord
    {
        internal const double MIN_HOLD_MS = 100;

        /// <summary>
        /// The usable holds of <paramref name="unit"/>, in character order: on a cell, at least
        /// <see cref="MIN_HOLD_MS"/> long, inside the word (a first-letter hold starts with it), not
        /// overlapping, between the word's cuts, clear of its rests (<see cref="ClearOfRest"/>), and ending
        /// after its letter is due.
        /// </summary>
        internal static IReadOnlyList<WordHold> UsableHolds(TimedUnit unit, Func<char, bool> isCell)
        {
            var holds = unit.Objects.Holds;

            if (holds.Count == 0)
                return Array.Empty<WordHold>();

            string text = unit.Text;
            var rests = RestsOf(unit, isCell);
            var cuts = cutsOf(unit, rests.Count > 0, isCell).ToArray();
            var kept = new List<WordHold>();
            int lastChar = -1;
            double lastEnd = unit.StartTime;

            foreach (var hold in holds.OrderBy(h => h.Char))
            {
                int c = hold.Char;
                bool hasNext = c + 1 < text.Length;

                if (c <= lastChar || c < 0 || c >= text.Length || !isCell(text[c]))
                    continue;

                if (hold.EndTime - hold.StartTime < MIN_HOLD_MS || hold.StartTime < lastEnd)
                    continue;

                bool startOk = c == 0
                    ? Math.Abs(hold.StartTime - unit.StartTime) < 0.5
                    : hold.StartTime > unit.StartTime && hold.StartTime < unit.EndTime;

                bool endOk = hasNext ? hold.EndTime < unit.EndTime : hold.EndTime <= unit.EndTime;

                if (!startOk || !endOk)
                    continue;

                bool cutsOk = cuts.All(cut => cut.Char < c ? cut.Time < hold.StartTime
                    : cut.Char == c || (cut.Char == c + 1 ? cut.Time >= hold.EndTime : cut.Time > hold.EndTime));

                // The hold must end after its letter is due, or the on-time press would miss its tail.
                if (!cutsOk || !rests.All(rest => ClearOfRest(hold, rest)) || hold.EndTime <= TypingLine.HoldFreeCellTarget(unit, c, isCell))
                    continue;

                kept.Add(hold);
                lastChar = c;
                lastEnd = hold.EndTime;
            }

            return kept;
        }

        /// <summary>The rests a play honours on <paramref name="unit"/> (<see cref="PausedWord.UsableRests"/>), in time order.</summary>
        internal static IReadOnlyList<WordPause> RestsOf(TimedUnit unit, Func<char, bool>? isCell = null)
            => unit.Pauses.Count == 0
                ? Array.Empty<WordPause>()
                : PausedWord.UsableRests(unit.Text, unit.StartTime, unit.EndTime, unit.Pauses, isCell ?? Typeability.IsCell);

        /// <summary>
        /// A hold sits wholly inside one sung stretch of its word: a letter before the rest's cut is let go by
        /// the rest's start, a letter after it is held from the rest's end. A hold never spans or overlaps a rest.
        /// </summary>
        internal static bool ClearOfRest(WordHold hold, WordPause rest)
            => hold.Char < rest.SplitChar ? hold.EndTime <= rest.StartTime : hold.StartTime >= rest.EndTime;

        /// <summary>
        /// The word's cuts as (character, time). On a paused word, each stretch's own cuts against its own
        /// boundaries (<see cref="PausedWord.Of"/>); the rests themselves are <see cref="ClearOfRest"/>'s.
        /// </summary>
        private static IEnumerable<(int Char, double Time)> cutsOf(TimedUnit unit, bool paused, Func<char, bool> isCell)
        {
            if (paused && PausedWord.Of(unit.Text, unit.StartTime, unit.EndTime, unit, isCell) is PausedWord.Cut cut)
            {
                foreach (var piece in cut.Pieces)
                {
                    for (int i = 0; i < piece.Cuts.Count && i < piece.Boundaries.Count; i++)
                        yield return (piece.Cuts[i], piece.Boundaries[i]);
                }

                yield break;
            }

            var splits = SyllableSegments.SplitsFor(unit);

            for (int i = 0; i < splits.Count && i < unit.SyllableBoundaries.Count; i++)
                yield return (splits[i], unit.SyllableBoundaries[i]);
        }
    }

    /// <summary>Which of a hold's authored ticks a play honours (game's <c>Gameplay/HoldTicks.cs</c>, the members the count reads).</summary>
    internal static class HoldTicks
    {
        /// <summary>The earliest a tick may sit: clear of the press.</summary>
        private static double WindowStart(double start) => start + HoldTailRule.MIN_SLACK_MS;

        /// <summary>A tick must sit before this: clear of the Great release window.</summary>
        private static double WindowEnd(double start, double end) => end - HoldTailRule.Slack(start, end);

        /// <summary>The hold's ticks rounded to whole ms, inside [<see cref="WindowStart"/>, <see cref="WindowEnd"/>), distinct and ascending.</summary>
        public static double[] UsableTicks(WordHold hold)
        {
            var authored = hold.Ticks;

            if (authored.Count == 0 || !double.IsFinite(hold.StartTime) || !double.IsFinite(hold.EndTime) || hold.EndTime <= hold.StartTime)
                return Array.Empty<double>();

            double lo = WindowStart(hold.StartTime);
            double hi = WindowEnd(hold.StartTime, hold.EndTime);
            var kept = new SortedSet<double>();

            foreach (double tick in authored)
            {
                if (!double.IsFinite(tick))
                    continue;

                double t = Math.Round(tick);

                if (t >= lo && t < hi)
                    kept.Add(t);
            }

            return kept.Count == 0 ? Array.Empty<double>() : kept.ToArray();
        }
    }

    /// <summary>The release grading's window constants (game's <c>Gameplay/HoldTailRule.cs</c>, the members the count reads).</summary>
    internal static class HoldTailRule
    {
        public const double MIN_SLACK_MS = 80;
        public const double SLACK_FRACTION = 0.1;

        /// <summary>How long before the end a release still counts as Great. Also the hand-off window.</summary>
        public static double Slack(double start, double end) => Math.Max(MIN_SLACK_MS, SLACK_FRACTION * Math.Max(1, end - start));
    }
}
