using System.Diagnostics;
using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for the browser scoring core's syllable-aware per-char timing. The hand-written
/// wwwroot/js/typebeat-core.js must reproduce the desktop game's TypingLine.syllableCharTarget
/// bit-for-bit, or /play scores on subdivided maps diverge from desktop and corrupt the shared
/// leaderboards. Golden values mirror typebeat-osu's TypingEngineTest syllable region:
///   "abcd" over [1000,2000] with a boundary at 1200 -> 1000/1100/1200/1600;
///   "abcdef" over [0,1200] with boundaries 300,900 -> 0/150/300/600/900/1050 (2 chars/segment);
///   an undivided word stays the flat ramp 1000/1250/1500/1750.
/// A Node harness loads the actual shipped JS and emits the computed targets; this asserts them.
/// </summary>
public class SyllableTimingParityTest
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Typebeat.Web", "wwwroot", "js", "typebeat-core.js")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("could not locate repo root (src/Typebeat.Web/wwwroot/js/typebeat-core.js)");
    }

    private static JsonElement RunHarness()
    {
        string root = RepoRoot();
        string corePath = Path.Combine(root, "src", "Typebeat.Web", "wwwroot", "js", "typebeat-core.js");
        string harness = Path.Combine(root, "tests", "Typebeat.Web.Tests", "Js", "CoreSyllableHarness.cjs");

        var psi = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(harness);
        psi.ArgumentList.Add(corePath);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            Assert.Ignore($"node is not available to run the JS fidelity harness: {ex.Message}");
            throw; // unreachable
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.That(process.ExitCode, Is.EqualTo(0), $"harness failed: {stderr}");
        return JsonDocument.Parse(stdout).RootElement;
    }

    private static double[] Arr(JsonElement root, string key)
    {
        var list = new List<double>();
        foreach (var e in root.GetProperty(key).EnumerateArray())
            list.Add(e.GetDouble());
        return list.ToArray();
    }

    [Test]
    public void JsSyllableTargetsMatchGameGoldenValues()
    {
        var root = RunHarness();

        Assert.Multiple(() =>
        {
            // One boundary, off-centre: non-uniform ramp (caret slows in the longer 2nd syllable).
            Assert.That(Arr(root, "divided"), Is.EqualTo(new[] { 1000d, 1100d, 1200d, 1600d }));
            // No boundary: exact flat interpolation, unchanged for every existing map.
            Assert.That(Arr(root, "flat"), Is.EqualTo(new[] { 1000d, 1250d, 1500d, 1750d }));
            // Two boundaries, uneven segment durations 300/600/300; 2 chars per segment, monotonic.
            Assert.That(Arr(root, "multi"), Is.EqualTo(new[] { 0d, 150d, 300d, 600d, 900d, 1050d }));
        });

        // Monotonic non-decreasing across the whole subdivided word.
        var multi = Arr(root, "multi");
        for (int i = 1; i < multi.Length; i++)
            Assert.That(multi[i], Is.GreaterThanOrEqualTo(multi[i - 1]));
    }

    [Test]
    public void JsBuildBeatmapDecodesSyllablesFromOsuJson()
    {
        var root = RunHarness();

        Assert.Multiple(() =>
        {
            // Proves words[].syllables[] in the served .osu decodes into boundaries and warps the
            // cells (not just the direct helper) — the field flows through parseLyricOsu/buildBeatmap.
            Assert.That(Arr(root, "dividedCells"), Is.EqualTo(new[] { 1000d, 1100d, 1200d, 1600d }));
            // A word with no syllables[] stays the flat ramp end-to-end.
            Assert.That(Arr(root, "flatCells"), Is.EqualTo(new[] { 1000d, 1250d, 1500d, 1750d }));
        });
    }
}
