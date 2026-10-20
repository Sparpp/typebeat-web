using System.Text;

namespace Typebeat.Web.Packages.Lyrics
{
    /// <summary>
    /// One cell of a Polyglot <see cref="TypingLine"/> (game's <c>Gameplay.TypingCell</c>), reduced to
    /// what the server reads: the char, whether it is judged, its target and the two Japanese
    /// character-timing figures (<see cref="EndTargetTime"/>, <see cref="MinimumRomajiInputs"/>).
    /// Every engine state field (typed char, judgement, retype history) is gameplay only and absent.
    /// </summary>
    public sealed class TypingCell
    {
        public char Expected { get; }

        public bool IsTypeable { get; }

        public double TargetTime { get; internal set; }

        public double? EndTargetTime { get; internal set; }

        /// <summary>
        /// The fewest romaji keys this Japanese character accepts (game's
        /// <c>TypingCell.MinimumRomajiInputs</c>); 0 on every other cell. The game's score processor
        /// counts a character with a positive value as that many judgements of combo
        /// (<c>TypeBeatScoreProcessor.MaximumComboFor</c>).
        /// </summary>
        public int MinimumRomajiInputs { get; internal set; }

        internal TypingCell(char expected, bool isTypeable, double targetTime)
        {
            Expected = expected;
            IsTypeable = isTypeable;
            TargetTime = targetTime;
        }
    }

    /// <summary>
    /// THE POLYGLOT FLATTENING, a REDUCED mirror of the game's <c>Gameplay/TypingLine.cs</c>: the cells
    /// a Polyglot play is judged on and the targets its rating reads, and nothing else.
    ///
    /// <para>What is mirrored byte for byte (bodies identical below the signatures): the cell passes
    /// of <c>build</c> (the per-token ramp, the paused-stretch interpolation, the punctuation pass,
    /// the monotonic guard and the Literate and default streams), <c>tokenCellTargets</c>,
    /// <c>fillPausedStretches</c>, <c>syllableCharTarget</c>, <c>interpolateUntimedTargets</c>,
    /// <c>CellTargetsFor</c>, <c>configureJapaneseEndpoints</c>, <c>JapaneseCharacterEnd</c>, the
    /// <c>JapaneseInput</c> plan and the <c>CellRules</c>. What is left out, because no server figure
    /// reads it: the syllable groupings, the word groups, the sung-position
    /// polyline, the seal grace and the era setters a replay flips (the server only ever builds the
    /// live eras: word timing on, character timing on, the plan's third era on).</para>
    ///
    /// <para>The aligned target era is built DIRECTLY (the game builds the legacy targets and swaps
    /// the aligned ones in through its lazy factory; the cells and their order are identical, so the
    /// result is too). Pinned against the game's own <c>TypingLine.ForMods</c> by WireCompat's
    /// <c>PolyglotParityTest</c>.</para>
    /// </summary>
    public sealed class TypingLine
    {
        public LyricLine Source { get; }

        public IReadOnlyList<TypingCell> Cells { get; }

        public string DisplayText { get; }

        private readonly int[]? rawCellIndices;
        private bool japanese;
        private LyricLine? japaneseAuthoredLine;
        private JapaneseInputPlan? japaneseInput;
        private bool japaneseWordTiming = true;
        private bool japaneseCharacterTiming;

        internal JapaneseInputPlan? JapaneseInput => japanese ? japaneseInput ??= JapaneseInputPlan.Create(DisplayText, japaneseAuthoredLine, japaneseWordTiming, era3: true, characterTiming: japaneseCharacterTiming) : null;

        private TypingLine(LyricLine source, IReadOnlyList<TypingCell> cells, int[]? rawCellIndices)
        {
            Source = source;
            Cells = cells;
            this.rawCellIndices = rawCellIndices;

            var display = new StringBuilder(cells.Count);

            foreach (var cell in cells)
                display.Append(cell.Expected);

            DisplayText = display.ToString();
        }

