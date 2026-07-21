namespace Typebeat.Web.Packages.Lyrics;

/// <summary>
/// Star rating for a lyric map. Keystroke load (word cost × line pressure/rhythm multiplier) is
/// binned into fixed real-time sections; a section's difficulty is its load PER SECOND — a smooth,
/// artifact-resistant measure of how fast you must type there. A soft maximum (log-sum-exp) over
/// the sections then makes the hardest sustained stretches dominate while length still counts only
/// logarithmically, and — every term being positive — a map can never rate below a subset of
/// itself.
///
/// Kept byte-for-byte in step with the game's port
/// (typebeat-osu: typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricDifficulty) so the in-game star
/// rating and the stored beatmaps.difficulty_rating always agree. The website only ever computes
/// the no-mod baseline (rate = 1); the game feeds a mod-adjusted rate for its live display. Any
/// change here must be mirrored there and <see cref="LyricPace.VERSION"/> bumped so existing rows
/// recompute.
/// </summary>
public static class LyricDifficulty
{
    private const double back_to_back_grace_ms = 100; // gap below which inter-line pressure is full
    private const double back_to_back_tau_ms = 600; // pressure decay constant
    private const double back_to_back_bonus = 0.70; // max inter-line multiplier
    private const double variation_weight = 0.50; // how much rhythm cv scales a line
    private const double variation_cap = 1.5; // cv is clamped here
    private const double section_ms = 1000; // real-time bin width for the per-second load
    private const double spike_focus = 4; // w — how sharply the hardest sections dominate
    private const double star_scale = 0.108; // maps the aggregate to stars (calibrated to real maps)
    private const double star_power = 1.5; // stretches the hard end so top ratings spread
    private const double max_stars = 10;
    private const double min_span_ms = 50; // floor a word's sung span (cv guard)
    private const double repeat_window_ms = 20_000; // "last 20 seconds" for word repetition

    private readonly struct Word
    {
        public readonly string Text; // typeable, lower-case (word-repetition key)
        public readonly int Chars;
        public readonly int Runs;
        public readonly double StartMs; // real-time onset (beatmap time / rate)
        public readonly int LineIndex;

        public Word(string text, int chars, int runs, double startMs, int lineIndex)
        {
            Text = text;
            Chars = chars;
            Runs = runs;
            StartMs = startMs;
            LineIndex = lineIndex;
        }
    }

    /// <summary>Stars for the given lyric lines, under a clock <paramref name="rate"/> (1 = no mod).</summary>
    public static double Compute(IReadOnlyList<LyricLine> lines, double rate = 1)
    {
        if (lines.Count == 0 || rate <= 0)
            return 0;

        var words = new List<Word>();
        double[] lineMultipliers = new double[lines.Count];
        double? prevLineEndMs = null;

        for (int li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            string[] tokens = line.RawText.Split(' ');

            var charDurations = new List<double>(); // per-char durations in this line (for rhythm cv)
            double lastUnitEndMs = line.StartTime;

            for (int j = 0; j < tokens.Length; j++)
            {
                string text = typeableLower(tokens[j]);

                if (text.Length == 0)
                    continue;

                double unitStart, unitEnd;

                if (j < line.Units.Count)
                {
                    unitStart = line.Units[j].StartTime;
                    unitEnd = line.Units[j].EndTime;
                }
                else
                {
                    double span = Math.Max(line.EndTime - line.StartTime, min_span_ms) / tokens.Length;
                    unitStart = line.StartTime + span * j;
                    unitEnd = unitStart + span;
                }

                double spanMs = Math.Max(unitEnd - unitStart, min_span_ms);
                lastUnitEndMs = Math.Max(lastUnitEndMs, unitEnd);

                double perChar = spanMs / text.Length;

                for (int c = 0; c < text.Length; c++)
                    charDurations.Add(perChar);

                words.Add(new Word(text, text.Length, countRuns(text), unitStart / rate, li));
            }

            double pressure;

            if (prevLineEndMs is not double prevEnd)
            {
                pressure = 0; // first line — no run-up
            }
            else
            {
                double gap = (line.StartTime - prevEnd) / rate;
                pressure = gap <= back_to_back_grace_ms ? 1 : Math.Exp(-(gap - back_to_back_grace_ms) / back_to_back_tau_ms);
            }

            double cv = coefficientOfVariation(charDurations);
            lineMultipliers[li] = (1 + back_to_back_bonus * pressure) * (1 + variation_weight * Math.Min(cv, variation_cap));

            prevLineEndMs = lastUnitEndMs;
        }

        if (words.Count == 0)
            return 0;

        // Bin each word's keystroke load into its real-time section.
        var sectionLoad = new Dictionary<long, double>();

        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            double run = 0.5 + 0.5 * ((double)w.Runs / w.Chars);
            double rep = repetitionFactor(words, i);
            double load = (w.Chars + 1) * run * rep * lineMultipliers[w.LineIndex];

            long section = (long)(w.StartMs / section_ms);
            sectionLoad[section] = sectionLoad.GetValueOrDefault(section) + load;
        }

        // Section difficulty = load per second; soft-max over sections (factor out the peak).
        double sectionSeconds = section_ms / 1000.0;
        double maxDifficulty = double.NegativeInfinity;

        foreach (double load in sectionLoad.Values)
        {
            double d = load / sectionSeconds;
            if (d > maxDifficulty)
                maxDifficulty = d;
        }

        double sum = 0;

        foreach (double load in sectionLoad.Values)
            sum += Math.Exp((load / sectionSeconds - maxDifficulty) / spike_focus);

        // Soft-max aggregate (peak + log of how much sustained difficulty sits near it), remapped
        // to stars by a power curve so the hard end spreads. Monotonic in content throughout.
        double aggregate = maxDifficulty / spike_focus + Math.Log(sum);
        double stars = star_scale * Math.Pow(aggregate, star_power);

        return Math.Clamp(stars, 0, max_stars);
    }

    /// <summary>The typeable characters of a token, lower-cased.</summary>
    private static string typeableLower(string token)
    {
        var sb = new System.Text.StringBuilder(token.Length);

        foreach (char c in token)
        {
            if (Typeability.IsTypeable(c))
                sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>Number of maximal runs of identical consecutive characters (e.g. "aaaas" = 2).</summary>
    private static int countRuns(string s)
    {
        if (s.Length == 0)
            return 0;

        int runs = 1;

        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] != s[i - 1])
                runs++;
        }

        return runs;
    }

    /// <summary>rep = max(0.6, 1 − 0.15 · repeats of this exact word within the last 20 s).</summary>
    private static double repetitionFactor(List<Word> words, int i)
    {
        double start = words[i].StartMs;
        int recent = 0;

        for (int j = i - 1; j >= 0; j--)
        {
            if (start - words[j].StartMs > repeat_window_ms)
                break;

            if (words[j].Text == words[i].Text)
                recent++;
        }

        return Math.Max(0.6, 1 - 0.15 * recent);
    }

    /// <summary>Population coefficient of variation (stddev / mean), 0 for fewer than two samples.</summary>
    private static double coefficientOfVariation(List<double> xs)
    {
        if (xs.Count < 2)
            return 0;

        double mean = xs.Average();

        if (mean <= 0)
            return 0;

        double variance = xs.Sum(x => (x - mean) * (x - mean)) / xs.Count;
        return Math.Sqrt(variance) / mean;
    }
}
