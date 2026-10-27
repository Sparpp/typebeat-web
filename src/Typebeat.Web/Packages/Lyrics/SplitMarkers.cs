using System.Text;

namespace Typebeat.Web.Packages.Lyrics
{
    /// <summary>
    /// The '|' authoring mark (game's <c>Beatmaps/SplitMarkers.cs</c>). Ratings do not read it; the parse
    /// records it in <see cref="TimedUnit.SplitMarkerSyllables"/> for <see cref="JudgedCells"/>.
    /// </summary>
    public static class SplitMarkers
    {
        /// <summary>The mark itself (game's <c>Typeability.SPLIT_MARKER</c>).</summary>
        public const char SPLIT_MARKER = '|';

        /// <summary>Whether <paramref name="text"/> carries a pipe at all.</summary>
        public static bool Carries(string? text) => !string.IsNullOrEmpty(text) && text.IndexOf(SPLIT_MARKER) >= 0;

        /// <summary>
        /// Splits pipe-bearing text into the text that gets STORED and, per surviving token, the
        /// character positions the pipes sat at (measured in the stripped token). A token that was
        /// nothing but pipes disappears entirely rather than becoming an empty word.
        /// </summary>
        public static (string Text, IReadOnlyList<IReadOnlyList<int>> Pipes) Strip(string withMarkers)
        {
            var texts = new List<string>();
            var pipes = new List<IReadOnlyList<int>>();

            foreach (string token in (withMarkers ?? string.Empty).Split(' '))
            {
                if (token.IndexOf(SPLIT_MARKER) < 0)
                {
                    if (token.Length == 0)
                        continue;

                    texts.Add(token);
                    pipes.Add(Array.Empty<int>());
                    continue;
                }

                var sb = new StringBuilder(token.Length);
                var positions = new List<int>();

                foreach (char c in token)
                {
                    if (c == SPLIT_MARKER)
                        positions.Add(sb.Length);
                    else
                        sb.Append(c);
                }

                if (sb.Length == 0)
                    continue;

                texts.Add(sb.ToString());
                pipes.Add(positions);
            }

            return (string.Join(' ', texts), pipes);
        }

        /// <summary>
        /// <paramref name="segments"/> equal slices of [<paramref name="start"/>, <paramref name="end"/>],
        /// returned as the <c>segments - 1</c> internal boundaries; empty for a word with no span.
        /// </summary>
        private static double[] EvenBoundaries(double start, double end, int segments)
        {
            if (segments < 2 || end <= start)
                return Array.Empty<double>();

            double[] boundaries = new double[segments - 1];

            for (int i = 0; i < boundaries.Length; i++)
                boundaries[i] = start + (end - start) * (i + 1) / segments;

            return boundaries;
        }

        /// <summary>
        /// The boundary/split pair one word's pipes author, or null when they author nothing: the
        /// pattern is not a legal cut of <paramref name="token"/> or the word has no span to divide.
        /// </summary>
        public static (IReadOnlyList<double> Boundaries, IReadOnlyList<int> Splits)? Authored(string token, double start, double end, IReadOnlyList<int> pipes)
        {
            int segments = pipes.Count + 1;

            if (pipes.Count == 0 || !SyllableSegments.IsAuthoredValid(token, segments, pipes))
                return null;

            double[] boundaries = EvenBoundaries(start, end, segments);

            if (boundaries.Length != pipes.Count)
                return null;

            return (boundaries, pipes.ToArray());
        }
    }
}
