using Jellyfin.Plugin.Popfeed.Models;
using Jellyfin.Plugin.Popfeed.Services;

using Xunit;

namespace Jellyfin.Plugin.Popfeed.Tests;

/// <summary>
/// Unit tests covering Popfeed identifier matching, list-item update detection,
/// episode identifier normalisation, and URL generation.
/// </summary>
public sealed class PopfeedEpisodeIdentifierTests
{
    /// <summary>
    /// Two identical movie identifier sets must be considered equal by both
    /// <see cref="PopfeedIdentifiers.HasSameValues"/> and <see cref="PopfeedIdentifiers.Matches"/>.
    /// </summary>
    [Fact]
    public void HasSameValues_TreatsMatchingMovieIdentifiersAsEqual()
    {
        var existing = new PopfeedIdentifiers
        {
            ImdbId = "tt9813792",
            TmdbId = "502356",
        };

        var desired = new PopfeedIdentifiers
        {
            ImdbId = "tt9813792",
            TmdbId = "502356",
        };

        Assert.True(existing.HasSameValues(desired));
        Assert.True(existing.Matches(desired));
    }

    /// <summary>
    /// An unchanged movie watched-list record must not trigger an unnecessary PDS write.
    /// </summary>
    [Fact]
    public void NeedsListItemUpdate_DoesNotTriggerForUnchangedMovieIdentifiers()
    {
        var existing = new PopfeedListItemRecord
        {
            Identifiers = new PopfeedIdentifiers
            {
                ImdbId = "tt9813792",
                TmdbId = "502356",
            },
            CreativeWorkType = "movie",
            Status = PopfeedListItemRecord.FinishedStatus,
            CompletedAt = "2026-05-12T08:00:00.000Z",
            Title = "Tetris",
        };

        var mappedItem = new PopfeedMappedItem(
            "movie",
            new PopfeedIdentifiers
            {
                ImdbId = "tt9813792",
                TmdbId = "502356",
            });

        var needsUpdate = PopfeedWatchedListWriter.NeedsListItemUpdate(
            existing,
            mappedItem,
            PopfeedListItemRecord.FinishedStatus,
            "Tetris",
            played: true);

        Assert.False(needsUpdate);
    }

    /// <summary>
    /// Legacy (TmdbId) and canonical (TmdbTvSeriesId) episode identifier shapes
    /// must match each other for deduplication purposes.
    /// </summary>
    [Fact]
    public void Matches_TreatsLegacyAndCanonicalEpisodeIdentifiersAsEquivalent()
    {
        var legacy = new PopfeedIdentifiers
        {
            TmdbId = "279471",
            SeasonNumber = 1,
            EpisodeNumber = 4,
        };

        var canonical = new PopfeedIdentifiers
        {
            TmdbTvSeriesId = "279471",
            SeasonNumber = 1,
            EpisodeNumber = 4,
        };

        Assert.True(legacy.Matches(canonical));
        Assert.True(canonical.Matches(legacy));
    }

    /// <summary>
    /// Legacy and canonical episode identifier shapes must NOT report equal serialised values
    /// so that an existing legacy record is detected and rewritten to canonical form.
    /// </summary>
    [Fact]
    public void HasSameValues_DetectsLegacyEpisodeIdentifierShape()
    {
        var legacy = new PopfeedIdentifiers
        {
            TmdbId = "279471",
            SeasonNumber = 1,
            EpisodeNumber = 4,
        };

        var canonical = new PopfeedIdentifiers
        {
            TmdbTvSeriesId = "279471",
            SeasonNumber = 1,
            EpisodeNumber = 4,
        };

        Assert.False(legacy.HasSameValues(canonical));
        Assert.False(canonical.HasSameValues(legacy));
    }

