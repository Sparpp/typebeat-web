using Microsoft.Extensions.Configuration;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests;

/// <summary>
/// <see cref="Db.ResolveConnectionString"/>'s precedence: TYPEBEAT_DB, then
/// ConnectionStrings:typebeat, then the local dev default. Deployments set TYPEBEAT_DB in the
/// environment, which the host's configuration carries, so reading it from configuration is the
/// same lookup production has always had. Pure, no database.
/// </summary>
public class DbConnectionStringTest
{
    private const string deployed = "Host=postgres;Port=5432;Database=typebeat;Username=typebeat;Password=deployed";
    private const string from_appsettings = "Host=elsewhere;Port=5432;Database=typebeat;Username=typebeat;Password=appsettings";

    private static IConfiguration config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Test]
    public void TypebeatDb_OutranksTheConnectionStringsSection()
    {
        string resolved = Db.ResolveConnectionString(config(new Dictionary<string, string?>
        {
            ["TYPEBEAT_DB"] = deployed,
            ["ConnectionStrings:typebeat"] = from_appsettings,
        }));

        Assert.That(resolved, Is.EqualTo(deployed));
    }

    [Test]
    public void WithoutTypebeatDb_TheConnectionStringsSectionApplies()
    {
        string resolved = Db.ResolveConnectionString(config(new Dictionary<string, string?>
        {
            ["ConnectionStrings:typebeat"] = from_appsettings,
        }));

        Assert.That(resolved, Is.EqualTo(from_appsettings));
    }

    [Test]
    public void WithNeither_TheLocalDevDefaultApplies()
    {
        string resolved = Db.ResolveConnectionString(config(new Dictionary<string, string?>()));

        Assert.That(resolved, Does.StartWith("Host=localhost;Port=5432;Database=typebeat;"));
    }
}
