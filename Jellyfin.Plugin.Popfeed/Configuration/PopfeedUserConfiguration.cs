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
    /// Gets or sets the cached movie watched-list URI.
    /// </summary>
    public string WatchedMovieListUri { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cached TV watched-list URI.
    /// </summary>
    public string WatchedTelevisionListUri { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether watched items should also be posted as activity to Bluesky.
    /// </summary>
    public bool PostWatchedItemsToBluesky { get; set; }

    /// <summary>
    /// Gets or sets the preferred language code for generated Bluesky posts.
    /// </summary>
    public string BlueskyPostLanguage { get; set; } = "en";

    /// <summary>
    /// Gets or sets the Bluesky post mode for watched items.
    /// </summary>
    /// <remarks>
    /// "episode" posts each watched episode individually.
    /// "season" posts only when a full season is finished.
    /// </remarks>
    public string BlueskyPostMode { get; set; } = "episode";

    /// <summary>
    /// Gets the cached watched-list URI for the target Popfeed creative work type.
    /// </summary>
    /// <param name="creativeWorkType">The Popfeed creative work type.</param>
    /// <returns>The cached watched-list URI when available.</returns>
    public string GetWatchedListUri(string creativeWorkType)
    {
        return creativeWorkType switch
        {
            "movie" => WatchedMovieListUri,
            "tv_episode" or "tv_season" or "tv_show" => WatchedTelevisionListUri,
            _ => WatchedListUri,
        };
    }

    /// <summary>
    /// Stores the cached watched-list URI for the target Popfeed creative work type.
    /// </summary>
    /// <param name="creativeWorkType">The Popfeed creative work type.</param>
    /// <param name="uri">The watched-list URI.</param>
    public void SetWatchedListUri(string creativeWorkType, string uri)
    {
        switch (creativeWorkType)
        {
            case "movie":
                WatchedMovieListUri = uri;
                break;
            case "tv_episode":
            case "tv_season":
            case "tv_show":
                WatchedTelevisionListUri = uri;
                break;
            default:
                WatchedListUri = uri;
                break;
        }
    }

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