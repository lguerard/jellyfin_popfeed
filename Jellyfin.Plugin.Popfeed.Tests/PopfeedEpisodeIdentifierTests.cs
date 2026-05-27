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
    /// An episode item with canonical identifiers (TmdbTvSeriesId + season + episode) must
    /// produce the <c>https://popfeed.social/episode?tvId=…</c> URL.
    /// </summary>
    [Fact]
    public void BuildItemUrl_UsesCanonicalEpisodePath()
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
                    TmdbTvSeriesId = "97546",
                    SeasonNumber = 3,
                    EpisodeNumber = 4,
                }));

        Assert.Equal(
            "https://popfeed.social/episode?tvId=97546&seasonNumber=3&episodeNumber=4",
            itemUrl);
    }

    /// <summary>
    /// A legacy episode shape (TmdbId instead of TmdbTvSeriesId) must be automatically
    /// normalised and produce the correct canonical episode URL.
    /// Previously the builder returned null for this shape; it now normalises it.
    /// </summary>
    [Fact]
    public void BuildItemUrl_NormalizesLegacyEpisodeShapeToCanonicalUrl()
    {
        var itemUrl = PopfeedItemUrlBuilder.BuildItemUrl(
            new PopfeedMappedItem(
                "tv_episode",
                new PopfeedIdentifiers
                {
                    TmdbId = "67997",
                    SeasonNumber = 33,
                    EpisodeNumber = 22,
                }));

        Assert.Equal(
            "https://popfeed.social/episode?tvId=67997&seasonNumber=33&episodeNumber=22",
            itemUrl);
    }

    /// <summary>
    /// A legacy episode shape (TmdbId) must be rewritten by
    /// <see cref="PopfeedItemUrlBuilder.NormalizeMappedItem"/> so that
    /// <c>TmdbTvSeriesId</c> is populated and <c>TmdbId</c> is cleared.
    /// </summary>
    [Fact]
    public void NormalizeMappedItem_RewritesLegacyEpisodeShape()
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
    /// Normalisation and URL building must both produce the same canonical episode shape
    /// from a legacy identifier input, confirming that Popfeed scrobbling and Bluesky
    /// posting resolve the same URL.
    /// </summary>
    [Fact]
    public void NormalizeAndBuildItemUrl_ProduceSameCanonicalEpisodeShape()
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

        Assert.Equal("4556", normalized.Identifiers.TmdbTvSeriesId);
        Assert.Equal(
            "https://popfeed.social/episode?tvId=4556&seasonNumber=1&episodeNumber=1",
            itemUrl);
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