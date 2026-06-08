using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
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
/// Stores watched state as native Popfeed activity records using deterministic
/// ATProto record keys.  Every write is an upsert (putRecord), so syncing the
/// same item twice is idempotent and no list scan is needed to locate existing
/// records.
/// </summary>
public sealed class PopfeedWatchedListWriter : IPopfeedWatchStateWriter
{
    private const string ListCollection = "social.popfeed.feed.list";
    private const string ListItemCollection = "social.popfeed.feed.listItem";
    private const string ReviewCollection = "social.popfeed.feed.review";
    private readonly PopfeedAtProtoClient _client;
    private readonly ILogger<PopfeedWatchedListWriter> _logger;

    private readonly record struct EpisodeCoordinate(int SeasonNumber, int EpisodeNumber);

    /// <summary>
    /// Initializes a new instance of the <see cref="PopfeedWatchedListWriter"/> class.
    /// </summary>
    public PopfeedWatchedListWriter(PopfeedAtProtoClient client, ILogger<PopfeedWatchedListWriter> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => PluginConfiguration.PopfeedWatchedListProviderName;

    /// <inheritdoc />
    public async Task<PopfeedActivityWriteResult?> SyncAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        PopfeedMappedItem mappedItem,
        string title,
        string activityText,
        BaseItem item,
        bool played,
        bool inProgress,
        DateTimeOffset? playedAt,
        bool removeWhenUnplayed,
        CancellationToken cancellationToken)
    {
        mappedItem = PopfeedItemUrlBuilder.NormalizeMappedItem(mappedItem, item);
        LogVerbose("Using native Popfeed watched-list strategy for {ItemName}.", title);

        var watchedListUri = await EnsureWatchedListAsync(userConfiguration, session, mappedItem.CreativeWorkType, cancellationToken).ConfigureAwait(false);
        string? recentListUri = null;
        if (userConfiguration.CreateRecentList)
        {
            try
            {
                recentListUri = await EnsureRecentListAsync(userConfiguration, session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve Recent list for {ItemName}; Recent list sync skipped.", title);
            }
        }

        if (played || inProgress)
        {
            return await UpsertWatchedItemAsync(
                userConfiguration, session, mappedItem, title, activityText,
                item, played, inProgress, playedAt, watchedListUri, recentListUri, cancellationToken).ConfigureAwait(false);
        }

        if (removeWhenUnplayed)
        {
            await RemoveWatchedItemAsync(
                userConfiguration, session, mappedItem, item,
                watchedListUri, recentListUri, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Upsert path
    // -------------------------------------------------------------------------

    private async Task<PopfeedActivityWriteResult> UpsertWatchedItemAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        PopfeedMappedItem mappedItem,
        string title,
        string activityText,
        BaseItem item,
        bool played,
        bool inProgress,
        DateTimeOffset? playedAt,
        string watchedListUri,
        string? recentListUri,
        CancellationToken cancellationToken)
    {
        var timestamp = playedAt ?? DateTimeOffset.UtcNow;
        var desiredStatus = played ? PopfeedListItemRecord.FinishedStatus : PopfeedListItemRecord.InProgressStatus;
        var watchedListType = GetWatchedListType(mappedItem.CreativeWorkType);

        var watchedRkey = PopfeedRkeyBuilder.ForWatchedItem(mappedItem);
        var reviewRkey = PopfeedRkeyBuilder.ForReview(mappedItem);

        // Fetch existing records by deterministic rkey — O(1), no pagination.
        // The two lookups are independent, so issue them concurrently.
        var existingWatchedTask = _client.GetRecordAsync<PopfeedListItemRecord>(
            userConfiguration.PdsUrl, session, ListItemCollection, watchedRkey, cancellationToken);
        var existingReviewTask = _client.GetRecordAsync<PopfeedReviewRecord>(
            userConfiguration.PdsUrl, session, ReviewCollection, reviewRkey, cancellationToken);
        await Task.WhenAll(existingWatchedTask, existingReviewTask).ConfigureAwait(false);
        var existingWatched = existingWatchedTask.Result;
        var existingReview = existingReviewTask.Result;

        // Build and upsert the watched-list entry.
        var watchedRecord = new PopfeedListItemRecord
        {
            Identifiers = mappedItem.Identifiers,
            CreativeWorkType = mappedItem.CreativeWorkType,
            ListUri = watchedListUri,
            ListType = watchedListType,
            Status = desiredStatus,
            AddedAt = !string.IsNullOrWhiteSpace(existingWatched?.Value.AddedAt)
                ? existingWatched.Value.AddedAt
                : ToAtProtoDateTime(timestamp.UtcDateTime),
            CompletedAt = played ? ToAtProtoDateTime(timestamp.UtcDateTime) : null,
            Title = title,
        };
        await _client.PutRecordAsync(userConfiguration.PdsUrl, session, ListItemCollection, watchedRkey, watchedRecord, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Synced watched list item for {ItemName} on account {Identifier}.", title, userConfiguration.Identifier);

        // Update tv_show progress and season markers for episodes.
        if (mappedItem.Identifiers.TmdbTvSeriesId is { Length: > 0 } seriesId
            && mappedItem.Identifiers.SeasonNumber.HasValue
            && mappedItem.Identifiers.EpisodeNumber.HasValue
            && item is Episode episode)
        {
            await UpsertTvShowProgressAsync(
                userConfiguration, session, watchedListUri, watchedListType,
                seriesId, mappedItem, episode, timestamp, cancellationToken).ConfigureAwait(false);
        }

        // Upsert recent-list entry (played items only).
        if (recentListUri is not null && played)
        {
            var recentRkey = PopfeedRkeyBuilder.ForRecentItem(mappedItem);
            var existingRecent = await _client.GetRecordAsync<PopfeedListItemRecord>(
                userConfiguration.PdsUrl, session, ListItemCollection, recentRkey, cancellationToken).ConfigureAwait(false);

            var recentRecord = new PopfeedListItemRecord
            {
                Identifiers = mappedItem.Identifiers,
                CreativeWorkType = mappedItem.CreativeWorkType,
                ListUri = recentListUri,
                Status = PopfeedListItemRecord.FinishedStatus,
                AddedAt = !string.IsNullOrWhiteSpace(existingRecent?.Value.AddedAt)
                    ? existingRecent.Value.AddedAt
                    : ToAtProtoDateTime(timestamp.UtcDateTime),
                CompletedAt = ToAtProtoDateTime(timestamp.UtcDateTime),
                Title = title,
            };
            await _client.PutRecordAsync(userConfiguration.PdsUrl, session, ListItemCollection, recentRkey, recentRecord, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Synced recent list item for {ItemName} on account {Identifier}.", title, userConfiguration.Identifier);
        }

        // Build and upsert the review record.  Reuse the already-uploaded poster
        // blob from the existing review so re-syncs skip the blob upload entirely.
        var desiredReview = await BuildReviewRecordAsync(
            session, userConfiguration, mappedItem, title, activityText, item, playedAt, existingReview?.Value, cancellationToken).ConfigureAwait(false);
        var reviewRecord = existingReview is not null
            ? MergeExistingReview(existingReview.Value, desiredReview)
            : desiredReview;
        var reviewResult = await _client.PutRecordAsync(
            userConfiguration.PdsUrl, session, ReviewCollection, reviewRkey, reviewRecord, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Synced review for {ItemName} on account {Identifier}.", title, userConfiguration.Identifier);

        return new PopfeedActivityWriteResult
        {
            Uri = reviewResult.Uri,
            Cid = reviewResult.Cid,
            Record = reviewRecord,
        };
    }

    // -------------------------------------------------------------------------
    // Delete path
    // -------------------------------------------------------------------------

    private async Task RemoveWatchedItemAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        PopfeedMappedItem mappedItem,
        BaseItem item,
        string watchedListUri,
        string? recentListUri,
        CancellationToken cancellationToken)
    {
        var watchedRkey = PopfeedRkeyBuilder.ForWatchedItem(mappedItem);
        var reviewRkey = PopfeedRkeyBuilder.ForReview(mappedItem);

        await TryDeleteRecordAsync(userConfiguration, session, ListItemCollection, watchedRkey, cancellationToken).ConfigureAwait(false);
        await TryDeleteRecordAsync(userConfiguration, session, ReviewCollection, reviewRkey, cancellationToken).ConfigureAwait(false);

        if (recentListUri is not null)
        {
            var recentRkey = PopfeedRkeyBuilder.ForRecentItem(mappedItem);
            await TryDeleteRecordAsync(userConfiguration, session, ListItemCollection, recentRkey, cancellationToken).ConfigureAwait(false);
        }

        if (mappedItem.Identifiers.TmdbTvSeriesId is { Length: > 0 } seriesId
            && mappedItem.Identifiers.SeasonNumber.HasValue
            && mappedItem.Identifiers.EpisodeNumber.HasValue
            && item is Episode episode)
        {
            await ReconcileTvShowProgressAfterRemovalAsync(
                userConfiguration, session, watchedListUri, seriesId, mappedItem, episode, cancellationToken).ConfigureAwait(false);
        }
    }

    // -------------------------------------------------------------------------
    // TV show / season progress
    // -------------------------------------------------------------------------

    private async Task UpsertTvShowProgressAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        string watchedListUri,
        string watchedListType,
        string seriesId,
        PopfeedMappedItem mappedItem,
        Episode episode,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var tvShowRkey = PopfeedRkeyBuilder.ForTvShowProgress(seriesId);
        var existingTvShow = await _client.GetRecordAsync<PopfeedListItemRecord>(
            userConfiguration.PdsUrl, session, ListItemCollection, tvShowRkey, cancellationToken).ConfigureAwait(false);

        var currentEpisode = new PopfeedWatchedEpisodeRecord
        {
            SeasonNumber = mappedItem.Identifiers.SeasonNumber!.Value,
            EpisodeNumber = mappedItem.Identifiers.EpisodeNumber!.Value,
            TmdbId = GetProviderId(episode, MetadataProvider.Tmdb),
        };

        var existingEpisodes = existingTvShow?.Value.WatchedEpisodes ?? [];
        var mergedEpisodes = MergeWatchedEpisodes(existingEpisodes.ToArray(), [currentEpisode]);

        // Watching a single episode can only change the completion state of that
        // episode's own season, so reconcile just that one marker instead of
        // re-evaluating and rewriting every season on every sync.
        var currentSeason = mappedItem.Identifiers.SeasonNumber!.Value;
        var libraryEpisodes = GetLibraryEpisodeCoordinates(episode);
        var isCurrentSeasonComplete = IsSeasonComplete(libraryEpisodes, mergedEpisodes, currentSeason);
        var isSeriesComplete = IsSeriesComplete(libraryEpisodes, mergedEpisodes);

        await ReconcileSeasonMarkerAsync(
            userConfiguration, session, watchedListUri, watchedListType,
            seriesId, episode.Series?.Name, currentSeason, isCurrentSeasonComplete,
            timestamp, cancellationToken).ConfigureAwait(false);

        var tvShowRecord = new PopfeedListItemRecord
        {
            Identifiers = new PopfeedIdentifiers { TmdbId = seriesId },
            CreativeWorkType = "tv_show",
            ListUri = watchedListUri,
            ListType = watchedListType,
            Status = isSeriesComplete ? PopfeedListItemRecord.FinishedStatus : PopfeedListItemRecord.InProgressStatus,
            AddedAt = !string.IsNullOrWhiteSpace(existingTvShow?.Value.AddedAt)
                ? existingTvShow.Value.AddedAt
                : ToAtProtoDateTime(timestamp.UtcDateTime),
            CompletedAt = isSeriesComplete ? ToAtProtoDateTime(timestamp.UtcDateTime) : null,
            Title = episode.Series?.Name,
            WatchedEpisodes = mergedEpisodes,
        };

        await _client.PutRecordAsync(
            userConfiguration.PdsUrl, session, ListItemCollection, tvShowRkey, tvShowRecord, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Updated tv_show progress for {SeriesName} on account {Identifier}.",
            episode.Series?.Name ?? seriesId, userConfiguration.Identifier);
    }

    private async Task ReconcileTvShowProgressAfterRemovalAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        string watchedListUri,
        string seriesId,
        PopfeedMappedItem mappedItem,
        Episode episode,
        CancellationToken cancellationToken)
    {
        var tvShowRkey = PopfeedRkeyBuilder.ForTvShowProgress(seriesId);
        var existingTvShow = await _client.GetRecordAsync<PopfeedListItemRecord>(
            userConfiguration.PdsUrl, session, ListItemCollection, tvShowRkey, cancellationToken).ConfigureAwait(false);

        if (existingTvShow is null)
        {
            return;
        }

        var currentSeason = mappedItem.Identifiers.SeasonNumber!.Value;
        var removedCoordinate = new EpisodeCoordinate(currentSeason, mappedItem.Identifiers.EpisodeNumber!.Value);
        var remaining = (existingTvShow.Value.WatchedEpisodes ?? [])
            .Where(e => new EpisodeCoordinate(e.SeasonNumber, e.EpisodeNumber) != removedCoordinate)
            .ToList();

        // Removing one episode can only un-complete that episode's own season.
        var libraryEpisodes = GetLibraryEpisodeCoordinates(episode);
        var isCurrentSeasonComplete = IsSeasonComplete(libraryEpisodes, remaining, currentSeason);
        var isSeriesComplete = IsSeriesComplete(libraryEpisodes, remaining);
        var timestamp = DateTimeOffset.UtcNow;
        var watchedListType = GetWatchedListType(mappedItem.CreativeWorkType);

        await ReconcileSeasonMarkerAsync(
            userConfiguration, session, watchedListUri, watchedListType,
            seriesId, episode.Series?.Name, currentSeason, isCurrentSeasonComplete,
            timestamp, cancellationToken).ConfigureAwait(false);

        existingTvShow.Value.Status = isSeriesComplete ? PopfeedListItemRecord.FinishedStatus : PopfeedListItemRecord.InProgressStatus;
        existingTvShow.Value.CompletedAt = isSeriesComplete ? ToAtProtoDateTime(timestamp.UtcDateTime) : null;
        existingTvShow.Value.WatchedEpisodes = remaining;

        await _client.PutRecordAsync(
            userConfiguration.PdsUrl, session, ListItemCollection, tvShowRkey, existingTvShow.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileSeasonMarkerAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        string watchedListUri,
        string watchedListType,
        string seriesId,
        string? seriesTitle,
        int seasonNumber,
        bool isComplete,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var seasonRkey = PopfeedRkeyBuilder.ForTvSeason(seriesId, seasonNumber);

        if (!isComplete)
        {
            await TryDeleteRecordAsync(userConfiguration, session, ListItemCollection, seasonRkey, cancellationToken).ConfigureAwait(false);
            return;
        }

        var existingSeason = await _client.GetRecordAsync<PopfeedListItemRecord>(
            userConfiguration.PdsUrl, session, ListItemCollection, seasonRkey, cancellationToken).ConfigureAwait(false);

        var seasonRecord = new PopfeedListItemRecord
        {
            Identifiers = new PopfeedIdentifiers { TmdbTvSeriesId = seriesId, SeasonNumber = seasonNumber },
            CreativeWorkType = "tv_season",
            ListUri = watchedListUri,
            ListType = watchedListType,
            Status = PopfeedListItemRecord.FinishedStatus,
            AddedAt = !string.IsNullOrWhiteSpace(existingSeason?.Value.AddedAt)
                ? existingSeason.Value.AddedAt
                : ToAtProtoDateTime(timestamp.UtcDateTime),
            CompletedAt = ToAtProtoDateTime(timestamp.UtcDateTime),
            Title = string.IsNullOrWhiteSpace(seriesTitle)
                ? $"Season {seasonNumber:00}"
                : $"{seriesTitle} - Season {seasonNumber:00}",
        };

        await _client.PutRecordAsync(
            userConfiguration.PdsUrl, session, ListItemCollection, seasonRkey, seasonRecord, cancellationToken).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Library helpers
    // -------------------------------------------------------------------------

    private static HashSet<EpisodeCoordinate> GetLibraryEpisodeCoordinates(Episode episode)
    {
        var coordinates = new HashSet<EpisodeCoordinate>();
        if (episode.Series is null)
        {
            if (TryGetEpisodeCoordinate(episode, out var current))
            {
                coordinates.Add(current);
            }

            return coordinates;
        }

        foreach (var child in episode.Series.GetRecursiveChildren())
        {
            if (child is Episode seriesEpisode && TryGetEpisodeCoordinate(seriesEpisode, out var coord))
            {
                coordinates.Add(coord);
            }
        }

        if (coordinates.Count == 0 && TryGetEpisodeCoordinate(episode, out var fallback))
        {
            coordinates.Add(fallback);
        }

        return coordinates;
    }

    private static bool TryGetEpisodeCoordinate(Episode episode, out EpisodeCoordinate coordinate)
    {
        if (!episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue)
        {
            coordinate = default;
            return false;
        }

        coordinate = new EpisodeCoordinate(episode.ParentIndexNumber.Value, episode.IndexNumber.Value);
        return true;
    }

    private static bool IsSeriesComplete(
        HashSet<EpisodeCoordinate> libraryEpisodes,
        List<PopfeedWatchedEpisodeRecord> watchedEpisodes)
    {
        if (libraryEpisodes.Count == 0)
        {
            return false;
        }

        var watchedCoordinates = watchedEpisodes
            .Select(e => new EpisodeCoordinate(e.SeasonNumber, e.EpisodeNumber))
            .ToHashSet();

        return libraryEpisodes.All(watchedCoordinates.Contains);
    }

    private static bool IsSeasonComplete(
        HashSet<EpisodeCoordinate> libraryEpisodes,
        List<PopfeedWatchedEpisodeRecord> watchedEpisodes,
        int season)
    {
        var seasonLibrary = libraryEpisodes.Where(e => e.SeasonNumber == season).ToHashSet();
        if (seasonLibrary.Count == 0)
        {
            return false;
        }

        var watchedCoordinates = watchedEpisodes
            .Select(e => new EpisodeCoordinate(e.SeasonNumber, e.EpisodeNumber))
            .ToHashSet();

        return seasonLibrary.All(watchedCoordinates.Contains);
    }

    internal static bool IsSeasonCompleteForTesting(
        IEnumerable<(int SeasonNumber, int EpisodeNumber)> libraryEpisodes,
        IEnumerable<(int SeasonNumber, int EpisodeNumber)> watchedEpisodes,
        int season)
    {
        var library = libraryEpisodes.Select(e => new EpisodeCoordinate(e.SeasonNumber, e.EpisodeNumber)).ToHashSet();
        var watched = watchedEpisodes.Select(e => new PopfeedWatchedEpisodeRecord { SeasonNumber = e.SeasonNumber, EpisodeNumber = e.EpisodeNumber }).ToList();
        return IsSeasonComplete(library, watched, season);
    }

    internal static bool IsSeriesCompleteForTesting(
        IEnumerable<(int SeasonNumber, int EpisodeNumber)> libraryEpisodes,
        IEnumerable<(int SeasonNumber, int EpisodeNumber)> watchedEpisodes)
    {
        var library = libraryEpisodes.Select(e => new EpisodeCoordinate(e.SeasonNumber, e.EpisodeNumber)).ToHashSet();
        var watched = watchedEpisodes.Select(e => new PopfeedWatchedEpisodeRecord { SeasonNumber = e.SeasonNumber, EpisodeNumber = e.EpisodeNumber }).ToList();
        return IsSeriesComplete(library, watched);
    }

    private static List<PopfeedWatchedEpisodeRecord> MergeWatchedEpisodes(params PopfeedWatchedEpisodeRecord[][] episodeSets)
    {
        var merged = new List<PopfeedWatchedEpisodeRecord>();
        foreach (var set in episodeSets)
        {
            foreach (var episode in set)
            {
                var existing = merged.FirstOrDefault(e =>
                    e.SeasonNumber == episode.SeasonNumber && e.EpisodeNumber == episode.EpisodeNumber);
                if (existing is null)
                {
                    merged.Add(new PopfeedWatchedEpisodeRecord
                    {
                        SeasonNumber = episode.SeasonNumber,
                        EpisodeNumber = episode.EpisodeNumber,
                        TmdbId = episode.TmdbId,
                    });
                }
                else if (string.IsNullOrWhiteSpace(existing.TmdbId) && !string.IsNullOrWhiteSpace(episode.TmdbId))
                {
                    existing.TmdbId = episode.TmdbId;
                }
            }
        }

        return merged;
    }

    // -------------------------------------------------------------------------
    // List ensure (scan once, then cache URI)
    // -------------------------------------------------------------------------

    private async Task<string> EnsureWatchedListAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        string creativeWorkType,
        CancellationToken cancellationToken)
    {
        var listName = GetWatchedListName(userConfiguration, creativeWorkType);
        var cachedUri = userConfiguration.GetWatchedListUri(creativeWorkType);
        if (!string.IsNullOrWhiteSpace(cachedUri))
        {
            return cachedUri;
        }

        string? cursor = null;
        do
        {
            var page = await _client.ListRecordsAsync<PopfeedListRecord>(userConfiguration.PdsUrl, session, ListCollection, cursor, cancellationToken).ConfigureAwait(false);
            var existing = page.Records.FirstOrDefault(r => r.Value is not null && string.Equals(r.Value.Name, listName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                userConfiguration.SetWatchedListUri(creativeWorkType, existing.Uri);
                Plugin.Instance.SaveConfiguration();
                return existing.Uri;
            }

            cursor = page.Cursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        var newRecord = new PopfeedListRecord
        {
            Name = listName,
            Description = "Created by Jellyfin Popfeed plugin to mirror watched history.",
            ListType = GetWatchedListType(creativeWorkType),
            CreatedAt = ToAtProtoDateTime(DateTime.UtcNow),
            Ordered = false,
        };

        var created = await _client.CreateRecordAsync(userConfiguration.PdsUrl, session, ListCollection, newRecord, cancellationToken).ConfigureAwait(false);
        userConfiguration.SetWatchedListUri(creativeWorkType, created.Uri);
        Plugin.Instance.SaveConfiguration();
        return created.Uri;
    }

    private async Task<string?> EnsureRecentListAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        CancellationToken cancellationToken)
    {
        if (!userConfiguration.CreateRecentList)
        {
            return null;
        }

        var listName = string.IsNullOrWhiteSpace(userConfiguration.RecentListName?.Trim()) ? "Recent" : userConfiguration.RecentListName.Trim();
        var cachedUri = userConfiguration.RecentListUri;
        if (!string.IsNullOrWhiteSpace(cachedUri))
        {
            return cachedUri;
        }

        string? cursor = null;
        do
        {
            var page = await _client.ListRecordsAsync<PopfeedListRecord>(userConfiguration.PdsUrl, session, ListCollection, cursor, cancellationToken).ConfigureAwait(false);
            var existing = page.Records.FirstOrDefault(r => r.Value is not null && string.Equals(r.Value.Name, listName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                userConfiguration.SetRecentListUri(existing.Uri);
                Plugin.Instance.SaveConfiguration();
                return existing.Uri;
            }

            cursor = page.Cursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        var newRecord = new PopfeedListRecord
        {
            Name = listName,
            Description = "Created by Jellyfin Popfeed plugin to aggregate recently watched items.",
            ListType = "recent",
            CreatedAt = ToAtProtoDateTime(DateTime.UtcNow),
            Ordered = false,
        };

        var created = await _client.CreateRecordAsync(userConfiguration.PdsUrl, session, ListCollection, newRecord, cancellationToken).ConfigureAwait(false);
        userConfiguration.SetRecentListUri(created.Uri);
        Plugin.Instance.SaveConfiguration();
        return created.Uri;
    }

    // -------------------------------------------------------------------------
    // Record construction helpers
    // -------------------------------------------------------------------------

    private static string GetWatchedListName(PopfeedUserConfiguration userConfiguration, string creativeWorkType)
    {
        var configuredName = userConfiguration.WatchedListName?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredName)
            && !string.Equals(configuredName, "Watched", StringComparison.OrdinalIgnoreCase))
        {
            return configuredName;
        }

        return creativeWorkType switch
        {
            "movie" => "Watched Movies",
            "episode" or "tv_episode" or "tv_season" or "tv_show" => "Watched Shows",
            _ => "Watched",
        };
    }

    private static string GetWatchedListType(string creativeWorkType)
    {
        return creativeWorkType switch
        {
            "movie" => "watched_movies",
            "episode" or "tv_episode" or "tv_season" or "tv_show" => "watched_tv_shows",
            _ => "watched",
        };
    }

    private async Task<PopfeedReviewRecord> BuildReviewRecordAsync(
        AtProtoSessionResponse session,
        PopfeedUserConfiguration userConfiguration,
        PopfeedMappedItem mappedItem,
        string title,
        string activityText,
        BaseItem item,
        DateTimeOffset? playedAt,
        PopfeedReviewRecord? existingReview,
        CancellationToken cancellationToken)
    {
        var timestamp = playedAt ?? DateTimeOffset.UtcNow;
        var reviewCreativeWorkType = mappedItem.CreativeWorkType;
        var reviewIdentifiers = BuildReviewIdentifiers(mappedItem);

        var record = new PopfeedReviewRecord
        {
            Title = item is Episode ep && ep.Series is not null ? ep.Series.Name + " - " + title : title,
            Text = activityText,
            Rating = 5,
            Identifiers = reviewIdentifiers,
            CreativeWorkType = reviewCreativeWorkType,
            CreatedAt = ToAtProtoDateTime(timestamp.UtcDateTime),
            Tags = ["jellyfin", "watched"],
            Facets = [],
            ContainsSpoilers = false,
            IsRevisit = false,
            ReleaseDate = item.PremiereDate.HasValue ? ToAtProtoDateTime(item.PremiereDate.Value) : null,
        };

        // Reuse the poster already uploaded to the PDS when re-syncing an existing
        // review; only upload a fresh blob when none exists yet.
        if (existingReview?.Poster is not null && !string.IsNullOrWhiteSpace(existingReview.PosterUrl))
        {
            record.Poster = existingReview.Poster;
            record.PosterUrl = existingReview.PosterUrl;
        }
        else
        {
            record.Poster = await TryUploadPosterAsync(userConfiguration, session, item, cancellationToken).ConfigureAwait(false);
            record.PosterUrl = BuildPosterUrl(session.Did, record.Poster);
        }

        return record;
    }

    private static PopfeedIdentifiers BuildReviewIdentifiers(PopfeedMappedItem mappedItem)
    {
        var isEpisode = string.Equals(mappedItem.CreativeWorkType, "episode", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mappedItem.CreativeWorkType, "tv_episode", StringComparison.OrdinalIgnoreCase);
        var hasCanonical = !string.IsNullOrWhiteSpace(mappedItem.Identifiers.TmdbTvSeriesId)
            && mappedItem.Identifiers.SeasonNumber.HasValue
            && mappedItem.Identifiers.EpisodeNumber.HasValue;

        var identifiers = new PopfeedIdentifiers
        {
            ImdbId = mappedItem.Identifiers.ImdbId,
            TmdbId = isEpisode ? null : mappedItem.Identifiers.TmdbId,
            TmdbTvSeriesId = mappedItem.Identifiers.TmdbTvSeriesId,
            SeasonNumber = mappedItem.Identifiers.SeasonNumber,
            EpisodeNumber = mappedItem.Identifiers.EpisodeNumber,
        };

        if (isEpisode && !hasCanonical)
        {
            identifiers.TmdbTvSeriesId = null;
            identifiers.SeasonNumber = null;
            identifiers.EpisodeNumber = null;
        }

        return identifiers;
    }

    // -------------------------------------------------------------------------
    // Review merge helpers (preserved for cross-post preservation on re-syncs)
    // -------------------------------------------------------------------------

    internal static PopfeedReviewRecord MergeExistingReview(PopfeedReviewRecord existing, PopfeedReviewRecord desired)
    {
        var mergedTags = new List<string>(existing.Tags ?? []);
        foreach (var tag in desired.Tags ?? [])
        {
            if (!mergedTags.Contains(tag, StringComparer.Ordinal))
            {
                mergedTags.Add(tag);
            }
        }

        return new PopfeedReviewRecord
        {
            Title = desired.Title,
            Text = desired.Text,
            Identifiers = desired.Identifiers,
            CreativeWorkType = desired.CreativeWorkType,
            CreatedAt = existing.CreatedAt,
            Poster = desired.Poster ?? existing.Poster,
            PosterUrl = desired.PosterUrl ?? existing.PosterUrl,
            ReleaseDate = desired.ReleaseDate ?? existing.ReleaseDate,
            Tags = mergedTags,
            Facets = existing.Facets.Count > 0 ? existing.Facets : desired.Facets,
            ContainsSpoilers = existing.ContainsSpoilers,
            IsRevisit = existing.IsRevisit,
            CrossPosts = existing.CrossPosts,
        };
    }

    // -------------------------------------------------------------------------
    // Poster upload
    // -------------------------------------------------------------------------

    private async Task<AtProtoBlob?> TryUploadPosterAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var imagePath = GetPosterImagePath(item);
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        var mimeType = GetMimeType(imagePath);
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(imagePath);
            return await _client.UploadBlobAsync(userConfiguration.PdsUrl, session, stream, mimeType, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to upload poster for {ItemName}.", item.Name);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to read poster for {ItemName}.", item.Name);
            return null;
        }
    }

    private static string? GetPosterImagePath(BaseItem item)
    {
        if (item.HasImage(ImageType.Primary, 0))
        {
            return item.GetImagePath(ImageType.Primary, 0);
        }

        return item switch
        {
            Episode episode when episode.Series is not null && episode.Series.HasImage(ImageType.Primary, 0)
                => episode.Series.GetImagePath(ImageType.Primary, 0),
            Movie movie when movie.HasImage(ImageType.Primary, 0) => movie.GetImagePath(ImageType.Primary, 0),
            _ => null,
        };
    }

    private static string? GetMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => null,
        };
    }

    private static string? BuildPosterUrl(string did, AtProtoBlob? blob)
    {
        if (blob is null || string.IsNullOrWhiteSpace(blob.Ref.Link) || string.IsNullOrWhiteSpace(blob.MimeType))
        {
            return null;
        }

        var extension = blob.MimeType switch
        {
            "image/jpeg" => "jpeg",
            "image/png" => "png",
            "image/webp" => "webp",
            _ => null,
        };

        return extension is null ? null : $"https://cdn.bsky.app/img/feed_fullsize/plain/{did}/{blob.Ref.Link}@{extension}";
    }

    // -------------------------------------------------------------------------
    // Delete helper (treats 404 as success)
    // -------------------------------------------------------------------------

    private async Task TryDeleteRecordAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        string collection,
        string rkey,
        CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteRecordAsync(userConfiguration.PdsUrl, session, collection, rkey, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Deleted {Collection}/{Rkey} on account {Identifier}.", collection, rkey, userConfiguration.Identifier);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404") || ex.Message.Contains("RecordNotFound"))
        {
            // Record did not exist — nothing to do.
        }
    }

    // -------------------------------------------------------------------------
    // Misc helpers
    // -------------------------------------------------------------------------

    private static string? GetProviderId(Episode? episode, MetadataProvider provider)
    {
        if (episode is null)
        {
            return null;
        }

        return episode.TryGetProviderId(provider, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    private static string ToAtProtoDateTime(DateTime dateTime)
        => dateTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

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
