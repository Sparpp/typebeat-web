using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages.Users;

/// <summary>People who follow this user (/users/{id}/followers). See <see cref="FollowListModel"/>.</summary>
public sealed class FollowersModel(Db db) : FollowListModel(db)
{
    protected override bool Incoming => true;

    public Task<IActionResult> OnGetAsync(long id) => LoadAsync(id);
}
