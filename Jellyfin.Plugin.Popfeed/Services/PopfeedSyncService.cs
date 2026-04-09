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
    /// <param name="playedAt">The watched timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SyncPlaystateAsync(Guid jellyfinUserId, BaseItem item, bool played, DateTimeOffset? playedAt, CancellationToken cancellationToken)
    {
        _ = await ExecuteSyncCoreAsync(jellyfinUserId, item, played, playedAt, true, "event", cancellationToken).ConfigureAwait(false);
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
        return ExecuteSyncCoreAsync(jellyfinUserId, item, played, DateTimeOffset.UtcNow, !dryRun, "test", cancellationToken);
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

        if (item is Episode episode && episode.Series is not null && episode.IndexNumber.HasValue)
        {
            var identifiers = new PopfeedIdentifiers
            {
                ImdbId = GetProviderId(episode, MetadataProvider.Imdb),
                TmdbId = GetProviderId(episode, MetadataProvider.Tmdb),
                TmdbTvSeriesId = GetProviderId(episode.Series, MetadataProvider.Tmdb),
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

    private async Task<PopfeedSyncTestResult> ExecuteSyncCoreAsync(
        Guid jellyfinUserId,
        BaseItem item,
        bool played,
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
        var activityText = BuildWatchedActivityText(item);

        if (!executeRemote)
        {
            result.Success = true;
            result.Executed = false;
            result.CreatedPopfeedActivity = played;
            result.PostedToBluesky = played && userConfiguration.PostWatchedItemsToBluesky;
            result.Message = BuildDryRunMessage(played, result.PostedToBluesky);
            LogVerbose("Dry run successful for {ItemName}. UserId={UserId}, Identifier={Identifier}", item.Name, jellyfinUserId, userConfiguration.Identifier);
            return CompleteResult(result, triggerSource);
        }

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                LogVerbose(
                    "Starting Popfeed sync for {ItemName}. UserId={UserId}, Played={Played}, Provider={Provider}, Identifier={Identifier}",
                    item.Name,
                    jellyfinUserId,
                    played,
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
                    playedAt,
                    configuration.RemoveFromWatchedListWhenUnplayed,
                    cancellationToken).ConfigureAwait(false);

                result.CreatedPopfeedActivity = played;

                if (played && userConfiguration.PostWatchedItemsToBluesky)
                {
                    try
                    {
                        var timestamp = playedAt ?? DateTimeOffset.UtcNow;
                        var reviewUrl = activityResult is null ? null : BuildPopfeedReviewUrl(activityResult.Uri);
                        var post = BuildBlueskyPost(item, userConfiguration.BlueskyPostLanguage, activityText, reviewUrl, activityResult?.Record.Poster, timestamp);

                        var createdPost = await _client.CreateRecordAsync(userConfiguration.PdsUrl, session, BlueskyPostCollection, post, cancellationToken).ConfigureAwait(false);
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
                result.Message = BuildSuccessMessage(played, result.PostedToBluesky);
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

    private static string BuildDryRunMessage(bool played, bool postToBluesky)
    {
        if (!played)
        {
            return "Dry run successful. Matching Popfeed activity would be removed when a plugin-created record exists.";
        }

        return postToBluesky
            ? "Dry run successful. The item would be posted as Popfeed activity and cross-posted to Bluesky."
            : "Dry run successful. The item would be posted as Popfeed activity.";
    }

    private static string BuildSuccessMessage(bool played, bool postedToBluesky)
    {
        if (!played)
        {
            return "Remote sync completed successfully. Matching Popfeed activity was removed when present.";
        }

        return postedToBluesky
            ? "Remote sync completed successfully. Created Popfeed activity and a Bluesky post."
            : "Remote sync completed successfully. Created Popfeed activity.";
    }

    private static string BuildWatchedActivityText(BaseItem item)
    {
        return BuildWatchedActivityText(item, "en");
    }

    private static string BuildWatchedActivityText(BaseItem item, string languageCode)
    {
        var normalizedLanguage = NormalizeLanguageCode(languageCode);
        return normalizedLanguage == "fr"
            ? BuildFrenchWatchedActivityText(item)
            : BuildEnglishWatchedActivityText(item);
    }

    private static string BuildEnglishWatchedActivityText(BaseItem item)
    {
        var summary = item switch
        {
            Episode episode when episode.Series is not null => $"watched {episode.Series.Name} {FormatEpisodeLabel(episode)}{FormatEpisodeTitleSuffix(episode)}",
            Movie movie when movie.ProductionYear.HasValue => $"watched {movie.Name} ({movie.ProductionYear.Value})",
            Movie movie => $"watched {movie.Name}",
            _ => $"watched {item.Name}",
        };

        return TruncateBlueskyPost($"I {summary} on Jellyfin via Popfeed.");
    }

    private static string BuildFrenchWatchedActivityText(BaseItem item)
    {
        var summary = item switch
        {
            Episode episode when episode.Series is not null => $"J'ai regardé {episode.Series.Name} {FormatEpisodeLabel(episode)}{FormatEpisodeTitleSuffix(episode)} sur Jellyfin via Popfeed.",
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
        var text = BuildWatchedActivityText(item, normalizedLanguage);
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

    private static string BuildPopfeedReviewUrl(string reviewUri)
    {
        return "https://popfeed.social/review/" + Uri.EscapeDataString(reviewUri);
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