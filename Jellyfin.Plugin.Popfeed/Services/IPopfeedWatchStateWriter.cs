using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Popfeed.Configuration;
using Jellyfin.Plugin.Popfeed.Models;

namespace Jellyfin.Plugin.Popfeed.Services;

/// <summary>
/// Writes Jellyfin playstate into a specific Popfeed representation.
/// </summary>
public interface IPopfeedWatchStateWriter
{
    /// <summary>
    /// Gets the provider name.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Synchronizes watched-state changes for a mapped item.
    /// </summary>
    /// <param name="userConfiguration">The Popfeed user mapping.</param>
    /// <param name="session">The authenticated ATProto session.</param>
    /// <param name="mappedItem">The mapped Popfeed item.</param>
    /// <param name="title">The media title.</param>
    /// <param name="played">Whether the item is watched.</param>
    /// <param name="playedAt">The watched timestamp.</param>
    /// <param name="removeWhenUnplayed">Whether to delete the Popfeed record when marking unwatched.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SyncAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        PopfeedMappedItem mappedItem,
        string title,
        bool played,
        DateTimeOffset? playedAt,
        bool removeWhenUnplayed,
        CancellationToken cancellationToken);
}