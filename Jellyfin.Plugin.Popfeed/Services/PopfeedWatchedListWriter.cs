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
        LogVerbose("Using native Popfeed activity strategy for {ItemName} with account {Identifier}.", title, userConfiguration.Identifier);
        var desiredRecord = await BuildReviewRecordAsync(session, userConfiguration, mappedItem, title, activityText, item, playedAt, cancellationToken).ConfigureAwait(false);
        var existingRecord = await FindExistingActivityAsync(userConfiguration, session, mappedItem.Identifiers, cancellationToken).ConfigureAwait(false);
        var watchedListUri = await EnsureWatchedListAsync(userConfiguration, session, mappedItem.CreativeWorkType, cancellationToken).ConfigureAwait(false);
        var existingListItem = await FindExistingListItemAsync(userConfiguration, session, watchedListUri, mappedItem.Identifiers, cancellationToken).ConfigureAwait(false);

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
            else if (existingListItem is not null)
            {
                var needsUpdate = NeedsListItemUpdate(existingListItem.Value, mappedItem, desiredStatus, title, played);

                if (needsUpdate)
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
            "tv_episode" or "tv_season" or "tv_show" => "Watched Shows",
            _ => "Watched",
        };
    }

    private async Task<AtProtoRecord<PopfeedListItemRecord>?> FindExistingListItemAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        string watchedListUri,
        PopfeedIdentifiers identifiers,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await _client.ListRecordsAsync<PopfeedListItemRecord>(userConfiguration.PdsUrl, session, ListItemCollection, cursor, cancellationToken).ConfigureAwait(false);
            var match = page.Records.FirstOrDefault(record =>
                record.Value is not null
                && string.Equals(record.Value.ListUri, watchedListUri, StringComparison.Ordinal)
                && record.Value.Identifiers.Matches(identifiers));

            if (match is not null)
            {
                LogVerbose("Found matching watched list item for identifiers in list {WatchedListUri}.", watchedListUri);
                return match;
            }

            cursor = page.Cursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return null;
    }

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

    private static string BuildActivityTitle(BaseItem item, string fallbackTitle)
    {
        return item is Episode episode && episode.Series is not null
            ? episode.Series.Name + " - " + fallbackTitle
            : fallbackTitle;
    }

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
            Episode episode when episode.Series is not null && episode.Series.HasImage(ImageType.Primary, 0) => episode.Series.GetImagePath(ImageType.Primary, 0),
            Movie movie when movie.HasImage(ImageType.Primary, 0) => movie.GetImagePath(ImageType.Primary, 0),
            _ => null,
        };
    }

    private static string? GetReleaseDate(BaseItem item)
    {
        if (item.PremiereDate.HasValue)
        {
            return ToAtProtoDateTime(item.PremiereDate.Value);
        }

        return null;
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
            Poster = existing.Poster ?? desired.Poster,
            PosterUrl = existing.PosterUrl ?? desired.PosterUrl,
            ReleaseDate = existing.ReleaseDate ?? desired.ReleaseDate,
            Tags = mergedTags,
            Facets = existing.Facets.Count > 0 ? existing.Facets : desired.Facets,
            ContainsSpoilers = existing.ContainsSpoilers,
            IsRevisit = existing.IsRevisit,
            CrossPosts = existing.CrossPosts,
        };
    }

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

    private static string ToAtProtoDateTime(DateTime dateTime)
    {
        return dateTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static string GetRecordKey(string uri)
    {
        var segments = uri.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new InvalidOperationException($"Could not extract rkey from URI '{uri}'.");
        }

        return segments[^1];
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