using Typebeat.Web.Pages.Users;

namespace Typebeat.Web.Tests;

/// <summary>
/// The two rules that make the stored profile order (026_profile_order.sql) survive the section
/// set changing under it, and the validation that keeps garbage out of the column. No database:
/// this is the pure half of task 68, and the page-level half lives in Website/ProfileOrderTest.
/// </summary>
public class ProfileSectionsTest
{
    [Test]
    public void Resolve_NothingStored_IsTheDefaultOrder()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProfileSections.Resolve(null), Is.EqualTo(ProfileSections.Default));
            Assert.That(ProfileSections.Resolve([]), Is.EqualTo(ProfileSections.Default));
        });
    }

    [Test]
    public void Resolve_KeepsTheStoredOrder()
    {
        var stored = ProfileSections.Default.Reverse().ToList();
        Assert.That(ProfileSections.Resolve(stored), Is.EqualTo(stored));
    }

    [Test]
    public void Resolve_IgnoresUnknownIds()
    {
        // A section removed in some later version, and a hand-typed one.
        string[] stored = ["kudosu", ProfileSections.Maps, "'; DROP TABLE users; --"];

        var order = ProfileSections.Resolve(stored);

        Assert.Multiple(() =>
        {
            Assert.That(order[0], Is.EqualTo(ProfileSections.Maps));
            Assert.That(order, Is.EquivalentTo(ProfileSections.Default), "no unknown id may survive");
        });
    }

    [Test]
    public void Resolve_AppendsSectionsMissingFromTheStoredArray_InDefaultOrder()
    {
        // What a user who reordered BEFORE a section existed has stored: everything but one.
        var stored = ProfileSections.Default.Where(id => id != ProfileSections.PlayHistory).Reverse().ToList();

        var order = ProfileSections.Resolve(stored);

        Assert.Multiple(() =>
        {
            Assert.That(order.Take(stored.Count), Is.EqualTo(stored), "the stored order is untouched");
            Assert.That(order[^1], Is.EqualTo(ProfileSections.PlayHistory), "the new section lands after it");
            Assert.That(order, Is.EquivalentTo(ProfileSections.Default), "and every section is present exactly once");
        });
    }

    [Test]
    public void Resolve_AppendsSeveralMissingSections_InDefaultOrderAmongThemselves()
    {
        var order = ProfileSections.Resolve([ProfileSections.Favourites]);

        Assert.Multiple(() =>
        {
            Assert.That(order[0], Is.EqualTo(ProfileSections.Favourites));
            Assert.That(order.Skip(1), Is.EqualTo(ProfileSections.Default.Where(id => id != ProfileSections.Favourites)));
        });
    }

    [Test]
    public void Resolve_KeepsADuplicatesFirstPosition()
    {
        var order = ProfileSections.Resolve([ProfileSections.Maps, ProfileSections.BestScores, ProfileSections.Maps]);

        Assert.Multiple(() =>
        {
            Assert.That(order[0], Is.EqualTo(ProfileSections.Maps));
            Assert.That(order[1], Is.EqualTo(ProfileSections.BestScores));
            Assert.That(order, Is.Unique);
            Assert.That(order, Is.EquivalentTo(ProfileSections.Default));
        });
    }

    [Test]
    public void Sanitize_AcceptsAReorderedFullList()
    {
        var submitted = ProfileSections.Default.Reverse().ToList();

        Assert.That(ProfileSections.TrySanitize(submitted, out string[]? stored), Is.True);
        Assert.That(stored, Is.EqualTo(submitted));
    }

    [Test]
    public void Sanitize_StoresNullForTheDefaultOrder()
    {
        // Dragging everything back where it started must leave the user in the "never reordered"
        // state, so a section added later still lands in its designed slot (026_profile_order.sql).
        Assert.That(ProfileSections.TrySanitize(ProfileSections.Default.ToList(), out string[]? stored), Is.True);
        Assert.That(stored, Is.Null);
    }

    [Test]
    public void Sanitize_DeduplicatesRatherThanRefusing()
    {
        List<string?> submitted = [ProfileSections.Maps, ProfileSections.Maps, ProfileSections.BestScores];

        Assert.That(ProfileSections.TrySanitize(submitted, out string[]? stored), Is.True);
        Assert.That(stored, Is.EqualTo(new[] { ProfileSections.Maps, ProfileSections.BestScores }));
    }

    [Test]
    public void Sanitize_RefusesGarbage()
    {
        // One unknown id poisons the whole submission: it is not a list to clean up, it is a
        // request nothing on this site produces.
        Assert.Multiple(() =>
        {
            Assert.That(ProfileSections.TrySanitize([ProfileSections.Maps, "kudosu"], out _), Is.False);
            Assert.That(ProfileSections.TrySanitize([""], out _), Is.False);
            Assert.That(ProfileSections.TrySanitize([null], out _), Is.False);
            Assert.That(ProfileSections.TrySanitize(null, out _), Is.False, "nothing submitted");
            Assert.That(ProfileSections.TrySanitize([], out _), Is.False, "an empty layout is not a layout");
        });
    }

    [Test]
    public void Sanitize_RefusesMoreEntriesThanThereAreSections()
    {
        // Even all-known ids: a padded array is a request to grow the column without bound.
        var submitted = ProfileSections.Default.Concat([ProfileSections.Maps]).ToList();

        Assert.That(submitted, Has.Count.GreaterThan(ProfileSections.Default.Count));
        Assert.That(ProfileSections.TrySanitize(submitted, out _), Is.False);
    }

    [Test]
    public void Default_IdsAreUniqueAndAnchorSafe()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProfileSections.Default, Is.Unique);
            Assert.That(ProfileSections.Default, Is.All.Matches<string>(id =>
                id.Length > 0 && id.All(c => char.IsAsciiLetterLower(c) || c == '-')),
                "the ids are also the page's anchors and its data attributes");
        });
    }
}
