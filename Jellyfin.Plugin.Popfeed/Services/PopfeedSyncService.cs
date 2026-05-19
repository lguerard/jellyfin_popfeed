using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Popfeed.Configuration;
using Jellyfin.Plugin.Popfeed.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Popfeed.Services;

/// <summary>
/// Maps Jellyfin playstate changes into Popfeed ATProto records.
/// </summary>
public sealed class PopfeedSyncService
{
    private const string BlueskyPostCollection = "app.bsky.feed.post";
    private const int BlueskyPostMaxLength = 300;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly PopfeedAtProtoClient _client;
    private readonly IPopfeedWatchStateWriter[] _watchStateWriters;
    private readonly PopfeedSyncStatusStore _statusStore;
    private readonly ILogger<PopfeedSyncService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PopfeedSyncService"/> class.
    /// </summary>
    /// <param name="client">The ATProto client.</param>
    /// <param name="logger">The logger.</param>
    public PopfeedSyncService(
        PopfeedAtProtoClient client,
        System.Collections.Generic.IEnumerable<IPopfeedWatchStateWriter> watchStateWriters,
        PopfeedSyncStatusStore statusStore,
        ILogger<PopfeedSyncService> logger)
    {
        _client = client;
        _watchStateWriters = watchStateWriters.ToArray();
        _statusStore = statusStore;
        _logger = logger;
    }