        /// <summary>
        /// The game's <c>TypingLine.ForMods</c> with the Polyglot mod on (the only branch the server
        /// builds: without the mod it rates the stored lines directly).
        /// </summary>
        public static TypingLine ForMods(LyricLine line, bool literate, bool polyglot, string? language, bool alignSubdivisionTargets = false, bool japaneseWordTiming = true,
                                         bool japaneseCharacterTiming = false)
        {
            if (!polyglot)
                throw new ArgumentException("The server mirror flattens Polyglot plays only.", nameof(polyglot));

            var derived = PolyglotLine.Derive(line, language, japaneseWordTiming, japaneseCharacterTiming);
            var result = build(derived.Line, literate, CellRules.Polyglot, alignSubdivisionTargets);
            result.japanese = JapaneseReading.IsJapanese(language);
            result.japaneseAuthoredLine = line;
            result.japaneseWordTiming = japaneseWordTiming;
            result.japaneseCharacterTiming = japaneseCharacterTiming;
            result.configureJapaneseEndpoints();
            return result;
        }

        private void configureJapaneseEndpoints()
        {
            if (!japaneseCharacterTiming || JapaneseInput == null)
                return;
            for (int i = 0; i < Cells.Count; i++)
            {
                string? reading = JapaneseInput.CharacterReadingAt(i);
                if (reading == null)
                    continue;
                Cells[i].MinimumRomajiInputs = JapaneseRomaji.MinimumInputs(reading, i + 1 < Cells.Count ? JapaneseInput.CharacterReadingAt(i + 1) ?? "" : "");
                if (Cells[i].MinimumRomajiInputs > 2)
                    Cells[i].EndTargetTime = JapaneseCharacterEnd(i);
            }
            for (int i = 0; i < Cells.Count; i++)
            {
                var group = JapaneseInput.At(i);
                if (group == null || group.StartCell != i)
                    continue;
                string? input = JapaneseRomaji.ShortestInput(group.Reading, JapaneseInput.Following(group));
                int[]? ends = input == null ? null : JapaneseRomaji.InputEnds(group.Reading, input, JapaneseInput.Following(group));
                if (ends == null)
                    continue;
                int kana = 0, previous = 0;
                for (int c = group.StartCell; c < group.EndCellExclusive; c++)
                {
                    kana += (JapaneseInput.CharacterReadingAt(c) ?? "").Length;
                    int end = kana > 0 && kana <= ends.Length ? ends[kana - 1] : previous;
                    Cells[c].MinimumRomajiInputs = Math.Max(1, end - previous);
                    previous = end;
                }
            }
        }

        /// <summary>The end of a Japanese character's sung span, including authored rests.</summary>
        internal double JapaneseCharacterEnd(int cell)
        {
            double end = Source.Units.LastOrDefault()?.EndTime ?? Source.SingEndTime;
            int raw = rawCellIndices?[cell] ?? cell;
            int offset = 0;
            foreach (var unit in Source.Units)
            {
                if (raw >= offset && raw < offset + unit.Text.Length)
                {
                    int local = raw - offset;
                    int ordinal = unit.Text.Take(local).Count(Typeability.IsPolyglotTypeCell);
                    double[] targets = CellTargetsFor(unit, unit.Text.Count(Typeability.IsPolyglotTypeCell), Typeability.IsPolyglotTypeCell);
                    end = ordinal + 1 < targets.Length ? targets[ordinal + 1] : unit.EndTime;
                    foreach (var pause in unit.Pauses)
                        if (pause.SplitChar > local && unit.Text.Take(pause.SplitChar).Count(Typeability.IsPolyglotTypeCell) == ordinal + 1)
                            end = pause.StartTime;
                    break;
                }
                offset += unit.Text.Length + 1;
            }
            return Math.Max(Cells[cell].TargetTime, end);
        }

        /// <summary>The game's <c>TypingLine.CellRules</c>.</summary>
        internal sealed class CellRules
        {
            public static readonly CellRules Default = new CellRules(Typeability.IsCell, Typeability.IsPunctuation, Typeability.DefaultChar);

            public static readonly CellRules Polyglot = new CellRules(Typeability.IsPolyglotTypeCell, Typeability.IsPolyglotPunctuation, Typeability.PolyglotDefaultChar);

            public readonly Func<char, bool> IsCell;
            public readonly Func<char, bool> IsLiteratePunctuation;
            public readonly Func<char, char?> DefaultChar;

            private CellRules(Func<char, bool> isCell, Func<char, bool> isLiteratePunctuation, Func<char, char?> defaultChar)
            {
                IsCell = isCell;
                IsLiteratePunctuation = isLiteratePunctuation;
                DefaultChar = defaultChar;
            }
        }

