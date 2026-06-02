using Jellyfin.Plugin.Popfeed.Configuration;

using Xunit;

namespace Jellyfin.Plugin.Popfeed.Tests;

/// <summary>
/// Tests for watched-list URI compatibility behavior.
/// </summary>
public sealed class PopfeedUserConfigurationTests
{
    /// <summary>
    /// TV creative work types must keep honoring the legacy shared URI field
    /// when the TV-specific cache field is empty.
    /// </summary>
    [Fact]
    public void GetWatchedListUri_TvTypesFallbackToLegacyUri()
    {
        var configuration = new PopfeedUserConfiguration
        {
            WatchedListUri = "at://did:plc:test/social.popfeed.feed.list/legacy-tv",
            WatchedTelevisionListUri = string.Empty,
        };

        var resolved = configuration.GetWatchedListUri("tv_episode");

        Assert.Equal(configuration.WatchedListUri, resolved);
    }

    /// <summary>
    /// Movie creative work types must keep honoring the legacy shared URI field
    /// when the movie-specific cache field is empty.
    /// </summary>
    [Fact]
    public void GetWatchedListUri_MovieTypeFallbackToLegacyUri()
    {
        var configuration = new PopfeedUserConfiguration
        {
            WatchedListUri = "at://did:plc:test/social.popfeed.feed.list/legacy-movies",
            WatchedMovieListUri = string.Empty,
        };

        var resolved = configuration.GetWatchedListUri("movie");

        Assert.Equal(configuration.WatchedListUri, resolved);
    }
}