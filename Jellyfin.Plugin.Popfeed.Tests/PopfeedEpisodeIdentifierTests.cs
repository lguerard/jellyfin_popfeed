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
    /// Merging a re-synced review must keep the original creation timestamp and
    /// cross-posts while taking the freshly built title, text, and identifiers.
    /// </summary>
    [Fact]
    public void MergeExistingReview_PreservesCreatedAtAndCrossPostsAndUnionsTags()
    {
        var existing = new PopfeedReviewRecord
        {
            Title = "Old Title",
            Text = "Old text.",
            CreativeWorkType = "episode",
            Identifiers = new PopfeedIdentifiers
            {
                TmdbTvSeriesId = "279471",
                SeasonNumber = 1,
                EpisodeNumber = 4,
            },
            CreatedAt = "2026-01-01T00:00:00.000Z",
            Tags = ["jellyfin", "watched"],
            CrossPosts = new PopfeedReviewCrossPosts { Bluesky = "at://did:plc:test/app.bsky.feed.post/abc" },
        };

        var desired = new PopfeedReviewRecord
        {
            Title = "New Title",
            Text = "New text.",
            CreativeWorkType = "episode",
            Identifiers = existing.Identifiers,
            CreatedAt = "2026-06-08T00:00:00.000Z",
            Tags = ["jellyfin", "rewatch"],
        };

        var merged = PopfeedWatchedListWriter.MergeExistingReview(existing, desired);

        Assert.Equal("New Title", merged.Title);
        Assert.Equal("New text.", merged.Text);
        Assert.Equal("2026-01-01T00:00:00.000Z", merged.CreatedAt);
        Assert.Equal("at://did:plc:test/app.bsky.feed.post/abc", merged.CrossPosts?.Bluesky);
        Assert.Contains("watched", merged.Tags);
        Assert.Contains("rewatch", merged.Tags);
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
    /// When canonical tv-series coordinates are missing, URL building must return null
    /// for episodes instead of falling back to standalone episode-id routes.
    /// </summary>
    [Fact]
    public void BuildItemUrl_ReturnsNullWhenOnlyStandaloneEpisodeTmdbIdIsPresent()
    {
        var itemUrl = PopfeedItemUrlBuilder.BuildItemUrl(
            new PopfeedMappedItem(
                "tv_episode",
                new PopfeedIdentifiers
                {
                    TmdbId = "4556",
                }));

        Assert.Null(itemUrl);
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

        Assert.Equal("tv_episode", normalized.CreativeWorkType);
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
    /// Legacy episode-shaped identifiers (TmdbId + season/episode without series id)
    /// are no longer promoted to canonical tv-series coordinates.
    /// </summary>
    [Fact]
    public void NormalizeMappedItem_DoesNotPromoteLegacyEpisodeSeriesShapeToCanonicalCoordinates()
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

        Assert.Equal("tv_episode", normalized.CreativeWorkType);
        Assert.Equal("4556", normalized.Identifiers.TmdbId);
        Assert.Null(normalized.Identifiers.TmdbTvSeriesId);
        Assert.Equal(1, normalized.Identifiers.SeasonNumber);
        Assert.Equal(8, normalized.Identifiers.EpisodeNumber);
    }

    /// <summary>
    /// Legacy episode-shaped identifiers that carry season/episode coordinates
    /// must not resolve to any URL.
    /// </summary>
    [Fact]
    public void NormalizeAndBuildItemUrl_ReturnsNullForLegacyEpisodeSeriesShape()
    {
        var mapped = new PopfeedMappedItem(
            "tv_episode",
            new PopfeedIdentifiers
            {
                TmdbId = "4556",
                SeasonNumber = 1,
                EpisodeNumber = 1,
            });

        var normalized = PopfeedItemUrlBuilder.NormalizeMappedItem(mapped);
        var itemUrl = PopfeedItemUrlBuilder.BuildItemUrl(mapped);

        Assert.Equal("tv_episode", normalized.CreativeWorkType);
        Assert.Equal("4556", normalized.Identifiers.TmdbId);
        Assert.Null(normalized.Identifiers.TmdbTvSeriesId);
        Assert.Equal(1, normalized.Identifiers.SeasonNumber);
        Assert.Equal(1, normalized.Identifiers.EpisodeNumber);
        Assert.Null(itemUrl);
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
    /// A season is complete only when every library episode in that season is watched.
    /// </summary>
    [Fact]
    public void IsSeasonCompleteForTesting_ReturnsTrueOnlyForFullyWatchedSeason()
    {
        var libraryEpisodes = new (int SeasonNumber, int EpisodeNumber)[]
        {
            (1, 1),
            (1, 2),
            (2, 1),
            (2, 2),
        };

        var watchedEpisodes = new (int SeasonNumber, int EpisodeNumber)[]
        {
            (1, 1),
            (1, 2),
            (2, 1),
        };

        Assert.True(PopfeedWatchedListWriter.IsSeasonCompleteForTesting(libraryEpisodes, watchedEpisodes, 1));
        Assert.False(PopfeedWatchedListWriter.IsSeasonCompleteForTesting(libraryEpisodes, watchedEpisodes, 2));
        // A season with no library episodes is never complete.
        Assert.False(PopfeedWatchedListWriter.IsSeasonCompleteForTesting(libraryEpisodes, watchedEpisodes, 3));
    }

    /// <summary>
    /// A series should become incomplete when new library episodes are present
    /// but not yet watched.
    /// </summary>
    [Fact]
    public void IsSeriesCompleteForTesting_ReturnsFalseWhenNewSeasonAppears()
    {
        var previouslyComplete = PopfeedWatchedListWriter.IsSeriesCompleteForTesting(
            [(1, 1), (1, 2)],
            [(1, 1), (1, 2)]);
        Assert.True(previouslyComplete);

        var afterNewSeason = PopfeedWatchedListWriter.IsSeriesCompleteForTesting(
            [(1, 1), (1, 2), (2, 1)],
            [(1, 1), (1, 2)]);

        Assert.False(afterNewSeason);
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