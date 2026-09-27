namespace Typebeat.Web.Social;

/// <summary>
/// Compact relative age ("3m", "5h", "2d"), the same glanceable shape osu-web's notification
/// list uses. Promoted out of <see cref="NotificationRowModel"/> when set comments arrived, so
/// the two row kinds cannot drift into printing the same moment two ways. Deliberately not the
/// profile's prose "n days ago": these rows are narrow and the timestamp is a suffix, not a
/// sentence.
/// </summary>
public static class RelativeAge
{
    public static string Label(DateTime createdAtUtc)
    {
        var elapsed = DateTime.UtcNow - createdAtUtc;

        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (elapsed < TimeSpan.FromMinutes(1))
            return "now";
        if (elapsed < TimeSpan.FromHours(1))
            return $"{(int)elapsed.TotalMinutes}m";
        if (elapsed < TimeSpan.FromDays(1))
            return $"{(int)elapsed.TotalHours}h";
        if (elapsed < TimeSpan.FromDays(30))
            return $"{(int)elapsed.TotalDays}d";
        if (elapsed < TimeSpan.FromDays(365))
            return $"{(int)(elapsed.TotalDays / 30)}mo";

        return $"{(int)(elapsed.TotalDays / 365)}y";
    }
}