    /// <summary>
    /// A watched-list item stored with legacy identifiers must be flagged for update
    /// so its identifiers are rewritten to canonical form on the next sync.
    /// </summary>
    [Fact]
    public void NeedsListItemUpdate_WhenExistingEpisodeUsesLegacyIdentifiers()
    {
        var existing = new PopfeedListItemRecord
        {
            Identifiers = new PopfeedIdentifiers
            {
                TmdbId = "279471",
                SeasonNumber = 1,
                EpisodeNumber = 4,
            },
            CreativeWorkType = "tv_episode",
            Status = PopfeedListItemRecord.FinishedStatus,
            CompletedAt = "2026-05-12T08:00:00.000Z",
            Title = "Life's Still Unfair",
        };

        var mappedItem = new PopfeedMappedItem(
            "episode",
            new PopfeedIdentifiers
            {
                TmdbTvSeriesId = "279471",
                SeasonNumber = 1,
                EpisodeNumber = 4,
            });

        var needsUpdate = PopfeedWatchedListWriter.NeedsListItemUpdate(
            existing,
            mappedItem,
            PopfeedListItemRecord.FinishedStatus,
            "Life's Still Unfair",
            played: true);

        Assert.True(needsUpdate);
    }

    /// <summary>
    /// When a review record carries legacy episode identifiers, the merge result must
    /// carry canonical identifiers and trigger a PDS update.
    /// </summary>
    [Fact]
    public void NeedsReviewUpdate_WhenMergedReviewRewritesLegacyEpisodeIdentifiers()
    {
        var existing = new PopfeedReviewRecord
        {
            Title = "Malcolm in the Middle - Life's Still Unfair",
            Text = "Watched Malcolm in the Middle S01E04.",
            CreativeWorkType = "tv_episode",
            Identifiers = new PopfeedIdentifiers
            {
                TmdbId = "279471",
                SeasonNumber = 1,
                EpisodeNumber = 4,
            },
            PosterUrl = "https://cdn.example/poster.jpg",
            Tags = ["jellyfin", "watched"],
        };

        var desired = new PopfeedReviewRecord
        {
            Title = existing.Title,
            Text = existing.Text,
            CreativeWorkType = "episode",
            Identifiers = new PopfeedIdentifiers
            {
                TmdbTvSeriesId = "279471",
                SeasonNumber = 1,
                EpisodeNumber = 4,
            },
            PosterUrl = existing.PosterUrl,
            Tags = ["jellyfin", "watched"],
        };

        var merged = PopfeedWatchedListWriter.MergeExistingReview(existing, desired);

        Assert.True(PopfeedWatchedListWriter.NeedsReviewUpdate(existing, merged));
        Assert.True(merged.Identifiers.HasSameValues(desired.Identifiers));
    }

    /// <summary>
    /// A movie item with a TMDb id must produce the canonical
    /// <c>https://popfeed.social/movie/{tmdbId}</c> URL.
    /// </summary>
    [Fact]
    public void BuildItemUrl_UsesMoviePath()
    {
        var itemUrl = PopfeedItemUrlBuilder.BuildItemUrl(
            new PopfeedMappedItem(
                "movie",
                new PopfeedIdentifiers
                {
                    TmdbId = "502356",
                }));

        Assert.Equal(
            "https://popfeed.social/movie/502356",
            itemUrl);
    }

    /// <summary>
    /// An episode item with canonical series coordinates must use the query route.
    /// </summary>
    [Fact]
    public void BuildItemUrl_PrefersCanonicalEpisodeQueryPath()
    {
        var itemUrl = PopfeedItemUrlBuilder.BuildItemUrl(
            new PopfeedMappedItem(
                "tv_episode",
                new PopfeedIdentifiers
                {
                    TmdbId = "7239719",
                    TmdbTvSeriesId = "67997",
                    SeasonNumber = 33,
                    EpisodeNumber = 22,
                }));

        Assert.Equal(
            "https://popfeed.social/episode?tvId=67997&seasonNumber=33&episodeNumber=22",
            itemUrl);
    }

    /// <summary>
    /// Ted Lasso season 3 episode 4 must resolve to the canonical Popfeed episode URL.
    /// This mirrors the exact URL that both watched-list sync and Bluesky posting use.
    /// </summary>
    [Fact]
    public void BuildItemUrl_UsesTedLassoSeason3Episode4CanonicalPath()
    {
        var itemUrl = PopfeedItemUrlBuilder.BuildItemUrl(
            new PopfeedMappedItem(
                "tv_episode",
                new PopfeedIdentifiers
                {
                    TmdbId = "6617364",
                    TmdbTvSeriesId = "97546",
                    SeasonNumber = 3,
                    EpisodeNumber = 4,
                }));

        Assert.Equal(
            "https://popfeed.social/episode?tvId=97546&seasonNumber=3&episodeNumber=4",
            itemUrl);
    }

