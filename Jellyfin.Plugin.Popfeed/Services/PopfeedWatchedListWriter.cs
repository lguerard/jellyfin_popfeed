using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
/// Stores watched state as native Popfeed activity records.
/// </summary>
public sealed class PopfeedWatchedListWriter : IPopfeedWatchStateWriter
{
    private const string ListCollection = "social.popfeed.feed.list";
    private const string ListItemCollection = "social.popfeed.feed.listItem";
    private const string ReviewCollection = "social.popfeed.feed.review";
    private readonly PopfeedAtProtoClient _client;
    private readonly ILogger<PopfeedWatchedListWriter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PopfeedWatchedListWriter"/> class.
    /// </summary>
    /// <param name="client">The ATProto client.</param>
    /// <param name="logger">The logger.</param>
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
        // Normalise identifiers to the canonical Popfeed URL shape
        // (TmdbTvSeriesId + SeasonNumber + EpisodeNumber) before any remote I/O.
        mappedItem = PopfeedItemUrlBuilder.NormalizeMappedItem(mappedItem, item);
        LogVerbose("Using native Popfeed activity strategy for {ItemName} with account {Identifier}.", title, userConfiguration.Identifier);
        var desiredRecord = await BuildReviewRecordAsync(session, userConfiguration, mappedItem, title, activityText, item, playedAt, cancellationToken).ConfigureAwait(false);
        var existingRecord = await FindExistingActivityAsync(userConfiguration, session, mappedItem.Identifiers, cancellationToken).ConfigureAwait(false);
        var watchedListUri = await EnsureWatchedListAsync(userConfiguration, session, mappedItem.CreativeWorkType, cancellationToken).ConfigureAwait(false);
        var matchingListItems = await FindMatchingListItemsAsync(userConfiguration, session, watchedListUri, mappedItem.Identifiers, cancellationToken).ConfigureAwait(false);
        var existingListItem = SelectPreferredListItem(matchingListItems, mappedItem);
        var duplicateListItems = existingListItem is null
            ? Array.Empty<AtProtoRecord<PopfeedListItemRecord>>()
            : matchingListItems
                .Where(candidate => !string.Equals(candidate.Uri, existingListItem.Uri, StringComparison.Ordinal))
                .ToArray();

