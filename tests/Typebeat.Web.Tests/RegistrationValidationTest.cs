using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Endpoints;

namespace Typebeat.Web.Tests;

/// <summary>
/// Covers the DB-free registration validation helpers and the form_error wire shape the client
/// deserializes (RegistrationRequest.RegistrationRequestErrors via SelectToken("form_error")).
/// </summary>
public class RegistrationValidationTest
{
    // ---- Username ----

    [TestCase("ab")]                 // too short (2)
    [TestCase("")]                   // empty
    [TestCase("thisusernameistoolong")] // too long (>15)
    [TestCase("bad!name")]           // illegal char
    [TestCase("emoji😀")]            // illegal char
    public void Username_Invalid(string username)
        => Assert.That(RegistrationEndpoints.ValidateUsername(username), Is.Not.Empty);

    [TestCase("abc")]                // min length 3
    [TestCase("Player One")]         // space allowed
    [TestCase("cool_guy-99")]        // _ - digits
    [TestCase("[typebeat]")]         // brackets
    [TestCase("fifteencharname")]    // exactly 15
    public void Username_Valid(string username)
        => Assert.That(RegistrationEndpoints.ValidateUsername(username), Is.Empty);

    [Test]
    public void Username_LengthBoundaries()
    {
        Assert.That(RegistrationEndpoints.ValidateUsername(new string('a', 3)), Is.Empty);
        Assert.That(RegistrationEndpoints.ValidateUsername(new string('a', 15)), Is.Empty);
        Assert.That(RegistrationEndpoints.ValidateUsername(new string('a', 16)), Is.Not.Empty);
    }

    // ---- Email ----

    [TestCase("")]
    [TestCase("notanemail")]
    [TestCase("no@domain")]          // domain has no dot
    [TestCase("two@@at.com")]
    [TestCase("has space@x.com")]
    [TestCase("@nolocal.com")]
    [TestCase("trailingdot@x.")]
    public void Email_Invalid(string email)
        => Assert.That(RegistrationEndpoints.ValidateEmail(email), Is.Not.Empty);

    [TestCase("a@b.com")]
    [TestCase("first.last@sub.example.co.uk")]
    [TestCase("player+tag@type.beat")]
    public void Email_Valid(string email)
        => Assert.That(RegistrationEndpoints.ValidateEmail(email), Is.Empty);

    // ---- Password ----

    [TestCase("", "player")]
    [TestCase("short7!", "player")]      // 7 chars
    public void Password_Invalid(string password, string username)
        => Assert.That(RegistrationEndpoints.ValidatePassword(password, username), Is.Not.Empty);

    [Test]
    public void Password_EqualToUsername_IsRejected_CaseInsensitively()
    {
        Assert.That(RegistrationEndpoints.ValidatePassword("Player12", "player12"), Is.Not.Empty);
        Assert.That(RegistrationEndpoints.ValidatePassword("player12", "player12"), Is.Not.Empty);
    }

    [TestCase("longenough", "player")]
    [TestCase("8charsss", "player")]     // exactly 8
    public void Password_Valid(string password, string username)
        => Assert.That(RegistrationEndpoints.ValidatePassword(password, username), Is.Empty);

    // ---- form_error wire shape ----

    [Test]
    public void FormError_MatchesClientContract()
    {
        object envelope = RegistrationEndpoints.BuildFormError(
            usernameErrors: new[] { "Username is already taken." },
            emailErrors: new[] { "Please enter a valid email address." },
            passwordErrors: new[] { "Password must be at least 8 characters." });

        var root = JObject.Parse(JsonConvert.SerializeObject(envelope));

        // The client does SelectToken("form_error", errorWhenNoMatch: true) then .ToObject<...>().
        var user = root.SelectToken("form_error")?.SelectToken("user") as JObject;
        Assert.That(user, Is.Not.Null, "form_error.user must exist");

        // Field names must be exactly username / user_email / password, each a string array.
        Assert.Multiple(() =>
        {
            Assert.That(user!["username"]!.Type, Is.EqualTo(JTokenType.Array));
            Assert.That(user["user_email"]!.Type, Is.EqualTo(JTokenType.Array));
            Assert.That(user["password"]!.Type, Is.EqualTo(JTokenType.Array));

            Assert.That(user["username"]!.ToObject<string[]>(), Is.EqualTo(new[] { "Username is already taken." }));
            Assert.That(user["user_email"]!.ToObject<string[]>(), Is.EqualTo(new[] { "Please enter a valid email address." }));
            Assert.That(user["password"]!.ToObject<string[]>(), Is.EqualTo(new[] { "Password must be at least 8 characters." }));
        });
    }

    [Test]
    public void FormError_EmptyLists_SerializeAsEmptyArrays_NotNull()
    {
        object envelope = RegistrationEndpoints.BuildFormError(
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        var user = JObject.Parse(JsonConvert.SerializeObject(envelope)).SelectToken("form_error.user") as JObject;

        Assert.That(user, Is.Not.Null);
        Assert.Multiple(() =>
        {
            // Client's UserErrors fields default to Array.Empty and are indexed unconditionally,
            // so absent fields must still serialize as [] rather than null.
            Assert.That(user!["username"]!.Type, Is.EqualTo(JTokenType.Array));
            Assert.That(user["username"]!.HasValues, Is.False);
            Assert.That(user["user_email"]!.Type, Is.EqualTo(JTokenType.Array));
            Assert.That(user["password"]!.Type, Is.EqualTo(JTokenType.Array));
        });
    }
}