    /// <summary>
    /// Synchronizes a watched-state change.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="jellyfinUserId">The Jellyfin user id.</param>
    /// <param name="played">Whether the item is now marked watched.</param>
    /// <param name="inProgress">Whether the item is currently in progress.</param>
    /// <param name="playedAt">The watched timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SyncPlaystateAsync(Guid jellyfinUserId, BaseItem item, bool played, bool inProgress, DateTimeOffset? playedAt, CancellationToken cancellationToken)
    {
        _ = await ExecuteSyncCoreAsync(jellyfinUserId, item, played, inProgress, playedAt, true, "event", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs a dry-run or real sync test for the settings page.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user id.</param>
    /// <param name="item">The item to test.</param>
    /// <param name="played">Whether the item should be treated as watched.</param>
    /// <param name="dryRun">Whether remote writes should be skipped.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sync test result.</returns>
    public Task<PopfeedSyncTestResult> TestSyncAsync(Guid jellyfinUserId, BaseItem item, bool played, bool dryRun, CancellationToken cancellationToken)
    {
        return ExecuteSyncCoreAsync(jellyfinUserId, item, played, false, DateTimeOffset.UtcNow, !dryRun, "test", cancellationToken);
    }

    /// <summary>
    /// Gets recent sync activity for the settings page.
    /// </summary>
    /// <param name="jellyfinUserId">Optional user filter.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <returns>The recent sync entries.</returns>
    public PopfeedSyncTestResult[] GetRecentStatuses(Guid? jellyfinUserId, int limit)
    {
        return _statusStore.GetRecent(jellyfinUserId, limit);
    }

    /// <summary>
    /// Returns whether the item type is enabled for sync by plugin settings.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns><see langword="true"/> when the item type is enabled.</returns>
    private static bool ShouldSyncItem(BaseItem item, PluginConfiguration configuration)
    {
        return (item is Movie && configuration.SyncMovies)
            || (item is Episode && configuration.SyncEpisodes);
    }

    /// <summary>
    /// Returns whether the item or its parent series is on the manual exclusion list.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns><see langword="true"/> when the item is excluded.</returns>
    private static bool IsExcluded(BaseItem item, PluginConfiguration configuration)
    {
        var excludedIds = configuration.GetExcludedItemIds();
        if (excludedIds.Count == 0)
        {
            return false;
        }

        if (excludedIds.Contains(item.Id.ToString()))
        {
            return true;
        }

        // Also exclude episodes that belong to an excluded series.
        return item is Episode episode
            && episode.Series is not null
            && excludedIds.Contains(episode.Series.Id.ToString());
    }

    /// <summary>
    /// Maps a Jellyfin media item to a Popfeed identifier payload.
    /// Returns null when the item has no recognisable external IDs.
    /// </summary>
    /// <param name="item">The Jellyfin item to map.</param>
    /// <returns>The mapped item, or null when no identifiers are available.</returns>
    private static PopfeedMappedItem? MapItem(BaseItem item)
    {
        if (item is Movie movie)
        {
            var identifiers = new PopfeedIdentifiers
            {
                ImdbId = GetProviderId(movie, MetadataProvider.Imdb),
                TmdbId = GetProviderId(movie, MetadataProvider.Tmdb),
            };

            return identifiers.IsEmpty
                ? null
                : new PopfeedMappedItem("movie", identifiers);
        }

        if (item is Season season && season.Series is not null && season.IndexNumber.HasValue)
        {
            var tmdbTvSeriesId = GetProviderId(season.Series, MetadataProvider.Tmdb);
            var identifiers = new PopfeedIdentifiers
            {
                ImdbId = string.IsNullOrWhiteSpace(tmdbTvSeriesId) ? GetProviderId(season, MetadataProvider.Imdb) : null,
                TmdbId = string.IsNullOrWhiteSpace(tmdbTvSeriesId) ? GetProviderId(season, MetadataProvider.Tmdb) : null,
                TmdbTvSeriesId = tmdbTvSeriesId,
                SeasonNumber = season.IndexNumber,
            };

            var hasSeasonShape = !string.IsNullOrWhiteSpace(identifiers.TmdbTvSeriesId) && identifiers.SeasonNumber.HasValue;
            var hasStandaloneId = !string.IsNullOrWhiteSpace(identifiers.ImdbId) || !string.IsNullOrWhiteSpace(identifiers.TmdbId);

            return (!hasSeasonShape && !hasStandaloneId)
                ? null
                : new PopfeedMappedItem("tv_season", identifiers);
        }

        if (item is Episode episode && episode.Series is not null && episode.IndexNumber.HasValue)
        {
            var tmdbTvSeriesId = GetEpisodeSeriesTmdbId(episode);
            var episodeTmdbId = GetProviderId(episode, MetadataProvider.Tmdb);
            var identifiers = new PopfeedIdentifiers
            {
                ImdbId = string.IsNullOrWhiteSpace(tmdbTvSeriesId) ? GetProviderId(episode, MetadataProvider.Imdb) : null,
                TmdbId = string.IsNullOrWhiteSpace(tmdbTvSeriesId) ? episodeTmdbId : null,
                TmdbTvSeriesId = tmdbTvSeriesId,
                SeasonNumber = episode.ParentIndexNumber,
                EpisodeNumber = episode.IndexNumber,
            };

            var hasEpisodeShape = !string.IsNullOrWhiteSpace(identifiers.TmdbTvSeriesId)
                && identifiers.SeasonNumber.HasValue
                && identifiers.EpisodeNumber.HasValue;
            var hasStandaloneId = !string.IsNullOrWhiteSpace(identifiers.ImdbId) || !string.IsNullOrWhiteSpace(identifiers.TmdbId);

            return (!hasEpisodeShape && !hasStandaloneId)
                ? null
                : new PopfeedMappedItem("tv_episode", identifiers);
        }

        return null;
    }

    /// <summary>
    /// Returns the provider id string for a given metadata provider, or null when absent.
    /// </summary>
    /// <param name="item">The item to query.</param>
    /// <param name="provider">The metadata provider.</param>
    /// <returns>The id value, or null when not present.</returns>
    private static string? GetProviderId(IHasProviderIds item, MetadataProvider provider)
    {
        return item.TryGetProviderId(provider, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    /// <summary>
    /// Returns the TMDb series id for an episode, falling back to the episode-level
    /// TMDb id for libraries that only expose it on the episode rather than the series.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <returns>The series TMDb id, or null when unavailable.</returns>
    private static string? GetEpisodeSeriesTmdbId(Episode episode)
    {
        var seriesTmdbId = GetProviderId(episode.Series!, MetadataProvider.Tmdb);
        if (!string.IsNullOrWhiteSpace(seriesTmdbId))
        {
            return seriesTmdbId;
        }

        // Some libraries only expose the parent show's TMDb id on the episode itself.
        return episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
            ? GetProviderId(episode, MetadataProvider.Tmdb)
            : null;
    }

    /// <summary>
    /// Returns whether a Bluesky post should be created for this sync event.
    /// In "season" mode, individual episodes are suppressed to avoid post spam.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="userConfiguration">The user configuration.</param>
    /// <returns><see langword="true"/> when a Bluesky post should be created.</returns>
    private static bool ShouldPostToBluesky(BaseItem item, PopfeedUserConfiguration userConfiguration)
    {
        if (!userConfiguration.PostWatchedItemsToBluesky)
        {
            return false;
        }

        // In "season" mode, individual episodes are suppressed.
        return !string.Equals(userConfiguration.BlueskyPostMode, "season", StringComparison.OrdinalIgnoreCase)
            || item is not Episode;
    }

    /// <summary>
    /// Core sync logic shared by both event-driven and test-driven paths.
    /// Validates configuration, maps identifiers, writes to Popfeed, and
    /// optionally cross-posts to Bluesky.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user id.</param>
    /// <param name="item">The media item.</param>
    /// <param name="played">Whether the item is marked watched.</param>
    /// <param name="inProgress">Whether playback is currently in progress.</param>
    /// <param name="playedAt">The watched timestamp.</param>
    /// <param name="executeRemote">When false, skips all remote writes (dry run).</param>
    /// <param name="triggerSource">Label written into the status store entry.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A result describing what happened.</returns>
    private async Task<PopfeedSyncTestResult> ExecuteSyncCoreAsync(
        Guid jellyfinUserId,
        BaseItem item,
        bool played,
        bool inProgress,
        DateTimeOffset? playedAt,
        bool executeRemote,
        string triggerSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var result = new PopfeedSyncTestResult
        {
            UserId = jellyfinUserId,
            ItemId = item.Id,
            ItemName = item.Name,
            Played = played,
        };

        var configuration = Plugin.Instance.Configuration;
        result.ProviderName = configuration.WatchStateProvider;

        if (!configuration.IsConfigured())
        {
            result.Message = "Plugin has no configured Popfeed users.";
            LogVerbose("Skipping {ItemName}: {Message}", item.Name, result.Message);
            return CompleteResult(result, triggerSource);
        }

        if (!ShouldSyncItem(item, configuration))
        {
            result.Message = "This media type is disabled by plugin settings.";
            LogVerbose("Skipping {ItemName}: {Message}", item.Name, result.Message);
            return CompleteResult(result, triggerSource);
        }

        if (IsExcluded(item, configuration))
        {
            result.Message = "This item is excluded from automatic Popfeed sync.";
            LogVerbose("Skipping {ItemName}: {Message}", item.Name, result.Message);
            return CompleteResult(result, triggerSource);
        }

        var userConfiguration = configuration.GetUserConfiguration(jellyfinUserId);
        if (userConfiguration is null || !userConfiguration.IsConfigured())
        {
            result.Message = "No Popfeed account mapping is configured for this Jellyfin user.";
            LogVerbose("Skipping {ItemName}: {Message}", item.Name, result.Message);
            return CompleteResult(result, triggerSource);
        }

        result.PopfeedIdentifier = userConfiguration.Identifier;

        var writer = _watchStateWriters.FirstOrDefault(candidate => string.Equals(candidate.ProviderName, configuration.WatchStateProvider, StringComparison.Ordinal));
        if (writer is null)
        {
            result.Message = $"No Popfeed watch-state writer is registered for provider '{configuration.WatchStateProvider}'.";
            _logger.LogWarning(result.Message);
            return CompleteResult(result, triggerSource);
        }

        var mapped = MapItem(item);
        if (mapped is null)
        {
            result.Message = "The item does not have enough Popfeed-compatible identifiers to sync.";
            LogVerbose("Skipping {ItemName}: {Message}", item.Name, result.Message);
            return CompleteResult(result, triggerSource);
        }

        result.CreativeWorkType = mapped.CreativeWorkType;
        result.Identifiers = mapped.Identifiers;
        result.WouldSync = true;
        var activityText = BuildActivityText(item, userConfiguration.BlueskyPostLanguage, played);
        var shouldPostToBluesky = played && userConfiguration.PostWatchedItemsToBluesky && ShouldPostToBluesky(item, userConfiguration);

        if (!executeRemote)
        {
            result.Success = true;
            result.Executed = false;
            result.CreatedPopfeedActivity = played || inProgress;
            result.PostedToBluesky = shouldPostToBluesky;
            result.Message = BuildDryRunMessage(played, inProgress, result.PostedToBluesky);
            LogVerbose("Dry run successful for {ItemName}. UserId={UserId}, Identifier={Identifier}", item.Name, jellyfinUserId, userConfiguration.Identifier);
            return CompleteResult(result, triggerSource);
        }

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                LogVerbose(
                    "Starting Popfeed sync for {ItemName}. UserId={UserId}, Played={Played}, InProgress={InProgress}, Provider={Provider}, Identifier={Identifier}",
                    item.Name,
                    jellyfinUserId,
                    played,
                    inProgress,
                    configuration.WatchStateProvider,
                    userConfiguration.Identifier);

                var session = await _client.CreateSessionAsync(userConfiguration, cancellationToken).ConfigureAwait(false);
                var activityResult = await writer.SyncAsync(
                    userConfiguration,
                    session,
                    mapped,
                    item.Name,
                    activityText,
                    item,
                    played,
                    inProgress,
                    playedAt,
                    configuration.RemoveFromWatchedListWhenUnplayed,
                    cancellationToken).ConfigureAwait(false);

                result.CreatedPopfeedActivity = played || inProgress;

                if (shouldPostToBluesky)
                {
                    try
                    {
                        var timestamp = playedAt ?? DateTimeOffset.UtcNow;
                        // Use the shared URL builder; it normalises legacy TmdbId→TmdbTvSeriesId
                        // before constructing the canonical Popfeed episode/season/movie URL.
                        var popfeedItemUrl = PopfeedItemUrlBuilder.BuildItemUrl(mapped, item);
                        var createdPost = await CreateBlueskyPostAsync(
                            userConfiguration,
                            session,
                            item,
                            userConfiguration.BlueskyPostLanguage,
                            activityText,
                            popfeedItemUrl,
                            activityResult?.Record.Poster,
                            timestamp,
                            cancellationToken).ConfigureAwait(false);
                        result.PostedToBluesky = true;

                        if (activityResult is not null)
                        {
                            activityResult.Record.CrossPosts ??= new PopfeedReviewCrossPosts();
                            activityResult.Record.CrossPosts.Bluesky = createdPost.Uri;
                            await _client.PutRecordAsync(
                                userConfiguration.PdsUrl,
                                session,
                                "social.popfeed.feed.review",
                                GetRecordKey(activityResult.Uri),
                                activityResult.Record,
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Created Popfeed activity for {ItemName}, but failed to create the Bluesky post for user {UserId}.", item.Name, jellyfinUserId);
                        result.Success = false;
                        result.Executed = true;
                        result.Message = "Popfeed activity was created, but the Bluesky post failed: " + ex.Message;
                        return CompleteResult(result, triggerSource);
                    }
                }

                result.Success = true;
                result.Executed = true;
                result.Message = BuildSuccessMessage(played, inProgress, result.PostedToBluesky);
                return CompleteResult(result, triggerSource);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed Popfeed sync for {ItemName} and user {UserId}.", item.Name, jellyfinUserId);
                result.Message = "Remote sync failed: " + ex.Message;
                return CompleteResult(result, triggerSource);
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Stamps the result with the trigger source and timestamp, persists it
    /// to the status store, and returns it.
    /// </summary>
    /// <param name="result">The result to finalise.</param>
    /// <param name="triggerSource">The trigger source label.</param>
    /// <returns>The finalised result.</returns>
    private PopfeedSyncTestResult CompleteResult(PopfeedSyncTestResult result, string triggerSource)
    {
        result.TriggerSource = triggerSource;
        result.TimestampUtc = DateTimeOffset.UtcNow;
        _statusStore.Add(result);
        return result;
    }

    /// <summary>
    /// Builds a human-readable dry-run result message for the settings page.
    /// </summary>
    /// <param name="played">Whether the item would be marked watched.</param>
    /// <param name="inProgress">Whether playback is in progress.</param>
    /// <param name="postToBluesky">Whether a Bluesky post would also be created.</param>
    /// <returns>The dry-run message.</returns>
    private static string BuildDryRunMessage(bool played, bool inProgress, bool postToBluesky)
    {
        if (played)
        {
            return postToBluesky
                ? "Dry run successful. The item would be posted as Popfeed activity and cross-posted to Bluesky."
                : "Dry run successful. The item would be posted as Popfeed activity.";
        }

        if (inProgress)
        {
            return "Dry run successful. The item would be marked as watching in Popfeed.";
        }

        return "Dry run successful. Matching Popfeed activity would be removed when a plugin-created record exists.";
    }

    /// <summary>
    /// Builds a human-readable success message for the settings page after a real sync.
    /// </summary>
    /// <param name="played">Whether the item was marked watched.</param>
    /// <param name="inProgress">Whether playback is in progress.</param>
    /// <param name="postedToBluesky">Whether a Bluesky post was created.</param>
    /// <returns>The success message.</returns>
    private static string BuildSuccessMessage(bool played, bool inProgress, bool postedToBluesky)
    {
        if (played)
        {
            return postedToBluesky
                ? "Remote sync completed successfully. Created Popfeed activity and a Bluesky post."
                : "Remote sync completed successfully. Created Popfeed activity.";
        }

        if (inProgress)
        {
            return "Remote sync completed successfully. The item was marked as watching in Popfeed.";
        }

        return "Remote sync completed successfully. Matching Popfeed activity was removed when present.";
    }

    /// <summary>
    /// Dispatches to the language-specific activity text builder.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="languageCode">The preferred BCP-47 language code.</param>
    /// <param name="played">Whether the item was fully watched (vs in progress).</param>
    /// <returns>The activity text for the Bluesky post and review record.</returns>
    private static string BuildActivityText(BaseItem item, string languageCode, bool played)
    {
        var normalizedLanguage = NormalizeLanguageCode(languageCode);
        return normalizedLanguage == "fr"
            ? BuildFrenchActivityText(item, played)
            : BuildEnglishActivityText(item, played);
    }

    /// <summary>
    /// Builds the English activity sentence.
    /// Uses past tense ("I watched") when <paramref name="played"/> is true,
    /// present tense ("I am watching") otherwise.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="played">Whether the item was fully watched.</param>
    /// <returns>The English activity text, truncated to the Bluesky post limit.</returns>
    private static string BuildEnglishActivityText(BaseItem item, bool played)
    {
        var summary = item switch
        {
            Episode episode when episode.Series is not null => $"{(played ? "watched" : "is watching")} {episode.Series.Name} {FormatEpisodeLabel(episode)}{FormatEpisodeTitleSuffix(episode)}",
            Season season when season.Series is not null && season.IndexNumber.HasValue => $"{(played ? "watched" : "is watching")} {season.Series.Name} season {season.IndexNumber.Value:00}",
            Movie movie when movie.ProductionYear.HasValue => $"{(played ? "watched" : "is watching")} {movie.Name} ({movie.ProductionYear.Value})",
            Movie movie => $"{(played ? "watched" : "is watching")} {movie.Name}",
            _ => $"{(played ? "watched" : "is watching")} {item.Name}",
        };

        return played
            ? TruncateBlueskyPost($"I {summary} on Jellyfin via Popfeed.")
            : TruncateBlueskyPost($"I am {summary} on Jellyfin via Popfeed.");
    }

    /// <summary>
    /// Builds the French activity sentence.
    /// "J'ai regardé" = past (watched); "Je regarde" = present (in progress).
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="played">Whether the item was fully watched.</param>
    /// <returns>The French activity text.</returns>
    private static string BuildFrenchActivityText(BaseItem item, bool played)
    {
        var summary = item switch
        {
            Episode episode when episode.Series is not null => $"{(played ? "J'ai regardé" : "Je regarde")} {episode.Series.Name} {FormatEpisodeLabel(episode)}{FormatEpisodeTitleSuffix(episode)} sur Jellyfin via Popfeed.",
            Season season when season.Series is not null && season.IndexNumber.HasValue => $"{(played ? "J'ai regardé" : "Je regarde")} la saison {season.IndexNumber.Value:00} de {season.Series.Name} sur Jellyfin via Popfeed.",
            Movie movie when movie.ProductionYear.HasValue => $"{(played ? "J'ai regardé" : "Je regarde")} {movie.Name} ({movie.ProductionYear.Value}) sur Jellyfin via Popfeed.",
            Movie movie => $"{(played ? "J'ai regardé" : "Je regarde")} {movie.Name} sur Jellyfin via Popfeed.",
            _ => $"{(played ? "J'ai regardé" : "Je regarde")} {item.Name} sur Jellyfin via Popfeed.",
        };

        return summary;
    }

    /// <summary>
    /// Formats the season/episode label, e.g. "S01E04". Uses "?" when the index is unknown.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <returns>The formatted S##E## label.</returns>
    private static string FormatEpisodeLabel(Episode episode)
    {
        var season = episode.ParentIndexNumber.HasValue ? $"S{episode.ParentIndexNumber.Value:00}" : "S?";
        var episodeNumber = episode.IndexNumber.HasValue ? $"E{episode.IndexNumber.Value:00}" : "E?";
        return season + episodeNumber;
    }

    /// <summary>
    /// Returns a quoted episode title suffix (e.g. <c> "Pilot"</c>),
    /// or an empty string when the episode has no title.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <returns>The title suffix string.</returns>
    private static string FormatEpisodeTitleSuffix(Episode episode)
    {
        return string.IsNullOrWhiteSpace(episode.Name) ? string.Empty : $" \"{episode.Name}\"";
    }

    /// <summary>
    /// Formats a <see cref="DateTime"/> as an ISO-8601 UTC string for ATProto records.
    /// </summary>
    /// <param name="dateTime">The datetime to format.</param>
    /// <returns>An ISO-8601 UTC string, e.g. <c>2026-05-19T12:00:00.000Z</c>.</returns>
    private static string ToAtProtoDateTime(DateTime dateTime)
    {
        return dateTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Constructs a <see cref="BlueskyFeedPostRecord"/> from the activity text and optional
    /// Popfeed URL. When a URL is supplied, adds a rich-text link facet and an external embed card.
    /// </summary>
    /// <param name="item">The media item (used for the embed card title).</param>
    /// <param name="languageCode">The BCP-47 language code for the post.</param>
    /// <param name="fallbackActivityText">The pre-built activity sentence.</param>
    /// <param name="popfeedItemUrl">Optional canonical Popfeed URL for the item.</param>
    /// <param name="thumb">Optional poster blob already uploaded to the PDS.</param>
    /// <param name="timestamp">The post creation timestamp.</param>
    /// <returns>The constructed Bluesky post record.</returns>
    private static BlueskyFeedPostRecord BuildBlueskyPost(
        BaseItem item,
        string languageCode,
        string fallbackActivityText,
        string? popfeedItemUrl,
        AtProtoBlob? thumb,
        DateTimeOffset timestamp)
    {
        var normalizedLanguage = NormalizeLanguageCode(languageCode);
        var linkLabel = normalizedLanguage == "fr" ? "Voir sur Popfeed" : "Open on Popfeed";
        // Append the link label on a new line only when we have a URL to attach.
        var postText = string.IsNullOrWhiteSpace(popfeedItemUrl)
            ? fallbackActivityText
            : TruncateBlueskyPost(fallbackActivityText + "\n\n" + linkLabel);

        var post = new BlueskyFeedPostRecord
        {
            Text = postText,
            CreatedAt = ToAtProtoDateTime(timestamp.UtcDateTime),
            Langs = [normalizedLanguage],
            Facets = [],
        };

        if (!string.IsNullOrWhiteSpace(popfeedItemUrl))
        {
            var linkStart = postText.LastIndexOf(linkLabel, StringComparison.Ordinal);
            if (linkStart >= 0)
            {
                post.Facets.Add(new BlueskyRichTextFacet
                {
                    Index = new BlueskyRichTextFacetIndex
                    {
                        ByteStart = GetUtf8ByteCount(postText[..linkStart]),
                        ByteEnd = GetUtf8ByteCount(postText[..(linkStart + linkLabel.Length)]),
                    },
                    Features = [new BlueskyLinkFacetFeature { Uri = popfeedItemUrl }],
                });
            }

            post.Embed = new BlueskyExternalEmbed
            {
                External = new BlueskyExternalObject
                {
                    Uri = popfeedItemUrl,
                    Title = BuildExternalEmbedTitle(item, normalizedLanguage),
                    Description = TruncateBlueskyDescription(fallbackActivityText),
                    Thumb = thumb,
                },
            };
        }

        return post;
    }

    /// <summary>
    /// Publishes a Bluesky post for the watched item. On failure caused by the
    /// optional embed metadata (URL or thumbnail), retries once without it so the
    /// plain-text post still goes through.
    /// </summary>
    /// <param name="userConfiguration">The Popfeed user mapping.</param>
    /// <param name="session">The authenticated ATProto session.</param>
    /// <param name="item">The media item.</param>
    /// <param name="languageCode">The BCP-47 language code for the post.</param>
    /// <param name="activityText">The pre-built activity sentence.</param>
    /// <param name="popfeedItemUrl">Optional canonical Popfeed URL.</param>
    /// <param name="thumb">Optional poster blob already uploaded to the PDS.</param>
    /// <param name="timestamp">The post creation timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The create-record response from the Bluesky PDS.</returns>
    private async Task<AtProtoCreateRecordResponse> CreateBlueskyPostAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        BaseItem item,
        string languageCode,
        string activityText,
        string? popfeedItemUrl,
        AtProtoBlob? thumb,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var post = BuildBlueskyPost(item, languageCode, activityText, popfeedItemUrl, thumb, timestamp);

        try
        {
            return await _client.CreateRecordAsync(userConfiguration.PdsUrl, session, BlueskyPostCollection, post, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(popfeedItemUrl) || thumb is not null)
        {
            _logger.LogWarning(ex, "Retrying Bluesky post for {ItemName} without Popfeed embed metadata.", item.Name);
            var fallbackPost = BuildBlueskyPost(item, languageCode, activityText, null, null, timestamp);
            return await _client.CreateRecordAsync(userConfiguration.PdsUrl, session, BlueskyPostCollection, fallbackPost, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the title string for the Bluesky external embed card.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="languageCode">The normalised BCP-47 language code.</param>
    /// <returns>The localised embed card title.</returns>
    private static string BuildExternalEmbedTitle(BaseItem item, string languageCode)
    {
        var itemTitle = item is Movie movie && movie.ProductionYear.HasValue
            ? $"{movie.Name} ({movie.ProductionYear.Value})"
            : item.Name;

        return languageCode == "fr"
            ? $"Activité Popfeed pour {itemTitle}"
            : $"Popfeed activity for {itemTitle}";
    }

    /// <summary>
    /// Truncates a string to 100 characters for use as a Bluesky embed card description.
    /// </summary>
    /// <param name="text">The text to truncate.</param>
    /// <returns>The truncated string, ending with an ellipsis when shortened.</returns>
    private static string TruncateBlueskyDescription(string text)
    {
        const int maxDescriptionLength = 100;
        return text.Length <= maxDescriptionLength
            ? text
            : text[..(maxDescriptionLength - 1)] + "…";
    }

    /// <summary>
    /// Returns the UTF-8 byte count for a string, used to compute byte-precise
    /// Bluesky rich-text facet offsets (Bluesky counts bytes, not characters).
    /// </summary>
    /// <param name="value">The string to measure.</param>
    /// <returns>The UTF-8 byte count.</returns>
    private static int GetUtf8ByteCount(string value)
    {
        return Encoding.UTF8.GetByteCount(value);
    }

    /// <summary>
    /// Normalises a BCP-47 language code to a consistent lowercase form.
    /// Compound codes such as "pt-BR" are preserved; simple codes are lowercased.
    /// Falls back to "en" when the input is null or whitespace.
    /// </summary>
    /// <param name="languageCode">The raw language code from user configuration.</param>
    /// <returns>The normalised language code.</returns>
    private static string NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return "en";
        }

        var trimmed = languageCode.Trim();
        return trimmed.Contains('-', StringComparison.Ordinal)
            ? trimmed
            : trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Extracts the ATProto record key (rkey) from the trailing segment of a record URI.
    /// </summary>
    /// <param name="uri">The ATProto record URI, e.g.
    /// <c>at://did:plc:xxx/social.popfeed.feed.review/rkey123</c>.</param>
    /// <returns>The rkey string.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the URI has no segments.</exception>
    private static string GetRecordKey(string uri)
    {
        var segments = uri.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new InvalidOperationException($"Could not extract rkey from URI '{uri}'.");
        }

        return segments[^1];
    }

    /// <summary>
    /// Truncates a string to <see cref="BlueskyPostMaxLength"/> characters,
    /// appending "..." when the text is shortened.
    /// </summary>
    /// <param name="text">The text to truncate.</param>
    /// <returns>The truncated string.</returns>
    private static string TruncateBlueskyPost(string text)
    {
        return text.Length <= BlueskyPostMaxLength
            ? text
            : text[..(BlueskyPostMaxLength - 3)] + "...";
    }

    /// <summary>
    /// Logs at <c>Information</c> level when debug logging is enabled in plugin settings,
    /// otherwise logs at <c>Debug</c> level so normal deployments stay quiet.
    /// </summary>
    /// <param name="message">The message template.</param>
    /// <param name="arguments">The message arguments.</param>
    private void LogVerbose(string message, params object?[] arguments)
    {
        if (Plugin.Instance.Configuration.EnableDebugLogging)
        {
            _logger.LogInformation("[PopfeedDebug] " + message, arguments);
        }
        else
        {
            _logger.LogDebug(message, arguments);
        }
    }
}