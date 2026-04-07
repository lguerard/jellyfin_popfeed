using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Popfeed.Configuration;
using Jellyfin.Plugin.Popfeed.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Popfeed.Services;

/// <summary>
/// Maps Jellyfin playstate changes into Popfeed ATProto records.
/// </summary>
public sealed class PopfeedSyncService
{
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly PopfeedAtProtoClient _client;
    private readonly IPopfeedWatchStateWriter[] _watchStateWriters;
    private readonly PopfeedSyncStatusStore _statusStore;
    private readonly ILogger<PopfeedSyncService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PopfeedSyncService"/> class.
    /// </summary>
    /// <param name="client">The ATProto client.</param>
    /// <param name="logger">The logger.</param>
    public PopfeedSyncService(
        PopfeedAtProtoClient client,
        System.Collections.Generic.IEnumerable<IPopfeedWatchStateWriter> watchStateWriters,
        PopfeedSyncStatusStore statusStore,
        ILogger<PopfeedSyncService> logger)
    {
        _client = client;
        _watchStateWriters = watchStateWriters.ToArray();
        _statusStore = statusStore;
        _logger = logger;
    }

    /// <summary>
    /// Synchronizes a watched-state change.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="jellyfinUserId">The Jellyfin user id.</param>
    /// <param name="played">Whether the item is now marked watched.</param>
    /// <param name="playedAt">The watched timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SyncPlaystateAsync(Guid jellyfinUserId, BaseItem item, bool played, DateTimeOffset? playedAt, CancellationToken cancellationToken)
    {
        _ = await ExecuteSyncCoreAsync(jellyfinUserId, item, played, playedAt, true, "event", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs a dry-run or real sync test for the settings page.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user id.</param>
    /// <param name="item">The item to test.</param>
    /// <param name="played">Whether the item should be treated as watched.</param>
    /// <param name="dryRun">Whether remote writes should be skipped.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sync test result.</returns>
    public Task<PopfeedSyncTestResult> TestSyncAsync(Guid jellyfinUserId, BaseItem item, bool played, bool dryRun, CancellationToken cancellationToken)
    {
        return ExecuteSyncCoreAsync(jellyfinUserId, item, played, DateTimeOffset.UtcNow, !dryRun, "test", cancellationToken);
    }

    /// <summary>
    /// Gets recent sync activity for the settings page.
    /// </summary>
    /// <param name="jellyfinUserId">Optional user filter.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <returns>The recent sync entries.</returns>
    public PopfeedSyncTestResult[] GetRecentStatuses(Guid? jellyfinUserId, int limit)
    {
        return _statusStore.GetRecent(jellyfinUserId, limit);
    }

    private static bool ShouldSyncItem(BaseItem item, PluginConfiguration configuration)
    {
        return (item is Movie && configuration.SyncMovies)
            || (item is Episode && configuration.SyncEpisodes);
    }

    private static bool IsExcluded(BaseItem item, PluginConfiguration configuration)
    {
        var excludedIds = configuration.GetExcludedItemIds();
        if (excludedIds.Count == 0)
        {
            return false;
        }

        if (excludedIds.Contains(item.Id.ToString()))
        {
            return true;
        }

        return item is Episode episode
            && episode.Series is not null
            && excludedIds.Contains(episode.Series.Id.ToString());
    }

    private static PopfeedMappedItem? MapItem(BaseItem item)
    {
        if (item is Movie movie)
        {
            var identifiers = new PopfeedIdentifiers
            {
                ImdbId = GetProviderId(movie, MetadataProvider.Imdb),
                TmdbId = GetProviderId(movie, MetadataProvider.Tmdb),
            };

            return identifiers.IsEmpty
                ? null
                : new PopfeedMappedItem("movie", identifiers);
        }

        if (item is Episode episode && episode.Series is not null && episode.IndexNumber.HasValue)
        {
            var identifiers = new PopfeedIdentifiers
            {
                ImdbId = GetProviderId(episode, MetadataProvider.Imdb),
                TmdbId = GetProviderId(episode, MetadataProvider.Tmdb),
                TmdbTvSeriesId = GetProviderId(episode.Series, MetadataProvider.Tmdb),
                SeasonNumber = episode.ParentIndexNumber,
                EpisodeNumber = episode.IndexNumber,
            };

            var hasEpisodeShape = !string.IsNullOrWhiteSpace(identifiers.TmdbTvSeriesId) && identifiers.SeasonNumber.HasValue;
            var hasStandaloneId = !string.IsNullOrWhiteSpace(identifiers.ImdbId) || !string.IsNullOrWhiteSpace(identifiers.TmdbId);

            return (!hasEpisodeShape && !hasStandaloneId)
                ? null
                : new PopfeedMappedItem("tv_episode", identifiers);
        }

        return null;
    }

    private static string? GetProviderId(IHasProviderIds item, MetadataProvider provider)
    {
        return item.TryGetProviderId(provider, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private async Task<PopfeedSyncTestResult> ExecuteSyncCoreAsync(
        Guid jellyfinUserId,
        BaseItem item,
        bool played,
        DateTimeOffset? playedAt,
        bool executeRemote,
        string triggerSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var result = new PopfeedSyncTestResult
        {
            UserId = jellyfinUserId,
            ItemId = item.Id,
            ItemName = item.Name,
            Played = played,
        };

        var configuration = Plugin.Instance.Configuration;
        result.ProviderName = configuration.WatchStateProvider;

        if (!configuration.IsConfigured())
        {
            result.Message = "Plugin has no configured Popfeed users.";
            LogVerbose("Skipping {ItemName}: {Message}", item.Name, result.Message);
            return CompleteResult(result, triggerSource);
        }

        if (!ShouldSyncItem(item, configuration))
        {
            result.Message = "This media type is disabled by plugin settings.";
            LogVerbose("Skipping {ItemName}: {Message}", item.Name, result.Message);
            return CompleteResult(result, triggerSource);
        }

        if (IsExcluded(item, configuration))
        {
            result.Message = "This item is excluded from automatic Popfeed sync.";
            LogVerbose("Skipping {ItemName}: {Message}", item.Name, result.Message);
            return CompleteResult(result, triggerSource);
        }

        var userConfiguration = configuration.GetUserConfiguration(jellyfinUserId);
        if (userConfiguration is null || !userConfiguration.IsConfigured())
        {
            result.Message = "No Popfeed account mapping is configured for this Jellyfin user.";
            LogVerbose("Skipping {ItemName}: {Message}", item.Name, result.Message);
            return CompleteResult(result, triggerSource);
        }

        result.PopfeedIdentifier = userConfiguration.Identifier;

        var writer = _watchStateWriters.FirstOrDefault(candidate => string.Equals(candidate.ProviderName, configuration.WatchStateProvider, StringComparison.Ordinal));
        if (writer is null)
        {
            result.Message = $"No Popfeed watch-state writer is registered for provider '{configuration.WatchStateProvider}'.";
            _logger.LogWarning(result.Message);
            return CompleteResult(result, triggerSource);
        }

        var mapped = MapItem(item);
        if (mapped is null)
        {
            result.Message = "The item does not have enough Popfeed-compatible identifiers to sync.";
            LogVerbose("Skipping {ItemName}: {Message}", item.Name, result.Message);
            return CompleteResult(result, triggerSource);
        }

        result.CreativeWorkType = mapped.CreativeWorkType;
        result.Identifiers = mapped.Identifiers;
        result.WouldSync = true;

        if (!executeRemote)
        {
            result.Success = true;
            result.Executed = false;
            result.Message = "Dry run successful. The item would be sent to Popfeed with the current configuration.";
            LogVerbose("Dry run successful for {ItemName}. UserId={UserId}, Identifier={Identifier}", item.Name, jellyfinUserId, userConfiguration.Identifier);
            return CompleteResult(result, triggerSource);
        }

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                LogVerbose(
                    "Starting Popfeed sync for {ItemName}. UserId={UserId}, Played={Played}, Provider={Provider}, Identifier={Identifier}",
                    item.Name,
                    jellyfinUserId,
                    played,
                    configuration.WatchStateProvider,
                    userConfiguration.Identifier);

                var session = await _client.CreateSessionAsync(userConfiguration, cancellationToken).ConfigureAwait(false);
                await writer.SyncAsync(
                    userConfiguration,
                    session,
                    mapped,
                    item.Name,
                    played,
                    playedAt,
                    configuration.RemoveFromWatchedListWhenUnplayed,
                    cancellationToken).ConfigureAwait(false);

                result.Success = true;
                result.Executed = true;
                result.Message = "Remote sync completed successfully.";
                return CompleteResult(result, triggerSource);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed Popfeed sync for {ItemName} and user {UserId}.", item.Name, jellyfinUserId);
                result.Message = "Remote sync failed: " + ex.Message;
                return CompleteResult(result, triggerSource);
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private PopfeedSyncTestResult CompleteResult(PopfeedSyncTestResult result, string triggerSource)
    {
        result.TriggerSource = triggerSource;
        result.TimestampUtc = DateTimeOffset.UtcNow;
        _statusStore.Add(result);
        return result;
    }

    private void LogVerbose(string message, params object?[] arguments)
    {
        if (Plugin.Instance.Configuration.EnableDebugLogging)
        {
            _logger.LogInformation("[PopfeedDebug] " + message, arguments);
        }
        else
        {
            _logger.LogDebug(message, arguments);
        }
    }
}