using Jellyfin.Plugin.Popfeed.Models;
using Jellyfin.Plugin.Popfeed.Services;

using Xunit;

namespace Jellyfin.Plugin.Popfeed.Tests;

public sealed class PopfeedEpisodeIdentifierTests
{
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
            "tv_episode",
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
            CreativeWorkType = existing.CreativeWorkType,
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
}