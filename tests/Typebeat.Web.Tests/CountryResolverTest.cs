using Typebeat.Web.Auth;

namespace Typebeat.Web.Tests;

/// <summary>
/// <see cref="CountryResolver"/>'s pure rule (backlog 333): Cloudflare's CF-IPCountry is honoured
/// only behind the proxy, and only when it names a country the game client's enum knows. The
/// host-driven half (all three account creation paths and the backfill) is
/// <c>Website/CountryTest</c>; the enum mirror itself is pinned in WireCompat.
/// </summary>
public class CountryResolverTest
{
    [TestCase("US", "US")]
    [TestCase("JP", "JP")]
    [TestCase("gb", "GB")]
    [TestCase(" de ", "DE")]
    public void BehindTheProxy_AKnownCountryIsAccepted(string header, string expected)
        => Assert.That(CountryResolver.Resolve(behindProxy: true, header), Is.EqualTo(expected));

    [TestCase("US")]
    [TestCase("JP")]
    [TestCase("T1")]
    public void OnADirectConnection_TheHeaderIsNeverTrusted(string header)
        => Assert.That(CountryResolver.Resolve(behindProxy: false, header), Is.EqualTo(Countries.Unknown));

    // Cloudflare's own non-country answers: XX (could not tell) and T1 (Tor).
    [TestCase("XX")]
    [TestCase("T1")]
    public void CloudflaresNonCountryValues_AreStoredAsUnknown(string header)
        => Assert.That(CountryResolver.Resolve(behindProxy: true, header), Is.EqualTo(Countries.Unknown));

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("U")]
    [TestCase("USA")]
    [TestCase("U1")]
    [TestCase("1A")]
    [TestCase("U$")]
    [TestCase("US,JP")]
    public void MalformedValues_AreRejected(string? header)
        => Assert.That(CountryResolver.Resolve(behindProxy: true, header), Is.EqualTo(Countries.Unknown));

    // Well-formed but not a country the client's enum can show: an unassigned code, the old GeoIP
    // pseudo-codes the enum still carries (A1, A2, AP, O1), its non-country regions (EU, AN, FX),
    // and South Sudan, which the enum does not have yet.
    [TestCase("ZZ")]
    [TestCase("A1")]
    [TestCase("A2")]
    [TestCase("AP")]
    [TestCase("O1")]
    [TestCase("EU")]
    [TestCase("AN")]
    [TestCase("FX")]
    [TestCase("SS")]
    public void UnknownCodes_AreRejected(string header)
        => Assert.That(CountryResolver.Resolve(behindProxy: true, header), Is.EqualTo(Countries.Unknown));

    [Test]
    public void EveryStorableCountry_HasAFlagImage_AndAName()
    {
        string flags = Path.Combine(JsHarness.RepoRoot(), "src", "Typebeat.Web", "wwwroot", "flags");

        var missing = Countries.Codes.Where(c => !File.Exists(Path.Combine(flags, c + ".png"))).ToList();
        var unnamed = Countries.Codes.Where(c => string.IsNullOrWhiteSpace(Countries.NameOf(c))).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(Countries.Codes.Count(), Is.EqualTo(249));
            Assert.That(missing, Is.Empty, "a storable country with no flag would render a broken image");
            Assert.That(unnamed, Is.Empty);
            Assert.That(Countries.IsCountry(Countries.Unknown), Is.False, "XX is the absence of a country, never one");
            Assert.That(Countries.NameOf(Countries.Unknown), Is.Null);
        });
    }
}