    /// <summary>
    /// When only canonical tv-series coordinates are available, URL building must
    /// still fall back to the query-string episode route.
    /// </summary>
    [Fact]
    public void BuildItemUrl_FallsBackToCanonicalQueryWhenEpisodeTmdbIdMissing()
    {
        var itemUrl = PopfeedItemUrlBuilder.BuildItemUrl(
            new PopfeedMappedItem(
                "tv_episode",
                new PopfeedIdentifiers
                {
                    TmdbTvSeriesId = "67997",
                    SeasonNumber = 33,
                    EpisodeNumber = 22,
                }));

        Assert.Equal(
            "https://popfeed.social/episode?tvId=67997&seasonNumber=33&episodeNumber=22",
            itemUrl);
    }

    /// <summary>
    /// When only the episode TMDb id exists, URL building must use /episode/{id}
    /// and must never reinterpret that id as a tv-series id.
    /// </summary>
    [Fact]
    public void BuildItemUrl_UsesEpisodePathWhenOnlyEpisodeTmdbIdIsPresent()
    {
        var itemUrl = PopfeedItemUrlBuilder.BuildItemUrl(
            new PopfeedMappedItem(
                "tv_episode",
                new PopfeedIdentifiers
                {
                    TmdbId = "4556",
                }));

        Assert.Equal("https://popfeed.social/episode/4556", itemUrl);
    }

    /// <summary>
    /// A mapped episode that already includes canonical tv-series coordinates must
    /// keep those values unchanged after normalisation.
    /// </summary>
    [Fact]
    public void NormalizeMappedItem_LeavesCanonicalEpisodeShapeIntact()
    {
        var normalized = PopfeedItemUrlBuilder.NormalizeMappedItem(
            new PopfeedMappedItem(
                "tv_episode",
                new PopfeedIdentifiers
                {
                    TmdbId = "7239719",
                    TmdbTvSeriesId = "67997",
                    SeasonNumber = 1,
                    EpisodeNumber = 1,
                }));

        Assert.Equal("episode", normalized.CreativeWorkType);
        Assert.Equal("67997", normalized.Identifiers.TmdbTvSeriesId);
        Assert.Null(normalized.Identifiers.TmdbId);
        Assert.Equal(1, normalized.Identifiers.SeasonNumber);
        Assert.Equal(1, normalized.Identifiers.EpisodeNumber);
    }

    /// <summary>
    /// Normalisation must preserve episode-level identifiers while URL building still
    /// prefers canonical series coordinates when present.
    /// </summary>
    [Fact]
    public void NormalizeAndBuildItemUrl_PrefersCanonicalQueryWhenSeriesCoordinatesExist()
    {
        var mapped = new PopfeedMappedItem(
            "tv_episode",
            new PopfeedIdentifiers
            {
                TmdbId = "7239719",
                TmdbTvSeriesId = "67997",
                SeasonNumber = 33,
                EpisodeNumber = 19,
            });

        var normalized = PopfeedItemUrlBuilder.NormalizeMappedItem(mapped);
        var itemUrl = PopfeedItemUrlBuilder.BuildItemUrl(mapped);

        Assert.Null(normalized.Identifiers.TmdbId);
        Assert.Equal("67997", normalized.Identifiers.TmdbTvSeriesId);
        Assert.Equal(
            "https://popfeed.social/episode?tvId=67997&seasonNumber=33&episodeNumber=19",
            itemUrl);
    }

