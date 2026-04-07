namespace Jellyfin.Plugin.Popfeed.Models;

/// <summary>
/// A Jellyfin item mapped into Popfeed identifier space.
/// </summary>
/// <param name="CreativeWorkType">The Popfeed creative work type.</param>
/// <param name="Identifiers">The Popfeed identifiers.</param>
public sealed record PopfeedMappedItem(string CreativeWorkType, PopfeedIdentifiers Identifiers);