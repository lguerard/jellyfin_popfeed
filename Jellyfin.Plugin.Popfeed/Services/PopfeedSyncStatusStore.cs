using System;
using System.Linq;
using Jellyfin.Plugin.Popfeed.Models;

namespace Jellyfin.Plugin.Popfeed.Services;

/// <summary>
/// Stores recent Popfeed sync outcomes for the current server session.
/// </summary>
public sealed class PopfeedSyncStatusStore
{
    private const int MaxEntries = 50;
    private readonly object _syncRoot = new();
    private readonly System.Collections.Generic.List<PopfeedSyncTestResult> _entries = [];

    /// <summary>
    /// Adds a sync status entry.
    /// </summary>
    /// <param name="entry">The entry to add.</param>
    public void Add(PopfeedSyncTestResult entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_syncRoot)
        {
            _entries.Insert(0, Clone(entry));
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            }
        }
    }

    /// <summary>
    /// Gets recent sync status entries.
    /// </summary>
    /// <param name="jellyfinUserId">Optional Jellyfin user filter.</param>
    /// <param name="limit">Maximum number of entries.</param>
    /// <returns>The matching entries.</returns>
    public PopfeedSyncTestResult[] GetRecent(Guid? jellyfinUserId, int limit)
    {
        var effectiveLimit = Math.Clamp(limit, 1, MaxEntries);

        lock (_syncRoot)
        {
            return _entries
                .Where(entry => !jellyfinUserId.HasValue || entry.UserId == jellyfinUserId.Value)
                .Take(effectiveLimit)
                .Select(Clone)
                .ToArray();
        }
    }

    private static PopfeedSyncTestResult Clone(PopfeedSyncTestResult entry)
    {
        return new PopfeedSyncTestResult
        {
            Success = entry.Success,
            WouldSync = entry.WouldSync,
            Executed = entry.Executed,
            Message = entry.Message,
            UserId = entry.UserId,
            UserName = entry.UserName,
            ItemId = entry.ItemId,
            ItemName = entry.ItemName,
            Played = entry.Played,
            TriggerSource = entry.TriggerSource,
            TimestampUtc = entry.TimestampUtc,
            ProviderName = entry.ProviderName,
            PopfeedIdentifier = entry.PopfeedIdentifier,
            CreativeWorkType = entry.CreativeWorkType,
            Identifiers = entry.Identifiers,
        };
    }
}