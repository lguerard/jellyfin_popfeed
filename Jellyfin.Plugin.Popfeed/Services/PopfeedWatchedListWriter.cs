using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Popfeed.Configuration;
using Jellyfin.Plugin.Popfeed.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Popfeed.Services;

/// <summary>
/// Stores watched state as Popfeed list items in a watched list.
/// </summary>
public sealed class PopfeedWatchedListWriter : IPopfeedWatchStateWriter
{
    private const string ListCollection = "social.popfeed.feed.list";
    private const string ListItemCollection = "social.popfeed.feed.listItem";
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
        bool played,
        DateTimeOffset? playedAt,
        bool removeWhenUnplayed,
        CancellationToken cancellationToken)
    {
        LogVerbose("Using watched-list strategy for {ItemName} with account {Identifier}.", title, userConfiguration.Identifier);
        var watchedListUri = await EnsureWatchedListAsync(userConfiguration, session, cancellationToken).ConfigureAwait(false);
        var existingRecord = await FindExistingListItemAsync(userConfiguration, session, watchedListUri, mappedItem.Identifiers, cancellationToken).ConfigureAwait(false);

        if (played)
        {
            if (existingRecord is not null)
            {
                LogVerbose("Skipping create for {ItemName}: matching watched list item already exists.", title);
                return;
            }

            var timestamp = playedAt ?? DateTimeOffset.UtcNow;
            var record = new PopfeedListItemRecord
            {
                Identifiers = mappedItem.Identifiers,
                CreativeWorkType = mappedItem.CreativeWorkType,
                ListUri = watchedListUri,
                AddedAt = ToAtProtoDateTime(timestamp.UtcDateTime),
                CompletedAt = ToAtProtoDateTime(timestamp.UtcDateTime),
                Title = title,
            };

            await _client.CreateRecordAsync(userConfiguration.PdsUrl, session, ListItemCollection, record, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Synced watched state for {ItemName} to Popfeed account {Identifier}.", title, userConfiguration.Identifier);
            return;
        }

        if (!removeWhenUnplayed || existingRecord is null)
        {
            LogVerbose("Skipping delete for {ItemName}: removeWhenUnplayed={RemoveWhenUnplayed}, recordExists={RecordExists}.", title, removeWhenUnplayed, existingRecord is not null);
            return;
        }

        var rkey = GetRecordKey(existingRecord.Uri);
        await _client.DeleteRecordAsync(userConfiguration.PdsUrl, session, ListItemCollection, rkey, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Removed watched state for {ItemName} from Popfeed account {Identifier}.", title, userConfiguration.Identifier);
    }

    private async Task<string> EnsureWatchedListAsync(PopfeedUserConfiguration userConfiguration, AtProtoSessionResponse session, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(userConfiguration.WatchedListUri))
        {
            LogVerbose("Using cached watched list URI for account {Identifier}: {WatchedListUri}", userConfiguration.Identifier, userConfiguration.WatchedListUri);
            return userConfiguration.WatchedListUri;
        }

        var listName = string.IsNullOrWhiteSpace(userConfiguration.WatchedListName) ? "Watched" : userConfiguration.WatchedListName;
        string? cursor = null;
        do
        {
            var page = await _client.ListRecordsAsync<PopfeedListRecord>(userConfiguration.PdsUrl, session, ListCollection, cursor, cancellationToken).ConfigureAwait(false);
            var existing = page.Records.FirstOrDefault(record => string.Equals(record.Value.Name, listName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                userConfiguration.WatchedListUri = existing.Uri;
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
        userConfiguration.WatchedListUri = created.Uri;
        Plugin.Instance.SaveConfiguration();
        LogVerbose("Created watched list {ListName} for account {Identifier}: {WatchedListUri}", listName, userConfiguration.Identifier, created.Uri);
        return created.Uri;
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
                string.Equals(record.Value.ListUri, watchedListUri, StringComparison.Ordinal)
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