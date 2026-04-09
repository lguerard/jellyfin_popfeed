using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Popfeed.Models;

/// <summary>
/// Session response from com.atproto.server.createSession.
/// </summary>
public sealed class AtProtoSessionResponse
{
    /// <summary>
    /// Gets or sets the access token.
    /// </summary>
    public string AccessJwt { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the repo DID.
    /// </summary>
    public string Did { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the account handle.
    /// </summary>
    public string Handle { get; set; } = string.Empty;
}

/// <summary>
/// ATProto list records response.
/// </summary>
/// <typeparam name="TRecord">The record payload type.</typeparam>
public sealed class AtProtoListRecordsResponse<TRecord>
{
    /// <summary>
    /// Gets or sets the cursor.
    /// </summary>
    public string? Cursor { get; set; }

    /// <summary>
    /// Gets or sets the records.
    /// </summary>
    public List<AtProtoRecord<TRecord>> Records { get; set; } = [];
}

/// <summary>
/// A record wrapper returned by listRecords.
/// </summary>
/// <typeparam name="TRecord">The underlying record type.</typeparam>
public sealed class AtProtoRecord<TRecord>
{
    /// <summary>
    /// Gets or sets the record URI.
    /// </summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the record CID.
    /// </summary>
    public string Cid { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the record payload.
    /// </summary>
    public TRecord Value { get; set; } = default!;
}

/// <summary>
/// Create record response.
/// </summary>
public sealed class AtProtoCreateRecordResponse
{
    /// <summary>
    /// Gets or sets the URI.
    /// </summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the CID.
    /// </summary>
    public string Cid { get; set; } = string.Empty;
}

/// <summary>
/// Popfeed list record.
/// </summary>
public sealed class PopfeedListRecord
{
    /// <summary>
    /// Gets or sets the type discriminator.
    /// </summary>
    [JsonPropertyName("$type")]
    public string Type { get; set; } = "social.popfeed.feed.list";

    /// <summary>
    /// Gets or sets the list name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the list type.
    /// </summary>
    public string ListType { get; set; } = "watched";

    /// <summary>
    /// Gets or sets the created timestamp.
    /// </summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the list is ordered.
    /// </summary>
    public bool Ordered { get; set; }
}

/// <summary>
/// Popfeed list item record.
/// </summary>
public sealed class PopfeedListItemRecord
{
    /// <summary>
    /// Gets or sets the type discriminator.
    /// </summary>
    [JsonPropertyName("$type")]
    public string Type { get; set; } = "social.popfeed.feed.listItem";

    /// <summary>
    /// Gets or sets the identifiers.
    /// </summary>
    public PopfeedIdentifiers Identifiers { get; set; } = new();

    /// <summary>
    /// Gets or sets the creative work type.
    /// </summary>
    public string CreativeWorkType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list URI.
    /// </summary>
    public string ListUri { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    public string Status { get; set; } = "#finished";

    /// <summary>
    /// Gets or sets the timestamp when the item was added.
    /// </summary>
    public string AddedAt { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the completion timestamp.
    /// </summary>
    public string? CompletedAt { get; set; }

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the list type.
    /// </summary>
    public string? ListType { get; set; } = "watched";
}

/// <summary>
/// Popfeed review/activity record.
/// </summary>
public sealed class PopfeedReviewRecord
{
    /// <summary>
    /// Gets or sets the type discriminator.
    /// </summary>
    [JsonPropertyName("$type")]
    public string Type { get; set; } = "social.popfeed.feed.review";

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the activity text.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the identifiers.
    /// </summary>
    public PopfeedIdentifiers Identifiers { get; set; } = new();

    /// <summary>
    /// Gets or sets the creative work type.
    /// </summary>
    public string CreativeWorkType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the creation timestamp.
    /// </summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets record tags.
    /// </summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Gets or sets the ATProto facets.
    /// </summary>
    public List<object> Facets { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the record contains spoilers.
    /// </summary>
    public bool ContainsSpoilers { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is a revisit.
    /// </summary>
    public bool IsRevisit { get; set; }
}

/// <summary>
/// Standard Bluesky feed post record.
/// </summary>
public sealed class BlueskyFeedPostRecord
{
    /// <summary>
    /// Gets or sets the type discriminator.
    /// </summary>
    [JsonPropertyName("$type")]
    public string Type { get; set; } = "app.bsky.feed.post";

    /// <summary>
    /// Gets or sets the post text.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the creation timestamp.
    /// </summary>
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>
/// External identifiers for Popfeed records.
/// </summary>
public sealed class PopfeedIdentifiers
{
    /// <summary>
    /// Gets or sets the IMDb id.
    /// </summary>
    public string? ImdbId { get; set; }

    /// <summary>
    /// Gets or sets the TMDb id.
    /// </summary>
    public string? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the TMDb TV series id.
    /// </summary>
    public string? TmdbTvSeriesId { get; set; }

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    public int? SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number.
    /// </summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>
    /// Gets a value indicating whether no identifiers are present.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(ImdbId)
        && string.IsNullOrWhiteSpace(TmdbId)
        && string.IsNullOrWhiteSpace(TmdbTvSeriesId)
        && SeasonNumber is null
        && EpisodeNumber is null;

    /// <summary>
    /// Determines whether two identifier objects describe the same work.
    /// </summary>
    /// <param name="other">The other identifier set.</param>
    /// <returns><see langword="true"/> when the identifiers match.</returns>
    public bool Matches(PopfeedIdentifiers other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(ImdbId, other.ImdbId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(TmdbId, other.TmdbId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(TmdbTvSeriesId, other.TmdbTvSeriesId, StringComparison.OrdinalIgnoreCase)
            && SeasonNumber == other.SeasonNumber
            && EpisodeNumber == other.EpisodeNumber;
    }
}