using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Typebeat.WireCompat;

/// <summary>
/// Runs one of the Node harnesses under <c>tests/Typebeat.Web.Tests/Js</c> against the SERVED
/// <c>wwwroot/js/typebeat-core.js</c> and parses its stdout as JSON.
///
/// <para>Node is optional on a dev box, so a missing node ignores the calling test rather than
/// failing it (CI has node), which is the rule every other JS guard in both test projects already
/// applies. <paramref name="extraArgs"/> is for harnesses that take their fixtures from the C#
/// side rather than carrying their own: the caller writes a temp file and passes its path.</para>
/// </summary>
public static class NodeHarness
{
    public static JsonElement Run(string harnessFileName, params string[] extraArgs)
    {
        string root = RepoRoot();
        string core = Path.Combine(root, "src", "Typebeat.Web", "wwwroot", "js", "typebeat-core.js");
        string harness = Path.Combine(root, "tests", "Typebeat.Web.Tests", "Js", harnessFileName);

        var psi = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        psi.ArgumentList.Add(harness);
        psi.ArgumentList.Add(core);

        foreach (string arg in extraArgs)
            psi.ArgumentList.Add(arg);

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

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Typebeat.Web", "wwwroot", "js", "typebeat-core.js")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new FileNotFoundException("could not locate the repo root");
    }
}
