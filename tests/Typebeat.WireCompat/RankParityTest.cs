using ClientProcessor = typebeat.Game.Rulesets.TypeBeat.Scoring.TypeBeatScoreProcessor;
using ClientRank = typebeat.Game.Scoring.ScoreRank;
using ServerContract = Typebeat.Web.Scoring.ScoringContract;

namespace Typebeat.WireCompat;

/// <summary>
/// Cross-repo pin for the GRADE rule (PR 17).
///
/// <para>
/// Grades moved from completion to timing accuracy with a missed-cell condition on SS and S. The
/// rule exists twice as C#: the game's <see cref="ClientProcessor"/> is what the client grades a
/// live run by, and the server's <see cref="ServerContract"/> is what grades a SUBMITTED score
/// (rank is recomputed server-side, never trusted from the client). A play graded S on the desktop
/// and A on the website would be the same lie the pp pin exists to prevent, so the two must be the
/// same arithmetic. This is the only project that compiles both repos, so this is where that is
/// provable.
/// </para>
///
/// <para>
/// Every point is checked on BOTH functions: the raw ladder (accuracy + missed fraction) and the
/// counts-derived entry point, whose missed fraction comes from each side's own note classifier.
/// The strings differ on one side only in type (the client returns <c>ScoreRank</c>, the server its
/// wire string), so the comparison is against the enum name.
/// </para>
/// </summary>
public class RankParityTest
{
    // Boundary and interior (accuracy, missedFraction) points: the missedFraction values straddle
    // the S miss limit (0.03) and zero, where the SS/S conditions bite.
    private static readonly (double Accuracy, double Missed)[] Ladder =
    {
        (1.00, 0.00), (0.98, 0.00), (0.979999, 0.00),
        (1.00, 0.001), (0.92, 0.029999), (0.92, 0.03), (0.99, 0.03),
        (0.919999, 0.00), (0.85, 0.15), (0.849999, 0.00),
        (0.75, 0.25), (0.749999, 0.00), (0.60, 0.40), (0.599999, 0.00),
        (0.00, 1.00), (1.00, 0.50), (0.95, 0.10),
    };

    // (accuracy, great/ok/meh/miss) - the counted shapes the two note classifiers must agree on.
    private static readonly (double Accuracy, int Great, int Ok, int Meh, int Miss)[] Counts =
    {
        (1.00, 100, 0, 0, 0),
        (0.99, 99, 0, 0, 1),
        (0.99, 96, 0, 0, 4),
        (0.80, 80, 0, 0, 0),
        (0.50, 50, 0, 0, 0),
        (0.90, 90, 5, 5, 10),
    };

    [Test]
    public void TheTwoGradeLaddersAgree()
    {
        foreach (var (accuracy, missed) in Ladder)
        {
            ClientRank client = ClientProcessor.RankFromAccuracy(accuracy, missed);
            string server = ServerContract.RankFromAccuracy(accuracy, missed);

            Assert.That(server, Is.EqualTo(client.ToString()),
                $"accuracy {accuracy}, missedFraction {missed}");
        }
    }

    [Test]
    public void TheTwoCountsDerivedGradesAgree()
    {
        foreach (var (accuracy, great, ok, meh, miss) in Counts)
        {
            var clientStats = new Dictionary<typebeat.Game.Rulesets.Scoring.HitResult, int>
            {
                [typebeat.Game.Rulesets.Scoring.HitResult.Great] = great,
                [typebeat.Game.Rulesets.Scoring.HitResult.Ok] = ok,
                [typebeat.Game.Rulesets.Scoring.HitResult.Meh] = meh,
                [typebeat.Game.Rulesets.Scoring.HitResult.Miss] = miss,
            };

            var serverStats = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["great"] = great,
                ["ok"] = ok,
                ["meh"] = meh,
                ["miss"] = miss,
            };

            ClientRank client = ClientProcessor.RankFromStatistics(accuracy, clientStats);
            string server = ServerContract.RankFromStatistics(accuracy, serverStats);

            Assert.That(server, Is.EqualTo(client.ToString()),
                $"accuracy {accuracy} over great={great} ok={ok} meh={meh} miss={miss}");
        }
    }
}
