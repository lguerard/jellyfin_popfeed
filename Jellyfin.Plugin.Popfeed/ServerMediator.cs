using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Popfeed.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Popfeed;

/// <summary>
/// Bridges Jellyfin events to Popfeed synchronization.
/// </summary>
public sealed class ServerMediator : IHostedService
{
    private readonly IUserDataManager _userDataManager;
    private readonly PopfeedSyncService _syncService;
    private readonly ILogger<ServerMediator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerMediator"/> class.
    /// </summary>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="syncService">The Popfeed sync service.</param>
    /// <param name="logger">The logger.</param>
    public ServerMediator(IUserDataManager userDataManager, PopfeedSyncService syncService, ILogger<ServerMediator> logger)
    {
        _userDataManager = userDataManager;
        _syncService = syncService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private async void OnUserDataSaved(object? sender, UserDataSaveEventArgs eventArgs)
    {
        try
        {
            var saveReason = eventArgs.SaveReason.ToString();
            var isTogglePlayed = string.Equals(saveReason, "TogglePlayed", StringComparison.Ordinal);
            var isPlaybackFinished = string.Equals(saveReason, "PlaybackFinished", StringComparison.Ordinal);

            if ((!isTogglePlayed && !isPlaybackFinished) || eventArgs.Item is null)
            {
                LogVerbose("Ignoring user data save event because save reason is {SaveReason} or item is missing.", eventArgs.SaveReason);
                return;
            }

            // PlaybackFinished fires for partial views too; only proceed when the item is fully played.
            if (isPlaybackFinished && !(eventArgs.UserData?.Played ?? false))
            {
                LogVerbose("Ignoring PlaybackFinished event for {ItemName} because item is not yet fully played.", eventArgs.Item.Name);
                return;
            }

            if (eventArgs.Item is not Movie && eventArgs.Item is not Episode)
            {
                LogVerbose("Ignoring item {ItemName} because only movies and episodes are supported.", eventArgs.Item.Name);
                return;
            }

            var configuration = Plugin.Instance.Configuration;
            if (!configuration.IsConfigured())
            {
                LogVerbose("Ignoring item {ItemName} because Popfeed is not configured.", eventArgs.Item.Name);
                return;
            }

            DateTimeOffset? playedAt = eventArgs.UserData?.LastPlayedDate.HasValue == true
                ? new DateTimeOffset(DateTime.SpecifyKind(eventArgs.UserData.LastPlayedDate.Value, DateTimeKind.Utc))
                : (DateTimeOffset?)null;

            LogVerbose(
                "Received TogglePlayed event for {ItemName}. UserId={UserId}, Played={Played}, PlayedAt={PlayedAt}",
                eventArgs.Item.Name,
                eventArgs.UserId,
                eventArgs.UserData?.Played ?? false,
                playedAt);

            await _syncService.SyncPlaystateAsync(eventArgs.UserId, eventArgs.Item, eventArgs.UserData?.Played ?? false, playedAt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync Jellyfin watch state to Popfeed.");
        }
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