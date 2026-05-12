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

    private static bool ShouldSyncItem(BaseItem item, PluginConfiguration configuration)
    {
        return (item is Movie && configuration.SyncMovies)
            || (item is Episode && configuration.SyncEpisodes);
    }

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

        return item is Episode episode
            && episode.Series is not null
            && excludedIds.Contains(episode.Series.Id.ToString());
    }

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
            var tmdbTvSeriesId = GetProviderId(episode.Series, MetadataProvider.Tmdb);
            var identifiers = new PopfeedIdentifiers
            {
                ImdbId = string.IsNullOrWhiteSpace(tmdbTvSeriesId) ? GetProviderId(episode, MetadataProvider.Imdb) : null,
                TmdbId = string.IsNullOrWhiteSpace(tmdbTvSeriesId) ? GetProviderId(episode, MetadataProvider.Tmdb) : null,
                TmdbTvSeriesId = tmdbTvSeriesId,
                SeasonNumber = episode.ParentIndexNumber,
                EpisodeNumber = episode.IndexNumber,
            };

            var hasEpisodeShape = !string.IsNullOrWhiteSpace(identifiers.TmdbTvSeriesId) && identifiers.SeasonNumber.HasValue;
            var hasStandaloneId = !string.IsNullOrWhiteSpace(identifiers.ImdbId) || !string.IsNullOrWhiteSpace(identifiers.TmdbId);

            return (!hasEpisodeShape && !hasStandaloneId)
                ? null
                : new PopfeedMappedItem("tv_episode", identifiers);
        }

        return null;
    }

    private static string? GetProviderId(IHasProviderIds item, MetadataProvider provider)
    {
        return item.TryGetProviderId(provider, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static bool ShouldPostToBluesky(BaseItem item, PopfeedUserConfiguration userConfiguration)
    {
        if (!userConfiguration.PostWatchedItemsToBluesky)
        {
            return false;
        }

        return !string.Equals(userConfiguration.BlueskyPostMode, "season", StringComparison.OrdinalIgnoreCase)
            || item is not Episode;
    }

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
        var activityText = BuildActivityText(item, userConfiguration.BlueskyPostLanguage, played, inProgress);
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
                        var reviewUrl = activityResult is null ? null : BuildPopfeedReviewUrl(activityResult.Uri, session.Handle);
                        var createdPost = await CreateBlueskyPostAsync(
                            userConfiguration,
                            session,
                            item,
                            userConfiguration.BlueskyPostLanguage,
                            activityText,
                            reviewUrl,
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

    private PopfeedSyncTestResult CompleteResult(PopfeedSyncTestResult result, string triggerSource)
    {
        result.TriggerSource = triggerSource;
        result.TimestampUtc = DateTimeOffset.UtcNow;
        _statusStore.Add(result);
        return result;
    }

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

    private static string BuildActivityText(BaseItem item, string languageCode, bool played, bool inProgress)
    {
        var normalizedLanguage = NormalizeLanguageCode(languageCode);
        return normalizedLanguage == "fr"
            ? BuildFrenchActivityText(item, played, inProgress)
            : BuildEnglishActivityText(item, played, inProgress);
    }

    private static string BuildEnglishActivityText(BaseItem item, bool played, bool inProgress)
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

    private static string BuildFrenchActivityText(BaseItem item, bool played, bool inProgress)
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

    private static string BuildFrenchWatchedActivityText(BaseItem item)
    {
        var summary = item switch
        {
            Episode episode when episode.Series is not null => $"J'ai regardé {episode.Series.Name} {FormatEpisodeLabel(episode)}{FormatEpisodeTitleSuffix(episode)} sur Jellyfin via Popfeed.",
            Season season when season.Series is not null && season.IndexNumber.HasValue => $"J'ai regardé la saison {season.IndexNumber.Value:00} de {season.Series.Name} sur Jellyfin via Popfeed.",
            Movie movie when movie.ProductionYear.HasValue => $"J'ai regardé {movie.Name} ({movie.ProductionYear.Value}) sur Jellyfin via Popfeed.",
            Movie movie => $"J'ai regardé {movie.Name} sur Jellyfin via Popfeed.",
            _ => $"J'ai regardé {item.Name} sur Jellyfin via Popfeed.",
        };

        return TruncateBlueskyPost(summary);
    }

    private static string FormatEpisodeLabel(Episode episode)
    {
        var season = episode.ParentIndexNumber.HasValue ? $"S{episode.ParentIndexNumber.Value:00}" : "S?";
        var episodeNumber = episode.IndexNumber.HasValue ? $"E{episode.IndexNumber.Value:00}" : "E?";
        return season + episodeNumber;
    }

    private static string FormatEpisodeTitleSuffix(Episode episode)
    {
        return string.IsNullOrWhiteSpace(episode.Name) ? string.Empty : $" \"{episode.Name}\"";
    }

    private static string ToAtProtoDateTime(DateTime dateTime)
    {
        return dateTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static BlueskyFeedPostRecord BuildBlueskyPost(
        BaseItem item,
        string languageCode,
        string fallbackActivityText,
        string? reviewUrl,
        AtProtoBlob? thumb,
        DateTimeOffset timestamp)
    {
        var normalizedLanguage = NormalizeLanguageCode(languageCode);
        var linkLabel = normalizedLanguage == "fr" ? "Voir sur Popfeed" : "Open on Popfeed";
        var text = fallbackActivityText;
        var postText = string.IsNullOrWhiteSpace(reviewUrl)
            ? text
            : TruncateBlueskyPost(text + "\n\n" + linkLabel);

        var post = new BlueskyFeedPostRecord
        {
            Text = postText,
            CreatedAt = ToAtProtoDateTime(timestamp.UtcDateTime),
            Langs = [normalizedLanguage],
            Facets = [],
        };

        if (!string.IsNullOrWhiteSpace(reviewUrl))
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
                    Features = [new BlueskyLinkFacetFeature { Uri = reviewUrl }],
                });
            }

            post.Embed = new BlueskyExternalEmbed
            {
                External = new BlueskyExternalObject
                {
                    Uri = reviewUrl,
                    Title = BuildExternalEmbedTitle(item, normalizedLanguage),
                    Description = TruncateBlueskyDescription(fallbackActivityText),
                    Thumb = thumb,
                },
            };
        }

        return post;
    }

    private async Task<AtProtoCreateRecordResponse> CreateBlueskyPostAsync(
        PopfeedUserConfiguration userConfiguration,
        AtProtoSessionResponse session,
        BaseItem item,
        string languageCode,
        string activityText,
        string? reviewUrl,
        AtProtoBlob? thumb,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var post = BuildBlueskyPost(item, languageCode, activityText, reviewUrl, thumb, timestamp);

        try
        {
            return await _client.CreateRecordAsync(userConfiguration.PdsUrl, session, BlueskyPostCollection, post, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(reviewUrl) || thumb is not null)
        {
            _logger.LogWarning(ex, "Retrying Bluesky post for {ItemName} without Popfeed embed metadata.", item.Name);
            var fallbackPost = BuildBlueskyPost(item, languageCode, activityText, null, null, timestamp);
            return await _client.CreateRecordAsync(userConfiguration.PdsUrl, session, BlueskyPostCollection, fallbackPost, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildExternalEmbedTitle(BaseItem item, string languageCode)
    {
        var itemTitle = item is Movie movie && movie.ProductionYear.HasValue
            ? $"{movie.Name} ({movie.ProductionYear.Value})"
            : item.Name;

        return languageCode == "fr"
            ? $"Activité Popfeed pour {itemTitle}"
            : $"Popfeed activity for {itemTitle}";
    }

    private static string TruncateBlueskyDescription(string text)
    {
        const int maxDescriptionLength = 100;
        return text.Length <= maxDescriptionLength
            ? text
            : text[..(maxDescriptionLength - 1)] + "…";
    }

    private static string BuildPopfeedReviewUrl(string reviewUri, string handle)
    {
        // reviewUri is an AT-URI: at://{did}/{collection}/{rkey}
        // Popfeed web URL format: https://popfeed.social/profile/{handle}/review/{rkey}
        var rkey = GetRecordKey(reviewUri);
        return $"https://popfeed.social/profile/{Uri.EscapeDataString(handle)}/review/{rkey}";
    }

    private static int GetUtf8ByteCount(string value)
    {
        return Encoding.UTF8.GetByteCount(value);
    }

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

    private static string GetRecordKey(string uri)
    {
        var segments = uri.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new InvalidOperationException($"Could not extract rkey from URI '{uri}'.");
        }

        return segments[^1];
    }

    private static string TruncateBlueskyPost(string text)
    {
        return text.Length <= BlueskyPostMaxLength
            ? text
            : text[..(BlueskyPostMaxLength - 3)] + "...";
    }

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