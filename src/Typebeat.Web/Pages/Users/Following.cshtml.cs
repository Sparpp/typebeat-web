using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages.Users;

/// <summary>People this user follows (/users/{id}/following). See <see cref="FollowListModel"/>.</summary>
public sealed class FollowingModel(Db db) : FollowListModel(db)
{
    protected override bool Incoming => false;

    public Task<IActionResult> OnGetAsync(long id) => LoadAsync(id);
}
