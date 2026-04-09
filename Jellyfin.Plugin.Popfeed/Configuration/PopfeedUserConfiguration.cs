using System;

namespace Jellyfin.Plugin.Popfeed.Configuration;

/// <summary>
/// Per-user Popfeed account mapping.
/// </summary>
public sealed class PopfeedUserConfiguration
{
    /// <summary>
    /// Gets or sets the Jellyfin user id.
    /// </summary>
    public Guid JellyfinUserId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this mapping is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the ATProto PDS base URL.
    /// </summary>
    public string PdsUrl { get; set; } = "https://bsky.social";

    /// <summary>
    /// Gets or sets the ATProto identifier.
    /// </summary>
    public string Identifier { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ATProto app password.
    /// </summary>
    public string AppPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Popfeed list name used to model watched state.
    /// </summary>
    public string WatchedListName { get; set; } = "Watched";

    /// <summary>
    /// Gets or sets the cached watched list URI.
    /// </summary>
    public string WatchedListUri { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether watched items should also be posted as activity to Bluesky.
    /// </summary>
    public bool PostWatchedItemsToBluesky { get; set; }

    /// <summary>
    /// Gets or sets the preferred language code for generated Bluesky posts.
    /// </summary>
    public string BlueskyPostLanguage { get; set; } = "en";

    /// <summary>
    /// Determines whether the mapping has enough information to sync.
    /// </summary>
    /// <returns><see langword="true"/> when the mapping is valid.</returns>
    public bool IsConfigured()
    {
        return Enabled
            && JellyfinUserId != Guid.Empty
            && !string.IsNullOrWhiteSpace(PdsUrl)
            && !string.IsNullOrWhiteSpace(Identifier)
            && !string.IsNullOrWhiteSpace(AppPassword);
    }
}