using System.Diagnostics;
using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// Shared plumbing for the JS-vs-C# fidelity guards. Each guard ships a small Node harness under
/// tests/Typebeat.Web.Tests/Js that loads the ACTUAL served wwwroot/js/typebeat-core.js and prints
/// its observations as JSON; the C# test then asserts them against golden values mirrored from the
/// game's own test fixtures. Node is optional on a dev box, so a missing node ignores the test
/// rather than failing it (CI has node).
/// </summary>
public static class JsHarness
{
    public const string CorePath = "src/Typebeat.Web/wwwroot/js/typebeat-core.js";

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Typebeat.Web", "wwwroot", "js", "typebeat-core.js")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"could not locate repo root ({CorePath})");
    }

    /// <summary>
    /// Runs tests/Typebeat.Web.Tests/Js/<paramref name="harnessFileName"/> under node with the
    /// served core script's path as its only argument, and parses its stdout as JSON.
    /// </summary>
    public static JsonElement Run(string harnessFileName)
    {
        string root = RepoRoot();
        string corePath = Path.Combine(root, "src", "Typebeat.Web", "wwwroot", "js", "typebeat-core.js");
        string harness = Path.Combine(root, "tests", "Typebeat.Web.Tests", "Js", harnessFileName);

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

    public static double[] Doubles(JsonElement root, string key)
    {
        var list = new List<double>();
        foreach (var e in root.GetProperty(key).EnumerateArray())
            list.Add(e.GetDouble());
        return list.ToArray();
    }
}
