using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Popfeed.Services;

/// <summary>
/// Background task that syncs previously watched Jellyfin items to Popfeed on startup.
/// Runs once, two minutes after the plugin loads, to allow Jellyfin's library to
/// finish initialising.  Uses deterministic ATProto rkeys, so the sync is fully
/// idempotent: re-running it never creates duplicates or overwrites user edits.
/// Enable via plugin settings: <c>EnableHistoricalSync = true</c>.
/// </summary>
public sealed class PopfeedHistoricalSyncTask : IHostedService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ItemThrottle = TimeSpan.FromMilliseconds(400);

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly PopfeedSyncService _syncService;
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
        ILogger<PopfeedHistoricalSyncTask> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _syncService = syncService;
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

                // Fetch items scoped to this user so Jellyfin returns user-specific play state.
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
}
