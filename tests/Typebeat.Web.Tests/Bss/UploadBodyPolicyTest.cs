using Typebeat.Web.Endpoints;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// Pins the per-endpoint Kestrel body policy on the two package-upload routes. Pure constant
/// checks: the features these values are written into are Kestrel's and absent under TestServer,
/// so the wiring itself is only exercised in production. What CAN be pinned is the policy, so a
/// drift in either number is a deliberate, visible edit.
/// </summary>
[TestFixture]
public class UploadBodyPolicyTest
{
    [Test]
    public void UploadBodyCapIsOneHundredMegabytes()
        => Assert.That(BssEndpoints.MaxUploadBodyBytes, Is.EqualTo(100L * 1024 * 1024));

    /// <summary>
    /// The floor exists to admit trickle-rate uploads that Kestrel's default (240 bytes/s after
    /// a 5 s grace period) aborts mid-read: a throttled user's PATCH arrives at the direct-origin
    /// host but the body pays out slower than the default floor, and the abort surfaces
    /// client-side as a generic transport error that the upload retry then burns its attempts on.
    /// 30 bytes/s with a 30 s grace still finishes a ~37 KB patch delta inside the client's
    /// 600 s request timeout at sub-kilobyte rates, while keeping a nonzero slowloris backstop.
    /// </summary>
    [Test]
    public void UploadMinBodyDataRateIsWellBelowKestrelsDefault()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BssEndpoints.UploadMinBodyDataRate.BytesPerSecond, Is.EqualTo(30).Within(1e-9));
            Assert.That(BssEndpoints.UploadMinBodyDataRate.GracePeriod, Is.EqualTo(TimeSpan.FromSeconds(30)));

            // Kestrel's defaults, restated so the relation this floor must keep is explicit.
            Assert.That(BssEndpoints.UploadMinBodyDataRate.BytesPerSecond, Is.LessThan(240));
            Assert.That(BssEndpoints.UploadMinBodyDataRate.GracePeriod, Is.GreaterThan(TimeSpan.FromSeconds(5)));
        });
    }
}
