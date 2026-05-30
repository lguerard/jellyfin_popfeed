using System;

namespace Jellyfin.Plugin.Popfeed.Models;

/// <summary>
/// Jellyfin user details for the settings page.
/// </summary>
public sealed class PopfeedJellyfinUserDto
{
    /// <summary>
    /// Gets or sets the user id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the user name.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Search result for library items.
/// </summary>
public sealed class PopfeedSearchItemDto
{
    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the item name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item type.
    /// </summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the parent series id when the item is an episode.
    /// </summary>
    public Guid? SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the parent series name when the item is an episode.
    /// </summary>
    public string? SeriesName { get; set; }
}

/// <summary>
/// Test sync request body.
/// </summary>
public sealed class PopfeedTestSyncRequest
{
    /// <summary>
    /// Gets or sets the Jellyfin user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the item should be treated as watched.
    /// </summary>
    public bool Played { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the test should avoid remote writes.
    /// </summary>
    public bool DryRun { get; set; } = true;
}

/// <summary>
/// Request payload for repairing incorrect recent TV episode activities.
/// </summary>
public sealed class PopfeedRepairEpisodesRequest
{
    /// <summary>
    /// Gets or sets the Jellyfin user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the number of latest episode activities to repair.
    /// When greater than zero, marker-title mode is ignored and only the
    /// latest N episode matches are repaired.
    /// </summary>
    public int LatestMatchesCount { get; set; }

    /// <summary>
    /// Gets or sets the marker title. Episode activities newer than the latest
    /// matching review are considered for repair.
    /// </summary>
    public string MarkerTitle { get; set; } = "Project Hail Mary";

    /// <summary>
    /// Gets or sets the maximum number of watched episodes to replay.
    /// </summary>
    public int MaxReplayItems { get; set; } = 200;

    /// <summary>
    /// Gets or sets a value indicating whether to only simulate changes.
    /// </summary>
    public bool DryRun { get; set; } = false;
}

/// <summary>
/// Result payload for the episode repair endpoint.
/// </summary>
public sealed class PopfeedRepairEpisodesResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the operation completed.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a human-readable message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the marker timestamp used for the repair window.
    /// </summary>
    public DateTimeOffset? MarkerTimestampUtc { get; set; }

    /// <summary>
    /// Gets or sets the number of episode review records identified as wrong.
    /// </summary>
    public int WrongReviewCount { get; set; }

    /// <summary>
    /// Gets or sets the number of wrong review records deleted.
    /// </summary>
    public int DeletedWrongReviews { get; set; }

    /// <summary>
    /// Gets or sets the number of watched episodes selected for replay.
    /// </summary>
    public int ReplayCandidateCount { get; set; }

    /// <summary>
    /// Gets or sets the number of replay syncs attempted.
    /// </summary>
    public int ReplayAttempted { get; set; }

    /// <summary>
    /// Gets or sets the number of replay syncs that succeeded.
    /// </summary>
    public int ReplaySucceeded { get; set; }
}

/// <summary>
/// Test sync result returned to the settings page.
/// </summary>
public sealed class PopfeedSyncTestResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the test completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the sync would proceed.
    /// </summary>
    public bool WouldSync { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the remote sync actually ran.
    /// </summary>
    public bool Executed { get; set; }

    /// <summary>
    /// Gets or sets the result message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin user name when available.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item name.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the item was treated as watched.
    /// </summary>
    public bool Played { get; set; }

    /// <summary>
    /// Gets or sets the origin of the sync request.
    /// </summary>
    public string TriggerSource { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp when the status entry was created.
    /// </summary>
    public DateTimeOffset TimestampUtc { get; set; }

    /// <summary>
    /// Gets or sets the strategy provider name.
    /// </summary>
    public string? ProviderName { get; set; }

    /// <summary>
    /// Gets or sets the mapped Popfeed account identifier.
    /// </summary>
    public string? PopfeedIdentifier { get; set; }

    /// <summary>
    /// Gets or sets the mapped creative work type.
    /// </summary>
    public string? CreativeWorkType { get; set; }

    /// <summary>
    /// Gets or sets the mapped identifiers.
    /// </summary>
    public PopfeedIdentifiers? Identifiers { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the sync created a native Popfeed activity record.
    /// </summary>
    public bool CreatedPopfeedActivity { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the sync also created a Bluesky activity post.
    /// </summary>
    public bool PostedToBluesky { get; set; }
}