        if (played || inProgress)
        {
            var timestamp = playedAt ?? DateTimeOffset.UtcNow;
            var desiredStatus = played
                ? PopfeedListItemRecord.FinishedStatus
                : PopfeedListItemRecord.InProgressStatus;
            if (existingListItem is null)
            {
                var listItemRecord = new PopfeedListItemRecord
                {
                    Identifiers = mappedItem.Identifiers,
                    CreativeWorkType = mappedItem.CreativeWorkType,
                    ListUri = watchedListUri,
                    Status = desiredStatus,
                    AddedAt = ToAtProtoDateTime(timestamp.UtcDateTime),
                    CompletedAt = played ? ToAtProtoDateTime(timestamp.UtcDateTime) : null,
                    Title = title,
                };

                await _client.CreateRecordAsync(userConfiguration.PdsUrl, session, ListItemCollection, listItemRecord, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Synced watched list item for {ItemName} to Popfeed account {Identifier}.", title, userConfiguration.Identifier);
            }
            else
            {
                // An existing list item was found; update it only if something meaningful changed.
                var needsUpdate = NeedsListItemUpdate(existingListItem.Value, mappedItem, desiredStatus, title, played);
                var shouldForceCanonicalEpisodeRefresh = !needsUpdate
                    && (string.Equals(mappedItem.CreativeWorkType, "episode", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(mappedItem.CreativeWorkType, "tv_episode", StringComparison.OrdinalIgnoreCase))
                    && !string.IsNullOrWhiteSpace(existingListItem.Value.Identifiers.TmdbTvSeriesId)
                    && existingListItem.Value.Identifiers.SeasonNumber.HasValue
                    && existingListItem.Value.Identifiers.EpisodeNumber.HasValue;

                if (shouldForceCanonicalEpisodeRefresh)
                {
                    var refreshedRecord = new PopfeedListItemRecord
                    {
                        Identifiers = mappedItem.Identifiers,
                        CreativeWorkType = mappedItem.CreativeWorkType,
                        ListUri = watchedListUri,
                        Status = desiredStatus,
                        AddedAt = string.IsNullOrWhiteSpace(existingListItem.Value.AddedAt)
                            ? ToAtProtoDateTime(timestamp.UtcDateTime)
                            : existingListItem.Value.AddedAt,
                        CompletedAt = played ? ToAtProtoDateTime(timestamp.UtcDateTime) : null,
                        Title = title,
                    };

                    var existingRkey = GetRecordKey(existingListItem.Uri);
                    await _client.DeleteRecordAsync(
                        userConfiguration.PdsUrl,
                        session,
                        ListItemCollection,
                        existingRkey,
                        cancellationToken).ConfigureAwait(false);
                    await _client.CreateRecordAsync(
                        userConfiguration.PdsUrl,
                        session,
                        ListItemCollection,
                        refreshedRecord,
                        cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Replaced canonical watched list item for {ItemName} on account {Identifier}.", title, userConfiguration.Identifier);
                }
                else if (needsUpdate)
                {
                    existingListItem.Value.Identifiers = mappedItem.Identifiers;
                    existingListItem.Value.CreativeWorkType = mappedItem.CreativeWorkType;
                    existingListItem.Value.Status = desiredStatus;
                    existingListItem.Value.Title = title;
                    existingListItem.Value.CompletedAt = played ? ToAtProtoDateTime(timestamp.UtcDateTime) : null;
                    if (string.IsNullOrWhiteSpace(existingListItem.Value.AddedAt))
                    {
                        existingListItem.Value.AddedAt = ToAtProtoDateTime(timestamp.UtcDateTime);
                    }

                    await _client.PutRecordAsync(
                        userConfiguration.PdsUrl,
                        session,
                        ListItemCollection,
                        GetRecordKey(existingListItem.Uri),
                        existingListItem.Value,
                        cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Updated watched list item for {ItemName} on account {Identifier}.", title, userConfiguration.Identifier);
                }
                else
                {
                    LogVerbose("Keeping existing watched list item unchanged for {ItemName}; identifiers already canonical.", title);
                }
            }

            foreach (var duplicateListItem in duplicateListItems)
            {
                var duplicateRkey = GetRecordKey(duplicateListItem.Uri);
                await _client.DeleteRecordAsync(
                    userConfiguration.PdsUrl,
                    session,
                    ListItemCollection,
                    duplicateRkey,
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Removed duplicate watched list item {ListItemUri} for {ItemName} on account {Identifier}.", duplicateListItem.Uri, title, userConfiguration.Identifier);
            }

            if (existingRecord is not null)
            {
                var mergedRecord = MergeExistingReview(existingRecord.Value, desiredRecord);
                if (NeedsReviewUpdate(existingRecord.Value, mergedRecord))
                {
                    var updated = await _client.PutRecordAsync(
                        userConfiguration.PdsUrl,
                        session,
                        ReviewCollection,
                        GetRecordKey(existingRecord.Uri),
                        mergedRecord,
                        cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation("Updated existing Popfeed activity for {ItemName} on account {Identifier}.", title, userConfiguration.Identifier);
                    return new PopfeedActivityWriteResult
                    {
                        Uri = updated.Uri,
                        Cid = updated.Cid,
                        Record = mergedRecord,
                    };
                }

                LogVerbose("Skipping create for {ItemName}: matching Popfeed activity already exists.", title);
                return new PopfeedActivityWriteResult
                {
                    Uri = existingRecord.Uri,
                    Cid = existingRecord.Cid,
                    Record = existingRecord.Value,
                };
            }

            var created = await _client.CreateRecordAsync(userConfiguration.PdsUrl, session, ReviewCollection, desiredRecord, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Created Popfeed activity for {ItemName} on account {Identifier}.", title, userConfiguration.Identifier);
            return new PopfeedActivityWriteResult
            {
                Uri = created.Uri,
                Cid = created.Cid,
                Record = desiredRecord,
            };
        }

        if (!removeWhenUnplayed || existingRecord is null)
        {
            LogVerbose("Skipping delete for {ItemName}: removeWhenUnplayed={RemoveWhenUnplayed}, recordExists={RecordExists}.", title, removeWhenUnplayed, existingRecord is not null);
            if (removeWhenUnplayed && existingListItem is not null)
            {
                var listItemRkey = GetRecordKey(existingListItem.Uri);
                await _client.DeleteRecordAsync(userConfiguration.PdsUrl, session, ListItemCollection, listItemRkey, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Removed watched list item for {ItemName} from Popfeed account {Identifier}.", title, userConfiguration.Identifier);
            }

            return null;
        }

        var rkey = GetRecordKey(existingRecord.Uri);
        await _client.DeleteRecordAsync(userConfiguration.PdsUrl, session, ReviewCollection, rkey, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Removed Popfeed activity for {ItemName} from account {Identifier}.", title, userConfiguration.Identifier);

        if (removeWhenUnplayed && existingListItem is not null)
        {
            var listItemRkey = GetRecordKey(existingListItem.Uri);
            await _client.DeleteRecordAsync(userConfiguration.PdsUrl, session, ListItemCollection, listItemRkey, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Removed watched list item for {ItemName} from Popfeed account {Identifier}.", title, userConfiguration.Identifier);
        }

        return null;
    }

    /// <summary>
    /// Determines whether a watched-list item record needs to be updated.
    /// An update is required when the status, title, identifiers, creative work type,
    /// or completion timestamp differ from the desired state.
    /// </summary>
    /// <param name="existing">The record currently stored in the PDS.</param>
    /// <param name="mappedItem">The desired mapped item.</param>
    /// <param name="desiredStatus">The desired status string.</param>
    /// <param name="title">The desired title.</param>
    /// <param name="played">Whether the item is fully watched.</param>
    /// <returns><see langword="true"/> when the record should be updated.</returns>
    internal static bool NeedsListItemUpdate(
        PopfeedListItemRecord existing,
        PopfeedMappedItem mappedItem,
        string desiredStatus,
        string title,
        bool played)
    {
        return !string.Equals(existing.Status, desiredStatus, StringComparison.Ordinal)
            || !string.Equals(existing.Title, title, StringComparison.Ordinal)
            || !existing.Identifiers.HasSameValues(mappedItem.Identifiers)
            || !string.Equals(existing.CreativeWorkType, mappedItem.CreativeWorkType, StringComparison.Ordinal)
            || (played && existing.CompletedAt is null)
            || (!played && existing.CompletedAt is not null);
    }

    /// <summary>
    /// Delegates to <see cref="PopfeedItemUrlBuilder.NormalizeMappedItem"/> so
    /// tests and callers outside this class can access the same canonical normalisation
    /// without a direct dependency on the URL builder.
    /// </summary>
    /// <param name="mappedItem">The mapped item to normalise.</param>
    /// <param name="sourceItem">Optional Jellyfin item to fill missing season/episode indexes.</param>
    /// <returns>The normalised mapped item.</returns>
    internal static PopfeedMappedItem NormalizeMappedItemForWatchedUrl(PopfeedMappedItem mappedItem, BaseItem? sourceItem = null)
    {
        return PopfeedItemUrlBuilder.NormalizeMappedItem(mappedItem, sourceItem);
    }

    /// <summary>
    /// Ensures the correct watched list exists for the given creative work type, creating it when absent.
    /// The list URI is cached on the user configuration to avoid repeated pagination on subsequent syncs.
    /// </summary>
    /// <param name="userConfiguration">The Popfeed user mapping.</param>
    /// <param name="session">The authenticated ATProto session.</param>
    /// <param name="creativeWorkType">The creative work type (e.g. "movie", "tv_episode").</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ATProto URI of the watched list.</returns>
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
            LogVerbose("Using cached watched list URI for account {Identifier}, CreativeWorkType={CreativeWorkType}: {WatchedListUri}", userConfiguration.Identifier, creativeWorkType, cachedUri);
            return cachedUri;
        }

        string? cursor = null;
        do
        {
            var page = await _client.ListRecordsAsync<PopfeedListRecord>(userConfiguration.PdsUrl, session, ListCollection, cursor, cancellationToken).ConfigureAwait(false);
            var existing = page.Records.FirstOrDefault(record => record.Value is not null && string.Equals(record.Value.Name, listName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                userConfiguration.SetWatchedListUri(creativeWorkType, existing.Uri);
                Plugin.Instance.SaveConfiguration();
                LogVerbose("Found existing watched list {ListName} for account {Identifier}: {WatchedListUri}", listName, userConfiguration.Identifier, existing.Uri);
                return existing.Uri;
            }

            cursor = page.Cursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        var newRecord = new PopfeedListRecord
        {
            Name = listName,
            Description = "Created by Jellyfin Popfeed plugin to mirror watched history.",
            CreatedAt = ToAtProtoDateTime(DateTime.UtcNow),
            Ordered = false,
        };

        var created = await _client.CreateRecordAsync(userConfiguration.PdsUrl, session, ListCollection, newRecord, cancellationToken).ConfigureAwait(false);
        userConfiguration.SetWatchedListUri(creativeWorkType, created.Uri);
        Plugin.Instance.SaveConfiguration();
        LogVerbose("Created watched list {ListName} for account {Identifier}: {WatchedListUri}", listName, userConfiguration.Identifier, created.Uri);
        return created.Uri;
    }

    /// <summary>
    /// Returns the localised watched-list name for the given creative work type.
    /// When the user has configured a custom name (other than the default "Watched"),
    /// that name is used regardless of type.
    /// </summary>
    /// <param name="userConfiguration">The Popfeed user mapping.</param>
    /// <param name="creativeWorkType">The creative work type.</param>
    /// <returns>The list name string.</returns>
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

    /// <summary>
    /// Searches all list-item records in the PDS for one that belongs to
    /// <paramref name="watchedListUri"/> and matches the given identifiers.
    /// Returns null when no match is found.
    /// </summary>
    /// <param name="userConfiguration">The Popfeed user mapping.</param>
    /// <param name="session">The authenticated ATProto session.</param>
    /// <param name="watchedListUri">The parent list URI to filter by.</param>
    /// <param name="identifiers">The identifiers to match.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matching record, or null when absent.</returns>
    private async Task<AtProtoRecord<PopfeedListItemRecord>[]> FindMatchingListItemsAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        string watchedListUri,
        PopfeedIdentifiers identifiers,
        CancellationToken cancellationToken)
    {
        var matches = new List<AtProtoRecord<PopfeedListItemRecord>>();
        string? cursor = null;
        do
        {
            var page = await _client.ListRecordsAsync<PopfeedListItemRecord>(userConfiguration.PdsUrl, session, ListItemCollection, cursor, cancellationToken).ConfigureAwait(false);
            matches.AddRange(page.Records.Where(record =>
                record.Value is not null
                && string.Equals(record.Value.ListUri, watchedListUri, StringComparison.Ordinal)
                && record.Value.Identifiers.Matches(identifiers)));

            cursor = page.Cursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        if (matches.Count > 0)
        {
            LogVerbose("Found {MatchCount} matching watched list item(s) in list {WatchedListUri}.", matches.Count, watchedListUri);
        }

        return matches.ToArray();
    }

    private static AtProtoRecord<PopfeedListItemRecord>? SelectPreferredListItem(
        IReadOnlyCollection<AtProtoRecord<PopfeedListItemRecord>> matches,
        PopfeedMappedItem mappedItem)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        var exact = matches.FirstOrDefault(candidate =>
            candidate.Value is not null
            && candidate.Value.Identifiers.HasSameValues(mappedItem.Identifiers)
            && string.Equals(candidate.Value.CreativeWorkType, mappedItem.CreativeWorkType, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        var canonicalEpisode = matches.FirstOrDefault(candidate =>
            candidate.Value is not null
            && (string.Equals(candidate.Value.CreativeWorkType, "episode", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Value.CreativeWorkType, "tv_episode", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(candidate.Value.Identifiers.TmdbTvSeriesId)
            && candidate.Value.Identifiers.SeasonNumber.HasValue
            && candidate.Value.Identifiers.EpisodeNumber.HasValue);
        if (canonicalEpisode is not null)
        {
            return canonicalEpisode;
        }

        return matches.First();
    }

    /// <summary>
    /// Searches all review records in the PDS for one that matches the given identifiers.
    /// Returns null when no matching activity exists.
    /// </summary>
    /// <param name="userConfiguration">The Popfeed user mapping.</param>
    /// <param name="session">The authenticated ATProto session.</param>
    /// <param name="identifiers">The identifiers to match.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matching record, or null when absent.</returns>
    private async Task<AtProtoRecord<PopfeedReviewRecord>?> FindExistingActivityAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        PopfeedIdentifiers identifiers,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await _client.ListRecordsAsync<PopfeedReviewRecord>(userConfiguration.PdsUrl, session, ReviewCollection, cursor, cancellationToken).ConfigureAwait(false);
            var match = page.Records.FirstOrDefault(record =>
                record.Value?.Identifiers is not null && record.Value.Identifiers.Matches(identifiers));

            if (match is not null)
            {
                LogVerbose("Found existing Popfeed activity for identifiers on account {Identifier}.", userConfiguration.Identifier);
                return match;
            }

            cursor = page.Cursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return null;
    }

    /// <summary>
    /// Builds the activity title shown in the review record.
    /// For episodes this prepends the series name for context.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="fallbackTitle">The raw item title from Jellyfin.</param>
    /// <returns>The formatted activity title.</returns>
    private static string BuildActivityTitle(BaseItem item, string fallbackTitle)
    {
        return item is Episode episode && episode.Series is not null
            ? episode.Series.Name + " - " + fallbackTitle
            : fallbackTitle;
    }

    /// <summary>
    /// Builds the full review record to write (or compare against an existing one).
    /// Attempts to upload the item poster so the review includes a thumbnail.
    /// </summary>
    /// <param name="session">The authenticated ATProto session.</param>
    /// <param name="userConfiguration">The Popfeed user mapping.</param>
    /// <param name="mappedItem">The normalised mapped item.</param>
    /// <param name="title">The display title.</param>
    /// <param name="activityText">The pre-built activity sentence.</param>
    /// <param name="item">The Jellyfin media item (used for poster and release date).</param>
    /// <param name="playedAt">The watched timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The constructed review record.</returns>
    private async Task<PopfeedReviewRecord> BuildReviewRecordAsync(
        AtProtoSessionResponse session,
        PopfeedUserConfiguration userConfiguration,
        PopfeedMappedItem mappedItem,
        string title,
        string activityText,
        BaseItem item,
        DateTimeOffset? playedAt,
        CancellationToken cancellationToken)
    {
        var timestamp = playedAt ?? DateTimeOffset.UtcNow;
        var record = new PopfeedReviewRecord
        {
            Title = BuildActivityTitle(item, title),
            Text = activityText,
            Rating = 5,
            Identifiers = mappedItem.Identifiers,
            CreativeWorkType = mappedItem.CreativeWorkType,
            CreatedAt = ToAtProtoDateTime(timestamp.UtcDateTime),
            Tags = ["jellyfin", "watched"],
            Facets = [],
            ContainsSpoilers = false,
            IsRevisit = false,
            ReleaseDate = GetReleaseDate(item),
        };

        record.Poster = await TryUploadPosterAsync(userConfiguration, session, item, cancellationToken).ConfigureAwait(false);
        record.PosterUrl = BuildPosterUrl(session.Did, record.Poster);
        return record;
    }

    /// <summary>
    /// Attempts to upload the item's primary poster image to the ATProto PDS as a blob.
    /// Returns null when no image is available or the upload fails.
    /// </summary>
    /// <param name="userConfiguration">The Popfeed user mapping.</param>
    /// <param name="session">The authenticated ATProto session.</param>
    /// <param name="item">The media item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The uploaded blob, or null when unavailable.</returns>
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

    /// <summary>
    /// Returns the filesystem path to the primary poster image for the item.
    /// For episodes without a poster, falls back to the parent series poster.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <returns>The image path, or null when no poster is available.</returns>
    private static string? GetPosterImagePath(BaseItem item)
    {
        if (item.HasImage(ImageType.Primary, 0))
        {
            return item.GetImagePath(ImageType.Primary, 0);
        }

        return item switch
        {
            Episode episode when episode.Series is not null && episode.Series.HasImage(ImageType.Primary, 0) => episode.Series.GetImagePath(ImageType.Primary, 0),
            Movie movie when movie.HasImage(ImageType.Primary, 0) => movie.GetImagePath(ImageType.Primary, 0),
            _ => null,
        };
    }

    /// <summary>
    /// Returns the ATProto-formatted premiere date for the item, or null when unknown.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <returns>An ISO-8601 UTC string, or null.</returns>
    private static string? GetReleaseDate(BaseItem item)
    {
        return item.PremiereDate.HasValue
            ? ToAtProtoDateTime(item.PremiereDate.Value)
            : null;
    }

    /// <summary>
    /// Returns the MIME type for a poster image path based on file extension.
    /// Returns null for unsupported types so the image is silently skipped.
    /// </summary>
    /// <param name="path">The image file path.</param>
    /// <returns>The MIME type string, or null when the extension is unsupported.</returns>
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

    /// <summary>
    /// Merges a new desired review record into an existing one, preserving fields that
    /// should survive re-syncs (creation timestamp, poster, cross-posts, revisit flag,
    /// and tags are union-merged so no previously set tag is lost).
    /// </summary>
    /// <param name="existing">The record currently in the PDS.</param>
    /// <param name="desired">The freshly built desired record.</param>
    /// <returns>A merged record ready to be written back.</returns>
    internal static PopfeedReviewRecord MergeExistingReview(PopfeedReviewRecord existing, PopfeedReviewRecord desired)
    {
        // Merge tags by union so we don't lose important tags (e.g., ensure "watched" is present)
        var mergedTags = new List<string>();
        if (existing.Tags is not null)
        {
            mergedTags.AddRange(existing.Tags);
        }

        if (desired.Tags is not null)
        {
            foreach (var t in desired.Tags)
            {
                if (!mergedTags.Contains(t, StringComparer.Ordinal)) mergedTags.Add(t);
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

    /// <summary>
    /// Returns whether the existing review record differs from the merged one
    /// in any field that warrants a PDS write.
    /// </summary>
    /// <param name="existing">The record currently in the PDS.</param>
    /// <param name="merged">The result of <see cref="MergeExistingReview"/>.</param>
    /// <returns><see langword="true"/> when the record should be updated.</returns>
    internal static bool NeedsReviewUpdate(PopfeedReviewRecord existing, PopfeedReviewRecord merged)
    {
        var existingTags = new HashSet<string>(existing.Tags ?? new List<string>(), StringComparer.Ordinal);
        var mergedTags = new HashSet<string>(merged.Tags ?? new List<string>(), StringComparer.Ordinal);

        return !string.Equals(existing.Title, merged.Title, StringComparison.Ordinal)
            || !string.Equals(existing.Text, merged.Text, StringComparison.Ordinal)
            || !existing.Identifiers.HasSameValues(merged.Identifiers)
            || !string.Equals(existing.CreativeWorkType, merged.CreativeWorkType, StringComparison.Ordinal)
            || !string.Equals(existing.PosterUrl, merged.PosterUrl, StringComparison.Ordinal)
            || !string.Equals(existing.ReleaseDate, merged.ReleaseDate, StringComparison.Ordinal)
            || (existing.Poster is null && merged.Poster is not null)
            || !existingTags.SetEquals(mergedTags);
    }

    /// <summary>
    /// Builds the public CDN URL for a poster blob uploaded to a Bluesky PDS.
    /// Returns null when the blob is missing required fields.
    /// </summary>
    /// <param name="did">The account DID used in the CDN path.</param>
    /// <param name="blob">The uploaded blob descriptor.</param>
    /// <returns>The CDN URL string, or null.</returns>
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

        return extension is null
            ? null
            : $"https://cdn.bsky.app/img/feed_fullsize/plain/{did}/{blob.Ref.Link}@{extension}";
    }

    /// <summary>
    /// Formats a <see cref="DateTime"/> as an ISO-8601 UTC string for ATProto records.
    /// </summary>
    /// <param name="dateTime">The datetime to format.</param>
    /// <returns>An ISO-8601 UTC string, e.g. <c>2026-05-19T12:00:00.000Z</c>.</returns>
    private static string ToAtProtoDateTime(DateTime dateTime)
    {
        return dateTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Extracts the ATProto record key (rkey) from the trailing segment of a record URI.
    /// </summary>
    /// <param name="uri">The ATProto record URI.</param>
    /// <returns>The rkey string.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the URI has no segments.</exception>
    private static string GetRecordKey(string uri)
    {
        var segments = uri.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new InvalidOperationException($"Could not extract rkey from URI '{uri}'.");
        }

        return segments[^1];
    }

    /// <summary>
    /// Logs at <c>Information</c> level when debug logging is enabled, otherwise at <c>Debug</c>.
    /// </summary>
    /// <param name="message">The message template.</param>
    /// <param name="arguments">The message arguments.</param>
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