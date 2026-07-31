using Typebeat.Web.Packages;
using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Tests;

/// <summary>
/// The offline song-language heuristic behind the 019_language.sql backfill: the Unicode script
/// census (japanese / korean / chinese / russian / other), the latin stopword profiles, and, just
/// as importantly, the cases where it must REFUSE to answer. A wrong guess is recoverable (the
/// mapper's own tag overrides it); a confident wrong guess on text the detector cannot actually
/// read is not, so the "returns null" tests below are the load-bearing half of this fixture.
/// </summary>
public class LanguageDetectorTest
{
    // ---- script census ----

    [Test]
    public void Japanese_IsDetectedFromKana_EvenWhenMostlyKanji()
    {
        const string lyrics =
            """
            夜空に光る星を数えて
            君の声がまだ聞こえてる
            この道の先に何があっても
            もう一度だけ走り出すよ
            """;

        Assert.That(LanguageDetector.Detect(lyrics), Is.EqualTo("japanese"));
    }

    [Test]
    public void Japanese_SurvivesAnEnglishChorus()
    {
        // The loanword problem that makes n-gram identifiers wobble on lyrics: a J-pop chorus is
        // routinely english. The kana in the verses must still decide the song.
        const string lyrics =
            """
            もう一度だけ君に会いたいよ
            この夜が明ける前に
            I wanna hold you tonight, never let you go
            shine on, shine on, we are alive tonight
            """;

        Assert.That(LanguageDetector.Detect(lyrics), Is.EqualTo("japanese"));
    }

    [Test]
    public void Chinese_IsHanWithoutKana()
    {
        const string lyrics =
            """
            我在城市的夜色里等你
            风吹过安静的街道
            那些说过的话还在心上
            我们一起走过的时光
            """;

        Assert.That(LanguageDetector.Detect(lyrics), Is.EqualTo("chinese"));
    }

    [Test]
    public void Korean_IsDetectedFromHangul()
    {
        const string lyrics =
            """
            어두운 밤하늘에 별이 빛나고
            너의 목소리가 들려오는 것 같아
            우리가 함께 걸었던 그 길에서
            다시 한번 너를 만나고 싶어
            """;

        Assert.That(LanguageDetector.Detect(lyrics), Is.EqualTo("korean"));
    }

    [Test]
    public void Russian_IsDetectedFromCyrillic()
    {
        const string lyrics =
            """
            Я иду по ночному городу
            И огни горят надо мной
            Ты сказала что всё будет хорошо
            Но я снова остался один
            """;

        Assert.That(LanguageDetector.Detect(lyrics), Is.EqualTo("russian"));
    }

    [Test]
    public void UnknownScript_FallsBackToOther()
    {
        // Greek is not a vocabulary member; "other" is the honest answer, not a null.
        const string lyrics =
            """
            Θυμάμαι ακόμα το καλοκαίρι
            που περπατούσαμε στην παραλία
            και τα λόγια σου μέσα στο μυαλό
            δεν με αφήνουν να κοιμηθώ
            """;

        Assert.That(LanguageDetector.Detect(lyrics), Is.EqualTo("other"));
    }

    // ---- latin stopword profiles ----