    /// <summary>
    /// Legacy episode-shaped identifiers that stored series id in TmdbId must be
    /// promoted to canonical series coordinates so links stop using /episode/{id}.
    /// </summary>
    [Fact]
    public void NormalizeMappedItem_PromotesLegacyEpisodeSeriesShape()
    {
        var normalized = PopfeedItemUrlBuilder.NormalizeMappedItem(
            new PopfeedMappedItem(
                "tv_episode",
                new PopfeedIdentifiers
                {
                    TmdbId = "4556",
                    SeasonNumber = 1,
                    EpisodeNumber = 8,
                }));

        Assert.Equal("episode", normalized.CreativeWorkType);
        Assert.Equal("4556", normalized.Identifiers.TmdbTvSeriesId);
        Assert.Null(normalized.Identifiers.TmdbId);
        Assert.Equal(1, normalized.Identifiers.SeasonNumber);
        Assert.Equal(8, normalized.Identifiers.EpisodeNumber);
    }

    /// <summary>
    /// Legacy episode-shaped identifiers with season/episode coordinates should
    /// be promoted to canonical series coordinates to avoid /episode/{id} links.
    /// </summary>
    [Fact]
    public void NormalizeMappedItem_PromotesLegacyEpisodeShapeToSeriesCoordinates()
    {
        var normalized = PopfeedItemUrlBuilder.NormalizeMappedItem(
            new PopfeedMappedItem(
                "tv_episode",
                new PopfeedIdentifiers
                {
                    TmdbId = "4556",
                    SeasonNumber = 1,
                    EpisodeNumber = 1,
                }));

        Assert.Equal("episode", normalized.CreativeWorkType);
        Assert.Equal("4556", normalized.Identifiers.TmdbTvSeriesId);
        Assert.Null(normalized.Identifiers.TmdbId);
        Assert.Equal(1, normalized.Identifiers.SeasonNumber);
        Assert.Equal(1, normalized.Identifiers.EpisodeNumber);
    }

    /// <summary>
    /// When a new poster blob is available, merge must prefer it so stale images
    /// are replaced on subsequent syncs.
    /// </summary>
    [Fact]
    public void MergeExistingReview_PrefersDesiredPosterWhenPresent()
    {
        var existing = new PopfeedReviewRecord
        {
            Poster = new AtProtoBlob
            {
                Ref = new AtProtoLink { Link = "oldCid" },
                MimeType = "image/jpeg",
            },
            PosterUrl = "https://cdn.bsky.app/img/feed_fullsize/plain/did:plc:test/oldCid@jpeg",
            Identifiers = new PopfeedIdentifiers { TmdbId = "1" },
            CreativeWorkType = "movie",
            Tags = ["jellyfin", "watched"],
        };

        var desired = new PopfeedReviewRecord
        {
            Poster = new AtProtoBlob
            {
                Ref = new AtProtoLink { Link = "newCid" },
                MimeType = "image/jpeg",
            },
            PosterUrl = "https://cdn.bsky.app/img/feed_fullsize/plain/did:plc:test/newCid@jpeg",
            Identifiers = new PopfeedIdentifiers { TmdbId = "1" },
            CreativeWorkType = "movie",
            Tags = ["jellyfin", "watched"],
        };

        var merged = PopfeedWatchedListWriter.MergeExistingReview(existing, desired);

        Assert.Equal("newCid", merged.Poster?.Ref.Link);
        Assert.Equal(desired.PosterUrl, merged.PosterUrl);
    }

    /// <summary>
    /// A legacy season shape (TmdbId instead of TmdbTvSeriesId) must be rewritten
    /// to canonical form by <see cref="PopfeedItemUrlBuilder.NormalizeMappedItem"/>.
    /// </summary>
    [Fact]
    public void NormalizeMappedItem_RewritesLegacySeasonShape()
    {
        var normalized = PopfeedItemUrlBuilder.NormalizeMappedItem(
            new PopfeedMappedItem(
                "tv_season",
                new PopfeedIdentifiers
                {
                    TmdbId = "4556",
                    SeasonNumber = 1,
                }));

        Assert.Equal("tv_season", normalized.CreativeWorkType);
        Assert.Equal("4556", normalized.Identifiers.TmdbTvSeriesId);
        Assert.Null(normalized.Identifiers.TmdbId);
        Assert.Equal(1, normalized.Identifiers.SeasonNumber);
    }
}