using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Popfeed.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Popfeed.Services;

/// <summary>
/// Background migration task that runs once, two minutes after the plugin loads,
/// when <c>EnableHistoricalSync</c> is set.  For each configured account it:
/// <list type="number">
/// <item>purges legacy records left by older plugin versions (any listItem or
/// review whose record key is a random TID rather than a deterministic
/// dotted rkey), removing the stale entries that rendered broken
/// <c>/episode/&lt;id&gt;</c> URLs; and</item>
/// <item>re-syncs every watched item from the Jellyfin library so the lists are
/// rebuilt with canonical identifiers.</item>
/// </list>
/// Deterministic rkeys make the whole task idempotent: re-running it never
/// creates duplicates.
/// </summary>
public sealed class PopfeedHistoricalSyncTask : IHostedService
{
    private const string ListItemCollection = "social.popfeed.feed.listItem";
    private const string ReviewCollection = "social.popfeed.feed.review";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ItemThrottle = TimeSpan.FromMilliseconds(400);

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly PopfeedSyncService _syncService;
    private readonly PopfeedAtProtoClient _client;
    private readonly ILogger<PopfeedHistoricalSyncTask> _logger;

    private CancellationTokenSource? _cts;

    /// <summary>
    /// Initializes a new instance of the <see cref="PopfeedHistoricalSyncTask"/> class.
    /// </summary>
    public PopfeedHistoricalSyncTask(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IUserManager userManager,
        PopfeedSyncService syncService,
        PopfeedAtProtoClient client,
        ILogger<PopfeedHistoricalSyncTask> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _syncService = syncService;
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(StartupDelay, cancellationToken).ConfigureAwait(false);

            var configuration = Plugin.Instance.Configuration;
            if (!configuration.EnableHistoricalSync || !configuration.IsConfigured())
            {
                return;
            }

            _logger.LogInformation("Popfeed historical sync starting.");

            foreach (var userConfig in configuration.PopfeedUsers)
            {
                if (!userConfig.IsConfigured())
                {
                    continue;
                }

                var jellyfinUser = _userManager.GetUserById(userConfig.JellyfinUserId);
                if (jellyfinUser is null)
                {
                    _logger.LogWarning("Popfeed historical sync: Jellyfin user {UserId} not found, skipping.", userConfig.JellyfinUserId);
                    continue;
                }

                // Step 1: purge legacy random-rkey records so old broken entries disappear.
                try
                {
                    await PurgeLegacyRecordsAsync(userConfig, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Popfeed historical sync: legacy purge failed for {Identifier}; continuing with re-sync.", userConfig.Identifier);
                }

                // Step 2: fetch items scoped to this user so Jellyfin returns user-specific play state.
                var userItems = _libraryManager.GetItemList(new InternalItemsQuery(jellyfinUser)
                {
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                    Recursive = true,
                    IsPlayed = true,
                    EnableTotalRecordCount = false,
                });

                _logger.LogInformation(
                    "Popfeed historical sync: {Count} watched items for account {Identifier}.",
                    userItems.Count,
                    userConfig.Identifier);

                var synced = 0;
                var skipped = 0;

                foreach (var item in userItems)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (item is not Movie && item is not Episode)
                    {
                        skipped++;
                        continue;
                    }

                    var userData = _userDataManager.GetUserData(jellyfinUser, item);
                    if (userData is null || !userData.Played)
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        DateTimeOffset? playedAt = userData.LastPlayedDate.HasValue
                            ? new DateTimeOffset(DateTime.SpecifyKind(userData.LastPlayedDate.Value, DateTimeKind.Utc))
                            : null;

                        await _syncService.SyncPlaystateAsync(
                            userConfig.JellyfinUserId,
                            item,
                            played: true,
                            inProgress: false,
                            playedAt,
                            cancellationToken).ConfigureAwait(false);

                        synced++;

                        // Throttle so the PDS is not overwhelmed.
                        await Task.Delay(ItemThrottle, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Popfeed historical sync: failed to sync {ItemName}.", item.Name);
                    }
                }

                _logger.LogInformation(
                    "Popfeed historical sync complete for {Identifier}: {Synced} synced, {Skipped} unwatched skipped.",
                    userConfig.Identifier,
                    synced,
                    skipped);
            }

            // One-shot migration: clear the flag so a restart does not repeat the
            // purge and full re-sync.  Live playback events keep the lists current
            // afterwards.  Re-enable from settings to run the migration again.
            if (!cancellationToken.IsCancellationRequested)
            {
                configuration.EnableHistoricalSync = false;
                Plugin.Instance.SaveConfiguration();
                _logger.LogInformation("Popfeed historical sync finished; disabling EnableHistoricalSync flag.");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Popfeed historical sync cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Popfeed historical sync failed.");
        }
    }

    /// <summary>
    /// Deletes every <c>listItem</c> and <c>review</c> record written by older
    /// plugin versions.  Those records used random TID record keys; the current
    /// plugin writes only deterministic dotted rkeys (e.g. <c>w.ep.4556.1.16</c>).
    /// Any record key without a dot therefore predates deterministic routing and
    /// is removed so its stale (often broken-URL) entry disappears from the list.
    /// </summary>
    private async Task PurgeLegacyRecordsAsync(
        Configuration.PopfeedUserConfiguration userConfig,
        CancellationToken cancellationToken)
    {
        var session = await _client.GetOrCreateSessionAsync(userConfig, cancellationToken).ConfigureAwait(false);
        var removed = 0;
        removed += await PurgeCollectionAsync<PopfeedListItemRecord>(userConfig, session, ListItemCollection, cancellationToken).ConfigureAwait(false);
        removed += await PurgeCollectionAsync<PopfeedReviewRecord>(userConfig, session, ReviewCollection, cancellationToken).ConfigureAwait(false);

        if (removed > 0)
        {
            _logger.LogInformation("Popfeed historical sync: purged {Count} legacy record(s) for {Identifier}.", removed, userConfig.Identifier);
        }
    }

    private async Task<int> PurgeCollectionAsync<TRecord>(
        Configuration.PopfeedUserConfiguration userConfig,
        AtProtoSessionResponse session,
        string collection,
        CancellationToken cancellationToken)
    {
        var legacyRkeys = new System.Collections.Generic.List<string>();
        string? cursor = null;
        do
        {
            var page = await _client.ListRecordsAsync<TRecord>(userConfig.PdsUrl, session, collection, cursor, cancellationToken).ConfigureAwait(false);
            foreach (var record in page.Records)
            {
                var rkey = ExtractRkey(record.Uri);
                if (!string.IsNullOrEmpty(rkey) && !rkey.Contains('.', StringComparison.Ordinal))
                {
                    legacyRkeys.Add(rkey);
                }
            }

            cursor = page.Cursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        foreach (var rkey in legacyRkeys)
        {
            await _client.DeleteRecordAsync(userConfig.PdsUrl, session, collection, rkey, cancellationToken).ConfigureAwait(false);
        }

        return legacyRkeys.Count;
    }

    private static string ExtractRkey(string uri)
    {
        var segments = uri.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? string.Empty : segments[^1];
    }
}
