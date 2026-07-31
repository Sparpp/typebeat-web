using System.Text;

namespace Typebeat.Web.Packages.Lyrics;

/// <summary>
/// Guesses a song's language from its lyric text, for the one-shot backfill of the sets that
/// predate the mapper-supplied language tag (019_language.sql, <see cref="LanguageBackfill"/>).
///
/// DESIGN CONSTRAINTS, and why this is a hand-rolled ~200 lines instead of a library:
///
///  * OFFLINE AND DETERMINISTIC. It runs inside app startup on a small prod box; it may not make
///    network calls, may not touch a model file on disk, and must return the same answer for the
///    same input forever (a re-run must never churn a set from one language to another).
///  * The alternative considered was NTextCat, the usual small offline .NET identifier. It is a
///    fine library, but it wants a multi-megabyte character-n-gram profile file shipped alongside
///    the binary (the deploy is deliberately a single self-contained artifact), and it is tuned
///    for prose: it is measurably worse than stopword counting on the short, repetitive,
///    punctuation-free, heavily-English-loanword text that song lyrics actually are. It buys
///    nothing here that the two mechanisms below do not already cover.
///  * BEST EFFORT, NEVER CONFIDENT NONSENSE. Everything the detector cannot separate cleanly
///    returns null, which the backfill stores as "still unknown" rather than a guess; the mapper's
///    own answer (which arrives with any post-task-58 upload) always overrides whatever this
///    produced.
///
/// TWO MECHANISMS, IN ORDER:
///
///  1. A UNICODE SCRIPT CENSUS. Kana, hangul, han and cyrillic identify japanese / korean /
///     chinese / russian essentially for free, and the remaining non-latin scripts (greek, arabic,
///     hebrew, thai, devanagari, ...) collapse to "other". This settles the overwhelming majority
///     of non-english maps with no word knowledge at all, and it is immune to the loanword problem
///     (a J-pop chorus in English still carries kana in the verses).
///  2. STOPWORD SCORING for latin-script text, over the compact embedded profiles below. Function
///     words are the highest-signal, lowest-storage discriminator available: they are the most
///     frequent tokens in any real lyric, they barely overlap between languages, and a few dozen
///     per language fit in this file. Diacritic evidence (Polish's stroked/ogonek letters,
///     Spanish's n-tilde, German's eszett, ...) contributes a smaller weighted bonus, because it
///     is decisive when present but frequently stripped by lyric transcribers.
/// </summary>
public static class LanguageDetector
{
    /// <summary>
    /// A classification needs this many letters to be attempted at all. Below it a single stray
    /// loanword swings the answer, so the honest result is "unknown".
    /// </summary>
    private const int min_letters = 24;

    /// <summary>
    /// Share of all letters a non-latin script must reach to claim the song. Well under half on
    /// purpose: latin-script chunks (English chorus, romanised title line, artist credits) are
    /// routine in CJK and cyrillic lyrics, so demanding a majority would misfile them as english.
    /// </summary>
    private const double non_latin_share = 0.15;

    /// <summary>Share of tokens the winning latin profile must match before any answer is given.</summary>
    private const double min_stopword_hit_rate = 0.06;

    /// <summary>How far ahead of the runner-up the winner must be to be trusted (relative).</summary>
    private const double min_stopword_margin = 1.25;

