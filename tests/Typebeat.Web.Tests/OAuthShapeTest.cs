using Newtonsoft.Json;
using Typebeat.Web.Endpoints;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Tests;

/// <summary>
/// Pure wire-shape tests for the /oauth/token DTOs — no database. Guards the exact JSON the
/// client (typebeat.Game.Online.API.OAuthToken / OAuth.OAuthError) deserializes: property
/// names and declaration order. Serialized through <see cref="WireJson.Settings"/>, i.e. the
/// same Newtonsoft settings the live endpoint uses.
/// </summary>
public class OAuthShapeTest
{
    [Test]
    public void ErrorBody_SerializesToExactShapeAndOrder()
    {
        var body = new OAuthEndpoints.OAuthErrorBody
        {
            Error = "invalid_grant",
            Hint = "The username or password is incorrect.",
            Message = "The username or password is incorrect.",
        };

        string json = JsonConvert.SerializeObject(body, WireJson.Settings);

        // error → hint → message, matching OAuth.OAuthError's field order and [JsonProperty] names.
        Assert.That(json, Is.EqualTo(
            "{\"error\":\"invalid_grant\"," +
            "\"hint\":\"The username or password is incorrect.\"," +
            "\"message\":\"The username or password is incorrect.\"}"));
    }

    [Test]
    public void ErrorBody_PutsHumanTextInHint_SoClientSurfacesIt()
    {
        // OAuth.OAuthError.UserDisplayableError = !empty(Hint) ? Hint : Error, so the readable
        // message must live in "hint" — assert it is populated and distinct from the identifier.
        var body = new OAuthEndpoints.OAuthErrorBody
        {
            Error = "invalid_grant",
            Hint = "The username or password is incorrect.",
            Message = "The username or password is incorrect.",
        };

        Assert.Multiple(() =>
        {
            Assert.That(body.Hint, Is.Not.Empty);
            Assert.That(body.Hint, Is.Not.EqualTo(body.Error));
        });
    }

    [Test]
    public void TokenResponse_SerializesToExactShapeAndOrder()
    {
        var body = new OAuthEndpoints.TokenResponse
        {
            TokenType = "Bearer",
            ExpiresIn = 86_400,
            AccessToken = "acc",
            RefreshToken = "ref",
        };

        string json = JsonConvert.SerializeObject(body, WireJson.Settings);

        // token_type → expires_in → access_token → refresh_token; expires_in is a bare number
        // (OAuthToken.ExpiresIn is a long, must be > 30 for IsValid).
        Assert.That(json, Is.EqualTo(
            "{\"token_type\":\"Bearer\"," +
            "\"expires_in\":86400," +
            "\"access_token\":\"acc\"," +
            "\"refresh_token\":\"ref\"}"));
    }
}
