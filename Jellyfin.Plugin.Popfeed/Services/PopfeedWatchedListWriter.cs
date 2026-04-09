using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Popfeed.Configuration;
using Jellyfin.Plugin.Popfeed.Models;
using MediaBrowser.Controller.Entities;
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
    public async Task SyncAsync(
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
        var existingRecord = await FindExistingActivityAsync(userConfiguration, session, mappedItem.Identifiers, activityText, cancellationToken).ConfigureAwait(false);

        if (played)
        {
            if (existingRecord is not null)
            {
                LogVerbose("Skipping create for {ItemName}: matching Popfeed activity already exists.", title);
                return;
            }

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
            };

            await _client.CreateRecordAsync(userConfiguration.PdsUrl, session, ReviewCollection, record, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Created Popfeed activity for {ItemName} on account {Identifier}.", title, userConfiguration.Identifier);
            return;
        }

        if (!removeWhenUnplayed || existingRecord is null)
        {
            LogVerbose("Skipping delete for {ItemName}: removeWhenUnplayed={RemoveWhenUnplayed}, recordExists={RecordExists}.", title, removeWhenUnplayed, existingRecord is not null);
            return;
        }

        var rkey = GetRecordKey(existingRecord.Uri);
        await _client.DeleteRecordAsync(userConfiguration.PdsUrl, session, ReviewCollection, rkey, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Removed Popfeed activity for {ItemName} from account {Identifier}.", title, userConfiguration.Identifier);
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
        return item is MediaBrowser.Controller.Entities.TV.Episode episode && episode.Series is not null
            ? episode.Series.Name + " - " + fallbackTitle
            : fallbackTitle;
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