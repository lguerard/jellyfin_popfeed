using System;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Popfeed.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Popfeed.Api;

/// <summary>
/// Plugin API controller for Popfeed settings page helpers.
/// </summary>
[ApiController]
[Authorize]
[Route("Popfeed")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class PopfeedController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly Services.PopfeedAtProtoClient _atProtoClient;
    private readonly Services.PopfeedSyncService _syncService;
    private readonly Services.PopfeedSyncStatusStore _statusStore;
    private readonly ILogger<PopfeedController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PopfeedController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="atProtoClient">The ATProto client.</param>
    /// <param name="syncService">The sync service.</param>
    /// <param name="statusStore">The status store.</param>
    /// <param name="logger">The logger.</param>
    public PopfeedController(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IUserManager userManager,
        Services.PopfeedAtProtoClient atProtoClient,
        Services.PopfeedSyncService syncService,
        Services.PopfeedSyncStatusStore statusStore,
        ILogger<PopfeedController> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _atProtoClient = atProtoClient;
        _syncService = syncService;
        _statusStore = statusStore;
        _logger = logger;
    }

    /// <summary>
    /// Gets Jellyfin users for the settings page mapping editor.
    /// </summary>
    /// <returns>The Jellyfin users.</returns>
    [HttpGet("Users")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PopfeedJellyfinUserDto[]> GetUsers()
    {
        var users = _userManager.GetUsers()
            .OrderBy(user => user.Username)
            .Select(user => new PopfeedJellyfinUserDto
            {
                Id = user.Id,
                Name = user.Username,
            })
            .ToArray();

        return Ok(users);
    }

    /// <summary>
    /// Searches library items for the settings page.
    /// </summary>
    /// <param name="searchTerm">The search term.</param>
    /// <param name="userId">Optional user id used to scope visible results.</param>
    /// <returns>The matching items.</returns>
    [HttpGet("Items/Search")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PopfeedSearchItemDto[]> SearchItems([FromQuery] string searchTerm, [FromQuery] Guid? userId)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return Ok(Array.Empty<PopfeedSearchItemDto>());
        }

        var user = userId.HasValue ? _userManager.GetUserById(userId.Value) : null;
        var query = new InternalItemsQuery(user)
        {
            SearchTerm = searchTerm,
            Recursive = true,
            Limit = 20,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode },
            EnableTotalRecordCount = false,
        };

        var items = _libraryManager.GetItemList(query)
            .Where(item => item is Movie || item is Series || item is Episode)
            .Take(20)
            .Select(item => new PopfeedSearchItemDto
            {
                Id = item.Id,
                Name = item.Name,
                ItemType = item.GetType().Name,
                SeriesId = item is Episode episode && episode.Series is not null ? episode.Series.Id : null,
                SeriesName = item is Episode episode2 && episode2.Series is not null ? episode2.Series.Name : null,
            })
            .ToArray();

        return Ok(items);
    }

    /// <summary>
    /// Gets recent Popfeed sync activity for the settings page.
    /// </summary>
    /// <param name="userId">Optional Jellyfin user id filter.</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <returns>The recent sync entries.</returns>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PopfeedSyncTestResult[]> GetStatus([FromQuery] Guid? userId, [FromQuery] int limit = 12)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 50);
        var userNames = _userManager.GetUsers().ToDictionary(user => user.Id, user => user.Username);

        var entries = _statusStore.GetRecent(userId, effectiveLimit)
            .Select(entry =>
            {
                entry.UserName = userNames.TryGetValue(entry.UserId, out var userName) ? userName : null;
                return entry;
            })
            .ToArray();

        return Ok(entries);
    }

    /// <summary>
    /// Runs a plugin test sync or dry run for an item.
    /// </summary>
    /// <param name="request">The test request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The test result.</returns>
    [HttpPost("TestSync")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PopfeedSyncTestResult>> TestSync([FromBody] PopfeedTestSyncRequest request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Popfeed test sync request received for user {UserId}, item {ItemId}, dryRun={DryRun}.", request.UserId, request.ItemId, request.DryRun);

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null)
        {
            return NotFound(new PopfeedSyncTestResult
            {
                Success = false,
                Message = "Item not found.",
                ItemId = request.ItemId,
                UserId = request.UserId,
            });
        }

        var result = await _syncService.TestSyncAsync(request.UserId, item, request.Played, request.DryRun, cancellationToken).ConfigureAwait(false);
        var user = _userManager.GetUserById(request.UserId);
        if (user is not null)
        {
            result.UserName = user.Username;
        }

        return Ok(result);
    }

    /// <summary>
    /// Repairs incorrect recent TV episode activities by deleting episode reviews
    /// newer than a marker review and replaying watched episodes from Jellyfin.
    /// </summary>
    /// <param name="request">The repair request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The repair operation result.</returns>
    [HttpPost("RepairEpisodeActivities")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PopfeedRepairEpisodesResult>> RepairEpisodeActivities([FromBody] PopfeedRepairEpisodesRequest request, CancellationToken cancellationToken)
    {
        if (request.UserId == Guid.Empty)
        {
            return BadRequest(new PopfeedRepairEpisodesResult
            {
                Success = false,
                Message = "A valid Jellyfin user id is required.",
            });
        }

        var latestMatchesCount = Math.Clamp(request.LatestMatchesCount, 0, 500);
        var useLatestCountMode = latestMatchesCount > 0;
        var markerTitle = string.IsNullOrWhiteSpace(request.MarkerTitle)
            ? "Project Hail Mary"
            : request.MarkerTitle.Trim();
        var maxReplayItems = Math.Clamp(request.MaxReplayItems, 1, 1000);

        var userConfiguration = Plugin.Instance.Configuration.GetUserConfiguration(request.UserId);
        if (userConfiguration is null || !userConfiguration.IsConfigured())
        {
            return BadRequest(new PopfeedRepairEpisodesResult
            {
                Success = false,
                Message = "No configured Popfeed mapping exists for this Jellyfin user.",
            });
        }

        var result = new PopfeedRepairEpisodesResult();

        var session = await _atProtoClient.CreateSessionAsync(userConfiguration, cancellationToken).ConfigureAwait(false);
        var allReviews = await LoadAllReviewRecordsAsync(userConfiguration, session, cancellationToken).ConfigureAwait(false);

        (AtProtoRecord<PopfeedReviewRecord> Record, DateTimeOffset CreatedAt)[] wrongEpisodeReviews;
        (Episode Episode, DateTimeOffset PlayedAtUtc)[] watchedEpisodes;

        if (useLatestCountMode)
        {
            wrongEpisodeReviews = allReviews
                .Where(record => record.Value is not null && IsEpisodeReview(record.Value))
                .Select(record => new
                {
                    Record = record,
                    CreatedAt = ParseRecordTimestamp(record.Value.CreatedAt),
                })
                .Where(entry => entry.CreatedAt.HasValue)
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(latestMatchesCount)
                .OrderBy(entry => entry.CreatedAt)
                .Select(entry => (entry.Record, entry.CreatedAt!.Value))
                .ToArray();

            watchedEpisodes = Array.Empty<(Episode Episode, DateTimeOffset PlayedAtUtc)>();
        }
        else
        {
            var marker = allReviews
                .Where(record => record.Value is not null && !string.IsNullOrWhiteSpace(record.Value.Title))
                .Select(record => new
                {
                    Record = record,
                    CreatedAt = ParseRecordTimestamp(record.Value.CreatedAt),
                })
                .Where(entry => entry.CreatedAt.HasValue
                    && entry.Record.Value.Title.Contains(markerTitle, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.CreatedAt)
                .FirstOrDefault();

            if (marker is null)
            {
                return NotFound(new PopfeedRepairEpisodesResult
                {
                    Success = false,
                    Message = $"Could not find marker review title '{markerTitle}' in Popfeed records.",
                });
            }

            var markerTimestampUtc = marker.CreatedAt!.Value;
            result.MarkerTimestampUtc = markerTimestampUtc;

            wrongEpisodeReviews = allReviews
                .Where(record => record.Value is not null)
                .Select(record => new
                {
                    Record = record,
                    CreatedAt = ParseRecordTimestamp(record.Value.CreatedAt),
                })
                .Where(entry => entry.CreatedAt.HasValue
                    && entry.CreatedAt.Value > markerTimestampUtc
                    && IsEpisodeReview(entry.Record.Value))
                .OrderBy(entry => entry.CreatedAt)
                .Select(entry => (entry.Record, entry.CreatedAt!.Value))
                .ToArray();

            watchedEpisodes = GetWatchedEpisodesSince(request.UserId, markerTimestampUtc)
                .OrderBy(entry => entry.PlayedAtUtc)
                .Take(maxReplayItems)
                .ToArray();
        }

        result.WrongReviewCount = wrongEpisodeReviews.Length;

        var latestRepairPlan = useLatestCountMode
            ? BuildLatestRepairPlan(request.UserId, wrongEpisodeReviews)
            : Array.Empty<(AtProtoRecord<PopfeedReviewRecord> WrongReview, Episode Episode, DateTimeOffset PlayedAtUtc)>();

        result.ReplayCandidateCount = useLatestCountMode
            ? latestRepairPlan.Length
            : watchedEpisodes.Length;

        if (request.DryRun)
        {
            result.Success = true;
            result.Message = useLatestCountMode
                ? $"Dry run: found {result.WrongReviewCount} latest episode matches to repair and {result.ReplayCandidateCount} watched episodes to replay."
                : $"Dry run: found {result.WrongReviewCount} wrong episode reviews after marker and {result.ReplayCandidateCount} watched episodes to replay.";
            return Ok(result);
        }

        if (useLatestCountMode)
        {
            foreach (var plannedRepair in latestRepairPlan)
            {
                result.ReplayAttempted++;
                try
                {
                    await RetrySyncEpisodeAsync(
                        request.UserId,
                        plannedRepair.Episode,
                        plannedRepair.PlayedAtUtc,
                        cancellationToken).ConfigureAwait(false);
                    result.ReplaySucceeded++;

                    var wrongRkey = GetRecordKey(plannedRepair.WrongReview.Uri);
                    await _atProtoClient.DeleteRecordAsync(
                        userConfiguration.PdsUrl,
                        session,
                        "social.popfeed.feed.review",
                        wrongRkey,
                        cancellationToken).ConfigureAwait(false);
                    result.DeletedWrongReviews++;

                    // Pace requests to reduce ATProto rate-limit pressure.
                    await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to repair review {ReviewUri} using episode {ItemName} ({ItemId}).",
                        plannedRepair.WrongReview.Uri,
                        plannedRepair.Episode.Name,
                        plannedRepair.Episode.Id);
                }
            }
        }
        else
        {
            foreach (var watchedEpisode in watchedEpisodes)
            {
                result.ReplayAttempted++;
                try
                {
                    await RetrySyncEpisodeAsync(
                        request.UserId,
                        watchedEpisode.Episode,
                        watchedEpisode.PlayedAtUtc,
                        cancellationToken).ConfigureAwait(false);
                    result.ReplaySucceeded++;

                    // Pace requests to reduce ATProto rate-limit pressure.
                    await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to replay episode {ItemName} ({ItemId}) during Popfeed repair.",
                        watchedEpisode.Episode.Name,
                        watchedEpisode.Episode.Id);
                }
            }

            // Safety guard: only delete wrong reviews once replay fully succeeded.
            if (result.ReplaySucceeded == result.ReplayAttempted && result.ReplayAttempted > 0)
            {
                foreach (var wrongReview in wrongEpisodeReviews)
                {
                    var rkey = GetRecordKey(wrongReview.Record.Uri);
                    await _atProtoClient.DeleteRecordAsync(
                        userConfiguration.PdsUrl,
                        session,
                        "social.popfeed.feed.review",
                        rkey,
                        cancellationToken).ConfigureAwait(false);
                    result.DeletedWrongReviews++;
                }
            }
        }

        result.Success = true;
        result.Message = useLatestCountMode
            ? $"Replayed {result.ReplaySucceeded}/{result.ReplayAttempted} watched episodes. Deleted {result.DeletedWrongReviews} latest wrong matches after successful replay."
            : $"Replayed {result.ReplaySucceeded}/{result.ReplayAttempted} watched episodes. Deleted {result.DeletedWrongReviews} wrong episode reviews after successful replay.";
        return Ok(result);
    }

    private async Task RetrySyncEpisodeAsync(
        Guid jellyfinUserId,
        Episode episode,
        DateTimeOffset playedAtUtc,
        CancellationToken cancellationToken)
    {
        var maxAttempts = 4;
        var delayMs = 2000;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _syncService.SyncPlaystateAsync(
                    jellyfinUserId,
                    episode,
                    true,
                    false,
                    playedAtUtc,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsRateLimitError(ex) && attempt < maxAttempts)
            {
                lastException = ex;
                _logger.LogWarning(
                    ex,
                    "Rate limited while replaying {ItemName} ({ItemId}). Retrying attempt {Attempt}/{MaxAttempts}.",
                    episode.Name,
                    episode.Id,
                    attempt,
                    maxAttempts);
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                delayMs *= 2;
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        throw lastException ?? new InvalidOperationException("Episode replay failed without an error.");
    }

    private async Task<System.Collections.Generic.List<AtProtoRecord<PopfeedReviewRecord>>> LoadAllReviewRecordsAsync(
        Configuration.PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        CancellationToken cancellationToken)
    {
        var records = new System.Collections.Generic.List<AtProtoRecord<PopfeedReviewRecord>>();
        string? cursor = null;

        do
        {
            var page = await _atProtoClient.ListRecordsAsync<PopfeedReviewRecord>(
                userConfiguration.PdsUrl,
                session,
                "social.popfeed.feed.review",
                cursor,
                cancellationToken).ConfigureAwait(false);

            records.AddRange(page.Records.Where(record => record.Value is not null));
            cursor = page.Cursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return records;
    }

    private System.Collections.Generic.IEnumerable<(Episode Episode, DateTimeOffset PlayedAtUtc)> GetWatchedEpisodesSince(
        Guid jellyfinUserId,
        DateTimeOffset markerTimestampUtc)
    {
        var user = _userManager.GetUserById(jellyfinUserId);
        if (user is null)
        {
            return Array.Empty<(Episode Episode, DateTimeOffset PlayedAtUtc)>();
        }

        var query = new InternalItemsQuery(user)
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            EnableTotalRecordCount = false,
        };

        return _libraryManager
            .GetItemList(query)
            .OfType<Episode>()
            .Select(episode => new
            {
                Episode = episode,
                UserData = _userDataManager.GetUserData(user, episode),
            })
            .Where(entry => entry.UserData is not null
                && entry.UserData.Played
                && entry.UserData.LastPlayedDate.HasValue)
            .Select(entry =>
            {
                var playedAtUtc = new DateTimeOffset(
                    DateTime.SpecifyKind(entry.UserData!.LastPlayedDate!.Value, DateTimeKind.Utc));
                return (entry.Episode, PlayedAtUtc: playedAtUtc);
            })
            .Where(entry => entry.PlayedAtUtc > markerTimestampUtc);
    }

    private System.Collections.Generic.IEnumerable<(Episode Episode, DateTimeOffset PlayedAtUtc)> GetLatestWatchedEpisodes(
        Guid jellyfinUserId,
        int latestCount)
    {
        var user = _userManager.GetUserById(jellyfinUserId);
        if (user is null)
        {
            return Array.Empty<(Episode Episode, DateTimeOffset PlayedAtUtc)>();
        }

        var query = new InternalItemsQuery(user)
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            EnableTotalRecordCount = false,
        };

        return _libraryManager
            .GetItemList(query)
            .OfType<Episode>()
            .Select(episode => new
            {
                Episode = episode,
                UserData = _userDataManager.GetUserData(user, episode),
            })
            .Where(entry => entry.UserData is not null
                && entry.UserData.Played
                && entry.UserData.LastPlayedDate.HasValue)
            .Select(entry =>
            {
                var playedAtUtc = new DateTimeOffset(
                    DateTime.SpecifyKind(entry.UserData!.LastPlayedDate!.Value, DateTimeKind.Utc));
                return (entry.Episode, PlayedAtUtc: playedAtUtc);
            })
            .OrderByDescending(entry => entry.PlayedAtUtc)
            .Take(latestCount);
    }

    private (AtProtoRecord<PopfeedReviewRecord> WrongReview, Episode Episode, DateTimeOffset PlayedAtUtc)[] BuildLatestRepairPlan(
        Guid jellyfinUserId,
        (AtProtoRecord<PopfeedReviewRecord> Record, DateTimeOffset CreatedAt)[] wrongReviews)
    {
        var user = _userManager.GetUserById(jellyfinUserId);
        if (user is null)
        {
            return Array.Empty<(AtProtoRecord<PopfeedReviewRecord> WrongReview, Episode Episode, DateTimeOffset PlayedAtUtc)>();
        }

        var query = new InternalItemsQuery(user)
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            EnableTotalRecordCount = false,
        };

        var playedEpisodes = _libraryManager
            .GetItemList(query)
            .OfType<Episode>()
            .Select(episode => new
            {
                Episode = episode,
                UserData = _userDataManager.GetUserData(user, episode),
            })
            .Where(entry => entry.UserData is not null
                && entry.UserData.Played
                && entry.UserData.LastPlayedDate.HasValue)
            .Select(entry =>
            {
                var playedAtUtc = new DateTimeOffset(
                    DateTime.SpecifyKind(entry.UserData!.LastPlayedDate!.Value, DateTimeKind.Utc));
                return (entry.Episode, PlayedAtUtc: playedAtUtc);
            })
            .ToList();

        var plan = new System.Collections.Generic.List<(AtProtoRecord<PopfeedReviewRecord> WrongReview, Episode Episode, DateTimeOffset PlayedAtUtc)>();
        foreach (var wrongReview in wrongReviews)
        {
            var reviewRecord = wrongReview.Record.Value;
            var bestMatch = playedEpisodes
                .Where(candidate => EpisodeMatchesReview(candidate.Episode, reviewRecord))
                .OrderBy(candidate => Math.Abs((candidate.PlayedAtUtc - wrongReview.CreatedAt).TotalSeconds))
                .FirstOrDefault();

            if (bestMatch.Episode is null)
            {
                continue;
            }

            plan.Add((wrongReview.Record, bestMatch.Episode, bestMatch.PlayedAtUtc));
            _ = playedEpisodes.Remove(bestMatch);
        }

        return plan.ToArray();
    }

    private static bool EpisodeMatchesReview(Episode episode, PopfeedReviewRecord review)
    {
        var identifiers = review.Identifiers;
        if (identifiers.SeasonNumber.HasValue && episode.ParentIndexNumber != identifiers.SeasonNumber.Value)
        {
            return false;
        }

        if (identifiers.EpisodeNumber.HasValue && episode.IndexNumber != identifiers.EpisodeNumber.Value)
        {
            return false;
        }

        var seriesTmdb = GetProviderId(episode.Series, MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
        var episodeTmdb = GetProviderId(episode, MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
        var reviewSeriesId = !string.IsNullOrWhiteSpace(identifiers.TmdbTvSeriesId)
            ? identifiers.TmdbTvSeriesId
            : (identifiers.SeasonNumber.HasValue && identifiers.EpisodeNumber.HasValue ? identifiers.TmdbId : null);

        if (!string.IsNullOrWhiteSpace(reviewSeriesId))
        {
            if (string.Equals(seriesTmdb, reviewSeriesId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(episodeTmdb, reviewSeriesId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        return !string.IsNullOrWhiteSpace(identifiers.TmdbId)
            && string.Equals(episodeTmdb, identifiers.TmdbId, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetProviderId(IHasProviderIds? item, MediaBrowser.Model.Entities.MetadataProvider provider)
    {
        if (item is null)
        {
            return null;
        }

        return item.TryGetProviderId(provider, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static bool IsEpisodeReview(PopfeedReviewRecord record)
    {
        return string.Equals(record.CreativeWorkType, "episode", StringComparison.OrdinalIgnoreCase)
            || string.Equals(record.CreativeWorkType, "tv_episode", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? ParseRecordTimestamp(string? createdAt)
    {
        if (string.IsNullOrWhiteSpace(createdAt))
        {
            return null;
        }

        return DateTimeOffset.TryParse(createdAt, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
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

    private static bool IsRateLimitError(Exception ex)
    {
        if (ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ex.Message.Contains("too many requests", StringComparison.OrdinalIgnoreCase);
    }
}
