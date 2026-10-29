namespace Typebeat.Web.Packages.Lyrics;

/// <summary>
/// Upload checks for word objects. The game refuses to submit a map whose holds a play would
/// not honour (<c>WordObjectsSubmission.Refusal</c>); this is the structural subset the server can
/// check without the engine, so a hand-edited file cannot carry objects the rating would misread.
/// Holds are checked in character order, as the game reads them (<c>HeldWord.UsableHolds</c> sorts by
/// character), because the stored list order is not guaranteed (re-tapping an earlier syllable appends).
/// Ticks are not checked: they move neither the rating nor the score bounds.
/// </summary>
public static class WordObjectsValidation
{
    /// <summary>The first problem found, naming the line and word, or null when every object is well formed.</summary>
    public static string? FirstProblem(IReadOnlyList<LyricLine> lines)
    {
        for (int l = 0; l < lines.Count; l++)
        {
            foreach (TimedUnit unit in lines[l].Units)
            {
                if (unit.Objects.IsEmpty)
                    continue;

                string at = $"line {l + 1}, word \"{unit.Text}\"";

                var rests = HeldWord.RestsOf(unit);
                WordHold? previous = null;

                foreach (WordHold hold in unit.Objects.Holds.OrderBy(h => h.Char))
                {
                    if (hold.Char < 0 || hold.Char >= unit.Text.Length)
                        return $"{at}: a hold sits outside the word.";

                    if (!(hold.EndTime - hold.StartTime >= HeldWord.MIN_HOLD_MS))
                        return $"{at}: a hold is shorter than {HeldWord.MIN_HOLD_MS:0} ms.";

                    if (hold.StartTime < unit.StartTime - 0.5 || hold.EndTime > unit.EndTime)
                        return $"{at}: a hold runs outside the word's timing.";

                    if (previous != null && (hold.Char <= previous.Char || hold.StartTime < previous.EndTime))
                        return $"{at}: two holds overlap or are out of order.";

                    // A hold sits inside one sung stretch: it never spans or overlaps a pause.
                    if (!rests.All(rest => HeldWord.ClearOfRest(hold, rest)))
                        return $"{at}: a hold crosses a pause.";

                    previous = hold;
                }
            }
        }

        return null;
    }
}
