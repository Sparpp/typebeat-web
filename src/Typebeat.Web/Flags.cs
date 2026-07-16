namespace Typebeat.Web;

/// <summary>
/// Tolerant boolean flag reading. Compose files pass unset flags through as EMPTY STRINGS
/// (<c>VAR: "${VAR:-}"</c>), and <c>IConfiguration.GetValue&lt;bool&gt;</c> THROWS on an empty
/// string rather than defaulting — which once crash-looped production. Anything that isn't a
/// parseable "true" counts as disabled.
/// </summary>
public static class Flags
{
    public static bool IsEnabled(IConfiguration config, string key)
        => bool.TryParse(config[key], out bool value) && value;
}
