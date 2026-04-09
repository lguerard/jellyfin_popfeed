using System;
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
        DateTimeOffset? playedAt,
        bool removeWhenUnplayed,
        CancellationToken cancellationToken)
    {
        LogVerbose("Using native Popfeed activity strategy for {ItemName} with account {Identifier}.", title, userConfiguration.Identifier);
        var desiredRecord = await BuildReviewRecordAsync(session, userConfiguration, mappedItem, title, activityText, item, playedAt, cancellationToken).ConfigureAwait(false);
        var existingRecord = await FindExistingActivityAsync(userConfiguration, session, mappedItem.Identifiers, activityText, cancellationToken).ConfigureAwait(false);

        if (played)
        {
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
            return null;
        }

        var rkey = GetRecordKey(existingRecord.Uri);
        await _client.DeleteRecordAsync(userConfiguration.PdsUrl, session, ReviewCollection, rkey, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Removed Popfeed activity for {ItemName} from account {Identifier}.", title, userConfiguration.Identifier);
        return null;
    }

    private async Task<AtProtoRecord<PopfeedReviewRecord>?> FindExistingActivityAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        PopfeedIdentifiers identifiers,
        string activityText,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await _client.ListRecordsAsync<PopfeedReviewRecord>(userConfiguration.PdsUrl, session, ReviewCollection, cursor, cancellationToken).ConfigureAwait(false);
            var match = page.Records.FirstOrDefault(record =>
                record.Value.Identifiers.Matches(identifiers)
                && string.Equals(record.Value.Text, activityText, StringComparison.Ordinal));

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

    private static PopfeedReviewRecord MergeExistingReview(PopfeedReviewRecord existing, PopfeedReviewRecord desired)
    {
        return new PopfeedReviewRecord
        {
            Title = desired.Title,
            Text = desired.Text,
            Identifiers = desired.Identifiers,
            CreativeWorkType = desired.CreativeWorkType,
            CreatedAt = existing.CreatedAt,
            Poster = existing.Poster ?? desired.Poster,
            ReleaseDate = existing.ReleaseDate ?? desired.ReleaseDate,
            Tags = existing.Tags.Count > 0 ? existing.Tags : desired.Tags,
            Facets = existing.Facets.Count > 0 ? existing.Facets : desired.Facets,
            ContainsSpoilers = existing.ContainsSpoilers,
            IsRevisit = existing.IsRevisit,
            CrossPosts = existing.CrossPosts,
        };
    }

    private static bool NeedsReviewUpdate(PopfeedReviewRecord existing, PopfeedReviewRecord merged)
    {
        return !string.Equals(existing.Title, merged.Title, StringComparison.Ordinal)
            || !string.Equals(existing.Text, merged.Text, StringComparison.Ordinal)
            || !string.Equals(existing.ReleaseDate, merged.ReleaseDate, StringComparison.Ordinal)
            || (existing.Poster is null && merged.Poster is not null)
            || existing.Tags.Count == 0 && merged.Tags.Count > 0;
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