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
}