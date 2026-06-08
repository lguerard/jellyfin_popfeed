using System;
using Jellyfin.Plugin.Popfeed.Models;

namespace Jellyfin.Plugin.Popfeed.Services;

/// <summary>
/// Builds deterministic ATProto record keys for Popfeed records.
///
/// Deterministic rkeys replace random TIDs so that putRecord acts as an
/// upsert: a second sync for the same item updates the existing record in
/// place without creating duplicates, and without requiring a full list
/// scan to locate the previous entry.
///
/// Scheme (dots are allowed in ATProto rkeys):
///   w.ep.{seriesId}.{S}.{E}   — watched-list episode entry
///   w.mv.{tmdbId}              — watched-list movie entry
///   w.tv.{seriesId}            — tv_show progress record (WatchedEpisodes)
///   w.ts.{seriesId}.{S}        — tv_season completed-season marker
///   r.ep.{seriesId}.{S}.{E}   — recent-list episode entry
///   r.mv.{tmdbId}              — recent-list movie entry
///   rv.ep.{seriesId}.{S}.{E}  — review record for an episode
///   rv.mv.{tmdbId}             — review record for a movie
/// </summary>
internal static class PopfeedRkeyBuilder
{
    /// <summary>
    /// Returns the rkey for a watched-list item (episode or movie).
    /// </summary>
    public static string ForWatchedItem(PopfeedMappedItem item)
    {
        return item.CreativeWorkType switch
        {
            "movie" when !string.IsNullOrWhiteSpace(item.Identifiers.TmdbId)
                => $"w.mv.{item.Identifiers.TmdbId}",
            "episode" or "tv_episode"
                when !string.IsNullOrWhiteSpace(item.Identifiers.TmdbTvSeriesId)
                && item.Identifiers.SeasonNumber.HasValue
                && item.Identifiers.EpisodeNumber.HasValue
                => $"w.ep.{item.Identifiers.TmdbTvSeriesId}.{item.Identifiers.SeasonNumber.Value}.{item.Identifiers.EpisodeNumber.Value}",
            _ => throw new ArgumentException($"Cannot compute watched rkey for creativeWorkType '{item.CreativeWorkType}' with available identifiers."),
        };
    }

    /// <summary>
    /// Returns the rkey for a recent-list item (episode or movie).
    /// </summary>
    public static string ForRecentItem(PopfeedMappedItem item)
    {
        return item.CreativeWorkType switch
        {
            "movie" when !string.IsNullOrWhiteSpace(item.Identifiers.TmdbId)
                => $"r.mv.{item.Identifiers.TmdbId}",
            "episode" or "tv_episode"
                when !string.IsNullOrWhiteSpace(item.Identifiers.TmdbTvSeriesId)
                && item.Identifiers.SeasonNumber.HasValue
                && item.Identifiers.EpisodeNumber.HasValue
                => $"r.ep.{item.Identifiers.TmdbTvSeriesId}.{item.Identifiers.SeasonNumber.Value}.{item.Identifiers.EpisodeNumber.Value}",
            _ => throw new ArgumentException($"Cannot compute recent rkey for creativeWorkType '{item.CreativeWorkType}' with available identifiers."),
        };
    }

    /// <summary>
    /// Returns the rkey for a review/activity record (episode or movie).
    /// </summary>
    public static string ForReview(PopfeedMappedItem item)
    {
        return item.CreativeWorkType switch
        {
            "movie" when !string.IsNullOrWhiteSpace(item.Identifiers.TmdbId)
                => $"rv.mv.{item.Identifiers.TmdbId}",
            "episode" or "tv_episode"
                when !string.IsNullOrWhiteSpace(item.Identifiers.TmdbTvSeriesId)
                && item.Identifiers.SeasonNumber.HasValue
                && item.Identifiers.EpisodeNumber.HasValue
                => $"rv.ep.{item.Identifiers.TmdbTvSeriesId}.{item.Identifiers.SeasonNumber.Value}.{item.Identifiers.EpisodeNumber.Value}",
            _ => throw new ArgumentException($"Cannot compute review rkey for creativeWorkType '{item.CreativeWorkType}' with available identifiers."),
        };
    }

    /// <summary>
    /// Returns the rkey for the tv_show progress record (holds WatchedEpisodes).
    /// </summary>
    public static string ForTvShowProgress(string seriesId)
        => $"w.tv.{seriesId}";

    /// <summary>
    /// Returns the rkey for a completed tv_season marker.
    /// </summary>
    public static string ForTvSeason(string seriesId, int season)
        => $"w.ts.{seriesId}.{season}";
}