        private static TypingLine build(LyricLine line, bool literate, CellRules rules, bool alignSubdivisions = false)
        {
            string text = line.RawText;
            var units = line.Units;

            int n = text.Length;
            char[] expected = new char[n];
            bool[] isTypeable = new bool[n];
            double?[] targets = new double?[n];

            // First pass: walk the raw text token by token (spaces delimit tokens; token m maps to Units[m]).
            string[] tokens = text.Split(' ');
            int pos = 0;

            for (int m = 0; m < tokens.Length; m++)
            {
                string token = tokens[m];

                // Guard against malformed data where token count != unit count.
                TimedUnit? unit = units.Count > 0 ? units[Math.Min(m, units.Count - 1)] : null;

                double unitStart = unit?.StartTime ?? line.StartTime;
                double unitEnd = unit?.EndTime ?? line.SingEndTime;

                int k = 0;

                foreach (char ch in token)
                {
                    if (rules.IsCell(ch))
                        k++;
                }

                double[] ramp = tokenCellTargets(token, unitStart, unitEnd, unit, k, rules.IsCell, alignSubdivisions);

                int j = 0;

                foreach (char ch in token)
                {
                    expected[pos] = ch;

                    if (rules.IsCell(ch))
                    {
                        isTypeable[pos] = true;
                        targets[pos] = ramp[j];
                        j++;
                    }

                    pos++;
                }

                // Punctuation must interpolate within the stretch on its authored side of a
                // pause. Interpolating across the whole token would put marks inside the silence.
                // Keep the letter targets above, and use each stretch's edges for missing anchors.
                if (k < token.Length && unit != null && PausedWord.Of(token, unitStart, unitEnd, unit, rules.IsCell) is PausedWord.Cut paused)
                {
                    int tokenStart = pos - token.Length;
                    foreach (var piece in paused.Pieces)
                        interpolateUntimedTargets(targets, tokenStart + piece.FirstChar, piece.CharCount, piece.StartTime, piece.StartTime, piece.EndTime);
                }

                if (m < tokens.Length - 1)
                {
                    // Inter-word space cell: preceding unit's EndTime.
                    expected[pos] = ' ';
                    isTypeable[pos] = true;
                    targets[pos] = unitEnd;
                    pos++;
                }
            }

            interpolateUntimedTargets(targets, 0, n, line.StartTime);

            // Guard: targets non-decreasing (clamp to previous if data is inverted).
            for (int i = 1; i < n; i++)
            {
                if (targets[i]!.Value < targets[i - 1]!.Value)
                    targets[i] = targets[i - 1];
            }

            TypingCell[] cells;
            List<int>? defaultSources = null;

            if (literate)
            {
                cells = new TypingCell[n];

                for (int i = 0; i < n; i++)

                    cells[i] = new TypingCell(expected[i], isTypeable[i] || rules.IsLiteratePunctuation(expected[i]), targets[i]!.Value);

            }
            else
            {
                var sb = new StringBuilder(n);
                var sources = new List<int>(n);
                Typeability.ProjectDefault(text, sb, sources, rules.DefaultChar);
                defaultSources = sources;

                cells = new TypingCell[sources.Count];

                for (int i = 0; i < sources.Count; i++)
                {
                    int src = sources[i];
                    cells[i] = new TypingCell(sb[i], rules.IsCell(sb[i]), targets[src]!.Value);
                }
            }

            return new TypingLine(line, cells, defaultSources?.ToArray());
        }

        /// <summary>Interpolate untimed marks using only anchors inside one permitted span.</summary>
        private static void interpolateUntimedTargets(double?[] targets, int start, int count, double fallback,
                                                       double? leading = null, double? trailing = null)
        {
            int limit = start + count;
            for (int i = start; i < limit; i++)
            {
                if (targets[i].HasValue)
                    continue;

                int end = i;
                while (end + 1 < limit && !targets[end + 1].HasValue)
                    end++;

                double? before = i > start ? targets[i - 1] : leading;
                double? after = end + 1 < limit ? targets[end + 1] : trailing;
                int length = end - i + 1;
                for (int m = 0; m < length; m++)
                {
                    targets[i + m] = before is double p
                        ? (after is double q ? p + (m + 1) * (q - p) / (length + 1) : p)
                        : after ?? fallback;
                }
                i = end;
            }
        }

