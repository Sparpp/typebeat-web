namespace Typebeat.Web.Packages;

/// <summary>
/// A beatmap package broke an upload invariant. The message is user-facing: BSS endpoints map
/// this 1:1 onto the 422 <c>{"error": "..."}</c> envelope the client displays verbatim.
/// </summary>
public sealed class PackageValidationException(string message) : Exception(message);