    /// <summary>
    /// The detected canonical language name (a member of <see cref="BeatmapLanguages.All"/>), or
    /// null when the text is too short, too ambiguous, or in nothing this knows about. Never
    /// throws: any input, including empty, is a valid "unknown".
    /// </summary>
    public static string? Detect(string? lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics))
            return null;

        var census = ScriptCensus.Of(lyrics);

        if (census.TotalLetters < min_letters)
            return null;

        // ---- 1. script census ----

        // Korean first: hangul is unambiguous and korean lyrics mix in latin more than any other
        // CJK language, so testing it before the han-based rules avoids losing it to a tie-break.
        if (census.Share(census.Hangul) >= non_latin_share)
            return "korean";

        // Kana settles japanese-vs-chinese: japanese prose cannot avoid it, chinese has none. Han
        // is counted with it because a japanese line is usually mostly kanji by character count.
        if (census.Kana >= 2 && census.Share(census.Kana + census.Han) >= non_latin_share)
            return "japanese";

        if (census.Share(census.Han) >= non_latin_share)
            return "chinese";

        // Cyrillic is shared with ukrainian/bulgarian/serbian, none of which are in the vocabulary;
        // russian is the only cyrillic member, so it absorbs them. Documented, deliberate.
        if (census.Share(census.Cyrillic) >= non_latin_share)
            return "russian";

        if (census.Share(census.OtherScript) >= non_latin_share)
            return "other";

        // ---- 2. latin stopword scoring ----

        if (census.Share(census.Latin) < 0.5)
            return null;

        return detectLatin(lyrics);
    }

    private static string? detectLatin(string lyrics)
    {
        // Vocalise is stripped BEFORE scoring, out of both the numerator and the denominator. It
        // is not weak evidence, it is anti-evidence: "na na na" is spelled identically in every
        // language on earth, yet "na" is also a genuine top-ten polish function word, so leaving
        // it in makes any sufficiently wordless chorus read as polish. Removing it means a lyric
        // that is ONLY vocalise falls below the token floor below and is reported as unknown,
        // which is the correct answer.
        var tokens = Tokenize(lyrics).Where(t => !vocalise.Contains(t)).ToList();

        if (tokens.Count < 8)
            return null;

        string? best = null, runnerUp = null;
        double bestScore = 0, runnerUpScore = 0;

        foreach (var (language, profile) in profiles)
        {
            double score = profile.Score(tokens);

            if (score > bestScore)
            {
                (runnerUp, runnerUpScore) = (best, bestScore);
                (best, bestScore) = (language, score);
            }
            else if (score > runnerUpScore)
            {
                (runnerUp, runnerUpScore) = (language, score);
            }
        }

        if (best == null || bestScore < min_stopword_hit_rate)
            return null;

        // A clear winner, or nothing. Two profiles within 25% of each other on lyric-length text
        // are not distinguishable (english/german on a chorus of "no no no", spanish/italian on
        // heavily-elided text), and a coin flip there is worse than leaving the row for a human
        // or for the mapper's own upload to settle.
        if (runnerUp != null && runnerUpScore > 0 && bestScore < runnerUpScore * min_stopword_margin)
            return null;

        return best;
    }

    /// <summary>
    /// Sung filler that belongs to no language. Deliberately conservative: anything that is also a
    /// real function word somewhere in the vocabulary is left OUT of this set (italian "ho"/"e",
    /// swedish "ha", ...), because wrongly discarding real evidence is worse than tolerating a
    /// little filler.
    /// </summary>
    private static readonly HashSet<string> vocalise = new(StringComparer.Ordinal)
    {
        "la", "lala", "lalala", "na", "nana", "nanana", "oh", "ohh", "ooh", "oooh", "ohoh",
        "ah", "aah", "ahh", "uh", "uhh", "hm", "hmm", "mm", "mmm", "mhm",
        "yeah", "yeh", "yea", "hey", "woah", "whoa", "woo", "wooh", "yo",
        "doo", "dum", "tra", "shalala", "haha", "hahaha", "ay", "aye", "ey", "oi", "ba", "dada",
    };

    /// <summary>
    /// Lowercased, diacritic-preserving word tokens. Splitting on "not a letter" keeps the
    /// apostrophe-elided forms that matter most in french and italian ("l", "d", "c" fall out as
    /// their own tokens, which is exactly how the profiles below score them).
    /// </summary>
    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (char ch in text)
        {
            if (char.IsLetter(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    /// <summary>
    /// Per-language evidence: a stopword set (each occurrence scores 1) plus characteristic
    /// letters (each occurrence scores <see cref="diacritic_weight"/>, so a handful of them can
    /// tip a close call without ever outvoting real word evidence). The score is normalised by
    /// token count, making it comparable across profiles and independent of lyric length.
    /// </summary>
    private sealed class Profile(string[] stopwords, string diacritics)
    {
        private const double diacritic_weight = 0.5;

        private readonly HashSet<string> words = new(stopwords, StringComparer.Ordinal);
        private readonly HashSet<char> marks = [.. diacritics];

        public double Score(IReadOnlyList<string> tokens)
        {
            double hits = 0;

            foreach (string token in tokens)
            {
                if (words.Contains(token))
                    hits++;

                foreach (char ch in token)
                {
                    if (marks.Contains(ch))
                        hits += diacritic_weight;
                }
            }

            return hits / tokens.Count;
        }
    }

    /// <summary>
    /// The latin-script members of the vocabulary. "instrumental" and "other" are deliberately
    /// absent: neither is a thing text can look like, so neither is ever guessed here (a set with
    /// no lyric text at all stays unknown; see <see cref="LanguageBackfill"/> for why that is not
    /// reported as instrumental).
    /// </summary>
    private static readonly (string Language, Profile Profile)[] profiles =
    [
        ("english", new Profile(
        [
            "the", "and", "you", "your", "youre", "i", "im", "ive", "it", "its", "is", "are", "was",
            "were", "be", "been", "to", "of", "in", "on", "at", "for", "with", "that", "this",
            "but", "not", "dont", "cant", "wont", "all", "we", "she", "he", "they", "my", "me",
            "know", "just", "like", "love", "never", "away", "down", "up", "out", "wanna", "gonna",
            "girl", "baby", "time", "night", "heart", "when", "what", "there", "here", "no", "so",
        ], string.Empty)),

        ("french", new Profile(
        [
            "le", "la", "les", "un", "une", "des", "du", "de", "et", "est", "que", "qui", "je",
            "tu", "il", "elle", "nous", "vous", "ils", "on", "ne", "pas", "plus", "pour", "dans",
            "sur", "avec", "mais", "comme", "tout", "tous", "toute", "moi", "toi", "mon", "ma",
            "mes", "ton", "ta", "ses", "son", "sa", "cest", "jai", "au", "aux", "ou", "où",
            "quand", "aime", "amour", "coeur", "cœur", "rien", "encore", "toujours", "faire",
        ], "çéèêëàâîïôûùœ")),

        ("german", new Profile(
        [
            "der", "die", "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "und",
            "ist", "sind", "war", "nicht", "ich", "du", "er", "sie", "wir", "ihr", "mir", "mich",
            "dich", "dir", "sich", "mit", "auf", "für", "von", "zu", "aus", "im", "am", "so",
            "auch", "noch", "nur", "wie", "was", "wenn", "wer", "aber", "doch", "mehr", "immer",
            "nie", "hier", "dass", "kann", "will", "hat", "habe", "haben", "sein", "wird", "über",
        ], "äöüß")),

        ("spanish", new Profile(
        [
            "el", "la", "los", "las", "un", "una", "unos", "unas", "de", "del", "y", "que", "en",
            "es", "son", "por", "para", "con", "sin", "no", "si", "me", "te", "se", "le", "lo",
            "mi", "tu", "su", "yo", "más", "mas", "pero", "como", "cuando", "porque", "todo",
            "toda", "nada", "quiero", "amor", "corazón", "corazon", "vida", "noche", "eres",
            "estoy", "está", "esta", "voy", "vas", "ya", "muy", "bien", "aquí", "aqui", "ahora",
        ], "ñáíóú")),

        ("italian", new Profile(
        [
            "il", "lo", "la", "gli", "le", "un", "uno", "una", "di", "del", "della", "dei", "che",
            "chi", "e", "ed", "è", "sono", "non", "per", "con", "come", "ma", "se", "mi", "ti",
            "si", "ci", "vi", "mio", "mia", "tuo", "tua", "sua", "questo", "questa", "quando",
            "più", "piu", "solo", "cosa", "così", "cosi", "amore", "cuore", "notte", "vita",
            "sempre", "ancora", "niente", "tutto", "tutti", "voglio", "adesso", "perché", "perche",
        ], "àèéìòù")),

        ("polish", new Profile(
        [
            "i", "w", "z", "na", "do", "nie", "to", "jest", "się", "sie", "że", "ze", "co", "jak",
            "ale", "już", "juz", "tylko", "tak", "gdy", "kiedy", "mnie", "ciebie", "ja", "ty",
            "my", "wy", "on", "ona", "oni", "mój", "moj", "twój", "twoj", "swoje", "przez", "dla",
            "od", "po", "za", "bez", "jeszcze", "wszystko", "nic", "kocham", "serce", "noc",
            "znów", "znow", "bardzo", "będzie", "bedzie", "była", "byla", "gdzie", "czy", "który",
        ], "ąćęłńóśźż")),

        ("swedish", new Profile(
        [
            "och", "att", "det", "som", "en", "ett", "den", "de", "är", "ar", "för", "for", "med",
            "på", "pa", "av", "till", "inte", "har", "hade", "jag", "du", "vi", "ni", "han", "hon",
            "min", "din", "sin", "men", "om", "när", "nar", "då", "da", "så", "sa", "vad", "här",
            "har", "där", "dar", "alla", "aldrig", "alltid", "bara", "hjärta", "hjarta", "natt",
            "kärlek", "karlek", "livet", "vill", "kan", "ska", "skall", "vara", "över", "over",
        ], "åäö")),
    ];

    /// <summary>
    /// Per-script letter counts for one text. Only letters are counted; digits, punctuation and
    /// whitespace are ignored entirely, so timestamps and formatting cannot skew a share.
    /// </summary>
    internal readonly struct ScriptCensus
    {
        public int Latin { get; private init; }
        public int Kana { get; private init; }
        public int Han { get; private init; }
        public int Hangul { get; private init; }
        public int Cyrillic { get; private init; }
        public int OtherScript { get; private init; }

        public int TotalLetters => Latin + Kana + Han + Hangul + Cyrillic + OtherScript;

        public double Share(int count) => TotalLetters == 0 ? 0 : (double)count / TotalLetters;

        public static ScriptCensus Of(string text)
        {
            int latin = 0, kana = 0, han = 0, hangul = 0, cyrillic = 0, other = 0;

            foreach (char ch in text)
            {
                if (!char.IsLetter(ch))
                    continue;

                switch (ch)
                {
                    // Hiragana, katakana, katakana phonetic extensions, halfwidth katakana. The
                    // prolonged-sound mark U+30FC is inside the katakana block already.
                    case >= '぀' and <= 'ヿ':
                    case >= 'ㇰ' and <= 'ㇿ':
                    case >= 'ｦ' and <= 'ﾝ':
                        kana++;
                        break;

                    // CJK unified ideographs (+ extension A). Shared by chinese and japanese; the
                    // kana count is what separates them.
                    case >= '㐀' and <= '䶿':
                    case >= '一' and <= '鿿':
                    case >= '豈' and <= '﫿':
                        han++;
                        break;

                    // Hangul syllables + jamo.
                    case >= 'ᄀ' and <= 'ᇿ':
                    case >= '㄰' and <= '㆏':
                    case >= '가' and <= '힣':
                        hangul++;
                        break;

                    case >= 'Ѐ' and <= 'ӿ':
                    case >= 'Ԁ' and <= 'ԯ':
                        cyrillic++;
                        break;

                    default:
                        // Basic latin, latin-1 supplement, latin extended-A/B and the additional
                        // extended blocks: everything the stopword profiles operate on.
                        if (ch < 'ɐ' || (ch >= 'Ḁ' && ch <= 'ỿ'))
                            latin++;
                        else
                            other++;
                        break;
                }
            }

            return new ScriptCensus
            {
                Latin = latin,
                Kana = kana,
                Han = han,
                Hangul = hangul,
                Cyrillic = cyrillic,
                OtherScript = other,
            };
        }
    }

    /// <summary>
    /// Every value <see cref="Detect"/> is capable of returning. Exists so the contract "the
    /// detector can only ever produce a storable canonical name" is assertable rather than a
    /// comment; nothing in the app reads it.
    /// </summary>
    public static IEnumerable<string> DetectableLanguages =>
        new[] { "japanese", "korean", "chinese", "russian", "other" }
            .Concat(profiles.Select(p => p.Language));
}