        internal static double syllableCharTarget(double unitStart, double unitEnd, IReadOnlyList<double> boundaries, int k, int j, IReadOnlyList<int>? cellCuts = null)
        {
            if (k <= 0)
                return unitStart;

            if (boundaries.Count == 0)
                return unitStart + (double)j * (unitEnd - unitStart) / k;

            int segments = boundaries.Count + 1;
            int s;
            double segIndexLo;
            double segIndexHi;

            if (cellCuts != null && cellCuts.Count == segments + 1)
            {
                s = SyllableSegments.SegmentOf(cellCuts, j);
                segIndexLo = cellCuts[s];
                segIndexHi = cellCuts[s + 1];
            }
            else
            {
                // Which segment holds char index j (floor of j scaled into segment-space), clamped to the last.
                s = (int)Math.Floor((double)j * segments / k);

                if (s >= segments)
                    s = segments - 1;

                segIndexLo = (double)s * k / segments;
                segIndexHi = (double)(s + 1) * k / segments;
            }

            double timeLo = s == 0 ? unitStart : boundaries[s - 1];
            double timeHi = s == segments - 1 ? unitEnd : boundaries[s];

            if (segIndexHi <= segIndexLo)
                return timeLo;

            return timeLo + (j - segIndexLo) / (segIndexHi - segIndexLo) * (timeHi - timeLo);
        }

        private static double[] tokenCellTargets(string token, double unitStart, double unitEnd, TimedUnit? unit, int k, Func<char, bool>? isCell = null, bool alignSubdivisions = false)
        {
            var ramp = new double[k];

            if (k <= 0)
                return ramp;

            var boundaries = unit?.SyllableBoundaries ?? Array.Empty<double>();

            if (unit != null && PausedWord.Of(token, unitStart, unitEnd, unit, isCell) is PausedWord.Cut cut)
            {
                fillPausedStretches(cut, ramp, token, isCell, alignSubdivisions);
                return ramp;
            }

            int[]? cellCuts;
            if (alignSubdivisions && boundaries.Count > 0)
            {
                // The editor and judgement groups use this split even when it is derived.
                // Spreading by segment count alone moves the caret into unrelated letters.
                var splits = SyllableSegments.SplitsFor(token, boundaries.Count + 1, unit?.SyllableSplits);
                cellCuts = SyllableSegments.CellCuts(token, splits, isCell);
                // A short word can yield fewer groups than requested. Its final group owns the tail.
                boundaries = boundaries.Take(splits.Count).ToArray();
            }
            else
                cellCuts = unit != null && SyllableSegments.IsAuthoredValid(token, boundaries.Count + 1, unit.SyllableSplits)
                    ? SyllableSegments.CellCuts(token, unit.SyllableSplits, isCell)
                    : null;

            for (int j = 0; j < k; j++)
                ramp[j] = syllableCharTarget(unitStart, unitEnd, boundaries, k, j, cellCuts);

            return ramp;
        }

        private static void fillPausedStretches(PausedWord.Cut cut, double[] ramp, string token, Func<char, bool>? isCell, bool alignSubdivisions)
        {
            foreach (var piece in cut.Pieces)
            {
                var boundaries = piece.Boundaries;
                var cellCuts = piece.CellCuts;
                if (alignSubdivisions && cellCuts == null && boundaries.Count > 0)
                {
                    var splits = piece.Cuts.Select(c => c - piece.FirstChar).ToArray();
                    cellCuts = SyllableSegments.CellCuts(token.Substring(piece.FirstChar, piece.CharCount), splits, isCell);
                    boundaries = boundaries.Take(splits.Length).ToArray();
                }
                for (int j = 0; j < piece.CellCount; j++)
                    ramp[piece.FirstCell + j] = syllableCharTarget(piece.StartTime, piece.EndTime, boundaries, piece.CellCount, j, cellCuts);
            }
        }

        /// <summary>
        /// One word's per-cell targets, the SAME spread gameplay judges on (game's
        /// <c>TypingLine.CellTargetsFor</c>).
        /// </summary>
        internal static double[] CellTargetsFor(TimedUnit unit, int typeableCount, Func<char, bool>? isCell = null)
            => tokenCellTargets(unit.Text, unit.StartTime, unit.EndTime, unit, typeableCount, isCell, alignSubdivisions: true);
    }
}
