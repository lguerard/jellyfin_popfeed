using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Popfeed.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        SyncMovies = true;
        SyncEpisodes = true;
        RemoveFromWatchedListWhenUnplayed = true;
        WatchStateProvider = PopfeedWatchedListProviderName;
        ExcludedItemIds = [];
        PopfeedUsers = [];
    }

    /// <summary>
    /// The current watched-list strategy provider name.
    /// </summary>
    public const string PopfeedWatchedListProviderName = "popfeed-watched-list";

    /// <summary>
    /// Gets or sets Jellyfin item or series ids that should never sync to Popfeed.
    /// Movies can be excluded by their own id. Episodes can be excluded either by their own id or by their parent series id.
    /// </summary>
    public string[] ExcludedItemIds { get; set; }

    /// <summary>
    /// Gets or sets the configured Popfeed user mappings.
    /// </summary>
    public PopfeedUserConfiguration[] PopfeedUsers { get; set; }

    /// <summary>
    /// Gets or sets the watched-state provider strategy.
    /// </summary>
    public string WatchStateProvider { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether movie events should sync.
    /// </summary>
    public bool SyncMovies { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether episode events should sync.
    /// </summary>
    public bool SyncEpisodes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether list items should be removed when a title is marked unwatched.
    /// </summary>
    public bool RemoveFromWatchedListWhenUnplayed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether verbose request logging is enabled.
    /// </summary>
    public bool EnableDebugLogging { get; set; }

    /// <summary>
    /// Gets the configured excluded item ids as a normalized set.
    /// </summary>
    /// <returns>The excluded ids.</returns>
    public IReadOnlySet<string> GetExcludedItemIds()
    {
        return ExcludedItemIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the configured Popfeed mapping for a Jellyfin user.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user id.</param>
    /// <returns>The configured mapping, or <see langword="null"/>.</returns>
    public PopfeedUserConfiguration? GetUserConfiguration(Guid jellyfinUserId)
    {
        return PopfeedUsers.FirstOrDefault(user => user.JellyfinUserId == jellyfinUserId && user.Enabled);
    }

    /// <summary>
    /// Determines whether any user mappings are configured.
    /// </summary>
    /// <returns><see langword="true"/> when configuration is present.</returns>
    public bool IsConfigured()
    {
        return PopfeedUsers.Any(user => user.IsConfigured());
    }
}