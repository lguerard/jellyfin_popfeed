using System;
using Jellyfin.Plugin.Popfeed.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Popfeed.Services;

/// <summary>
/// Normalizes mapped items into canonical Popfeed URL-compatible identifier shapes.
/// </summary>
internal static class PopfeedItemUrlBuilder
{
    /// <summary>
    /// Normalizes mapped identifiers so Popfeed resolves episode/season items by
    /// canonical tv-series coordinates.
    /// </summary>
    /// <param name="mappedItem">The mapped item.</param>
    /// <param name="sourceItem">Optional Jellyfin source item to fill missing indexes.</param>
    /// <returns>The normalized mapped item.</returns>
    public static PopfeedMappedItem NormalizeMappedItem(PopfeedMappedItem mappedItem, BaseItem? sourceItem = null)
    {
        ArgumentNullException.ThrowIfNull(mappedItem);

        var identifiers = mappedItem.Identifiers;

        if (mappedItem.CreativeWorkType == "tv_episode")
        {
            var episode = sourceItem as Episode;
            var tvSeriesId = FirstNonEmpty(
                identifiers.TmdbTvSeriesId,
                GetProviderId(episode?.Series, MetadataProvider.Tmdb));
            var seasonNumber = identifiers.SeasonNumber ?? episode?.ParentIndexNumber;
            var episodeNumber = identifiers.EpisodeNumber ?? episode?.IndexNumber;

            if (!string.IsNullOrWhiteSpace(tvSeriesId)
                && seasonNumber.HasValue
                && episodeNumber.HasValue)
            {
                return new PopfeedMappedItem(
                    "tv_episode",
                    new PopfeedIdentifiers
                    {
                        ImdbId = identifiers.ImdbId,
                        // Keep canonical tv-episode coordinates only. Some Popfeed
                        // surfaces prioritize TmdbId when present and can route to
                        // legacy /episode/{id} links instead of season/episode URLs.
                        TmdbId = null,
                        TmdbTvSeriesId = tvSeriesId,
                        SeasonNumber = seasonNumber,
                        EpisodeNumber = episodeNumber,
                    });
            }

            if (!string.IsNullOrWhiteSpace(identifiers.TmdbId))
            {
                return new PopfeedMappedItem(
                    "tv_episode",
                    new PopfeedIdentifiers
                    {
                        ImdbId = identifiers.ImdbId,
                        // Canonical-only rule: when series coordinates are unavailable,
                        // keep this as an episode-id fallback and drop season/episode
                        // to avoid persisting legacy series-shaped identifiers.
                        TmdbId = identifiers.TmdbId,
                        TmdbTvSeriesId = null,
                        SeasonNumber = null,
                        EpisodeNumber = null,
                    });
            }
        }

        if (mappedItem.CreativeWorkType == "tv_season")
        {
            var season = sourceItem as Season;
            var tvSeriesId = FirstNonEmpty(identifiers.TmdbTvSeriesId, identifiers.TmdbId);
            var seasonNumber = identifiers.SeasonNumber ?? season?.IndexNumber;

            if (!string.IsNullOrWhiteSpace(tvSeriesId)
                && seasonNumber.HasValue)
            {
                return new PopfeedMappedItem(
                    mappedItem.CreativeWorkType,
                    new PopfeedIdentifiers
                    {
                        ImdbId = identifiers.ImdbId,
                        TmdbTvSeriesId = tvSeriesId,
                        TmdbId = null,
                        SeasonNumber = seasonNumber,
                        EpisodeNumber = identifiers.EpisodeNumber,
                    });
            }
        }

        return mappedItem;
    }

    /// <summary>
    /// Builds the public Popfeed URL for a mapped item.
    /// </summary>
    /// <param name="mappedItem">The mapped item.</param>
    /// <param name="sourceItem">Optional Jellyfin source item to fill missing indexes.</param>
    /// <returns>The URL when sufficient identifiers are available; otherwise null.</returns>
    public static string? BuildItemUrl(PopfeedMappedItem mappedItem, BaseItem? sourceItem = null)
    {
        var normalized = NormalizeMappedItem(mappedItem, sourceItem);
        var identifiers = normalized.Identifiers;

        return normalized.CreativeWorkType switch
        {
            "movie" when !string.IsNullOrWhiteSpace(identifiers.TmdbId)
                => $"https://popfeed.social/movie/{Uri.EscapeDataString(identifiers.TmdbId)}",
            "episode" or "tv_episode" when !string.IsNullOrWhiteSpace(identifiers.TmdbTvSeriesId)
                && identifiers.SeasonNumber.HasValue
                && identifiers.EpisodeNumber.HasValue
                => $"https://popfeed.social/episode?tvId={Uri.EscapeDataString(identifiers.TmdbTvSeriesId)}&seasonNumber={identifiers.SeasonNumber.Value}&episodeNumber={identifiers.EpisodeNumber.Value}",
            "episode" or "tv_episode" when !string.IsNullOrWhiteSpace(identifiers.TmdbId)
                => $"https://popfeed.social/episode/{Uri.EscapeDataString(identifiers.TmdbId)}",
            "tv_season" when !string.IsNullOrWhiteSpace(identifiers.TmdbTvSeriesId)
                && identifiers.SeasonNumber.HasValue
                => $"https://popfeed.social/season?tvId={Uri.EscapeDataString(identifiers.TmdbTvSeriesId)}&seasonNumber={identifiers.SeasonNumber.Value}",
            _ => null,
        };
    }

    /// <summary>
    /// Returns <paramref name="preferred"/> when it is non-empty,
    /// otherwise returns <paramref name="fallback"/> (or null when both are empty).
    /// Used to promote a legacy <c>TmdbId</c> value when <c>TmdbTvSeriesId</c>
    /// is absent.
    /// </summary>
    /// <param name="preferred">The preferred value (e.g. TmdbTvSeriesId).</param>
    /// <param name="fallback">The fallback value (e.g. TmdbId).</param>
    /// <returns>The first non-empty string, or null.</returns>
    private static string? FirstNonEmpty(string? preferred, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred;
        }

        return string.IsNullOrWhiteSpace(fallback)
            ? null
            : fallback;
    }

    private static string? GetProviderId(IHasProviderIds? item, MetadataProvider provider)
    {
        if (item is null)
        {
            return null;
        }

        return item.TryGetProviderId(provider, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }
}