    [TestCase("english", """
                         I know you never wanted me to say the words out loud
                         but all the time we had is gone and I am still here
                         and when the night comes down I will be waiting for you
                         just like I said that I would, all the way down
                         """)]
    [TestCase("french", """
                        Je ne sais pas si tu te souviens de moi
                        mais la nuit est encore plus belle avec toi
                        et tout ce que je voulais c'est un peu de temps
                        pour te dire que mon coeur est toujours dans le vent
                        """)]
    [TestCase("german", """
                        Ich weiss nicht mehr was du mir damals gesagt hast
                        aber die Nacht ist noch immer so hell wie ein Traum
                        und alles was ich will ist nur ein wenig Zeit mit dir
                        denn mein Herz schlaegt immer noch fuer dich allein
                        """)]
    [TestCase("spanish", """
                         No se si todavia te acuerdas de mi
                         pero la noche es mas bonita cuando estas aqui
                         y todo lo que quiero es un poco de tu tiempo
                         para decirte que mi corazon es tuyo
                         """)]
    [TestCase("italian", """
                         Non so se ti ricordi ancora di me
                         ma la notte e piu bella quando ci sei tu
                         e tutto quello che voglio e solo un po di tempo
                         per dirti che il mio cuore e sempre con te
                         """)]
    [TestCase("polish", """
                        Nie wiem czy jeszcze pamietasz o mnie
                        ale ta noc jest tak samo piekna jak wtedy
                        i wszystko co chce to tylko troche czasu
                        zeby powiedziec ci ze moje serce jest twoje
                        """)]
    [TestCase("swedish", """
                         Jag vet inte om du fortfarande minns mig
                         men natten ar lika vacker som den var da
                         och allt jag vill ha ar bara lite tid med dig
                         for att saga att mitt hjarta alltid ar ditt
                         """)]
    public void LatinScript_IsScoredByStopwords(string expected, string lyrics)
    {
        Assert.That(LanguageDetector.Detect(lyrics), Is.EqualTo(expected));
    }

    [Test]
    public void Diacritics_DoNotDerailTheirOwnLanguage()
    {
        // The same polish sample with its diacritics restored must still be polish (the marks are
        // a bonus, never a competing signal).
        const string lyrics =
            """
            Nie wiem czy jeszcze pamiętasz o mnie
            ale ta noc jest tak samo piękna jak wtedy
            i wszystko co chcę to tylko trochę czasu
            żeby powiedzieć ci że moje serce jest twoje
            """;

        Assert.That(LanguageDetector.Detect(lyrics), Is.EqualTo("polish"));
    }

    // ---- refusals ----

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   \n  \n ")]
    public void NoText_IsUnknown_NeverInstrumental(string? lyrics)
    {
        // Empty lyrics are ambiguous between "genuinely wordless" and "the pace backfill has not
        // reached this row", so the detector must not claim 'instrumental'; see LanguageBackfill.
        Assert.That(LanguageDetector.Detect(lyrics), Is.Null);
    }

    [Test]
    public void TooShort_IsUnknown()
    {
        Assert.That(LanguageDetector.Detect("la la la"), Is.Null);
    }

    [Test]
    public void Vocalise_IsUnknown()
    {
        // Latin letters, plenty of them, no function words at all: nothing to go on.
        Assert.That(LanguageDetector.Detect(
            "ooh ooh ooh aah aah ooh na na na na na ooh aah na na na ooh ooh aah aah na"), Is.Null);
    }

    [Test]
    public void RomanisedNonsense_IsUnknown_RatherThanAWildGuess()
    {
        Assert.That(LanguageDetector.Detect(
            "zxq vrbn kthl mwpf zxq vrbn kthl mwpf zxq vrbn kthl mwpf zxq vrbn kthl"), Is.Null);
    }

    // ---- contract with the storable vocabulary ----

    [Test]
    public void EveryDetectableLanguage_IsAStorableCanonicalName()
    {
        // The detector writes straight into beatmapsets.language, so it must be incapable of
        // producing a value outside the column's vocabulary.
        foreach (string language in LanguageDetector.DetectableLanguages)
            Assert.That(BeatmapLanguages.IsCanonical(language), Is.True, language);
    }

    [Test]
    public void Detection_IsDeterministic()
    {
        // A re-run at every boot must never churn a set from one language to another.
        const string lyrics =
            """
            I know you never wanted me to say the words out loud
            but all the time we had is gone and I am still here
            """;

        Assert.That(LanguageDetector.Detect(lyrics), Is.EqualTo(LanguageDetector.Detect(lyrics)));
    }
}
