using System;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Popfeed.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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
    private readonly IUserManager _userManager;
    private readonly Services.PopfeedSyncService _syncService;
    private readonly Services.PopfeedSyncStatusStore _statusStore;
    private readonly ILogger<PopfeedController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PopfeedController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="syncService">The sync service.</param>
    /// <param name="logger">The logger.</param>
    public PopfeedController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        Services.PopfeedSyncService syncService,
        Services.PopfeedSyncStatusStore statusStore,
        ILogger<PopfeedController> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
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
        var users = _userManager.Users
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
        var userNames = _userManager.Users.ToDictionary(user => user.Id, user => user.Username);

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
}