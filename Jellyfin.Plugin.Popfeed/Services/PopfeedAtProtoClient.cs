using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Popfeed.Configuration;
using Jellyfin.Plugin.Popfeed.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Popfeed.Services;

/// <summary>
/// Thin client around the ATProto XRPC endpoints used by the plugin.
/// </summary>
public sealed class PopfeedAtProtoClient
{
    private const string CreateSessionPath = "/xrpc/com.atproto.server.createSession";
    private const string CreateRecordPath = "/xrpc/com.atproto.repo.createRecord";
    private const string PutRecordPath = "/xrpc/com.atproto.repo.putRecord";
    private const string GetRecordPath = "/xrpc/com.atproto.repo.getRecord";
    private const string ListRecordsPath = "/xrpc/com.atproto.repo.listRecords";
    private const string DeleteRecordPath = "/xrpc/com.atproto.repo.deleteRecord";
    private const string UploadBlobPath = "/xrpc/com.atproto.repo.uploadBlob";
    private static readonly TimeSpan _sessionTtl = TimeSpan.FromMinutes(90);
    private const int MaxRequestAttempts = 6;
    private static readonly Regex _waitForRegex = new(@"wait\s+for\s+(\d+)\s*s", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PopfeedAtProtoClient> _logger;
    private readonly Dictionary<string, (AtProtoSessionResponse Session, DateTimeOffset ExpiresAt)> _sessionCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="PopfeedAtProtoClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The http client factory.</param>
    /// <param name="logger">The logger.</param>
    public PopfeedAtProtoClient(IHttpClientFactory httpClientFactory, ILogger<PopfeedAtProtoClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns a cached session when still valid, or authenticates and caches a new one.
    /// Sessions are valid for ~2 hours; the cache refreshes 90 minutes in so the token
    /// is always at least 30 minutes from expiry when first used within a sync.
    /// </summary>
    /// <param name="userConfiguration">The Popfeed user configuration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A valid authenticated session.</returns>
    public async Task<AtProtoSessionResponse> GetOrCreateSessionAsync(PopfeedUserConfiguration userConfiguration, CancellationToken cancellationToken)
    {
        var cacheKey = $"{userConfiguration.PdsUrl}|{userConfiguration.Identifier}";
        if (_sessionCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            LogVerbose("Reusing cached ATProto session for {Identifier}.", userConfiguration.Identifier);
            return cached.Session;
        }

        var session = await CreateSessionAsync(userConfiguration, cancellationToken).ConfigureAwait(false);
        _sessionCache[cacheKey] = (session, DateTimeOffset.UtcNow.Add(_sessionTtl));
        return session;
    }

    /// <summary>
    /// Creates a new session.
    /// </summary>
    /// <param name="userConfiguration">The Popfeed user configuration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The authenticated session.</returns>
    public async Task<AtProtoSessionResponse> CreateSessionAsync(PopfeedUserConfiguration userConfiguration, CancellationToken cancellationToken)
    {
        var client = CreateClient(userConfiguration.PdsUrl);
        LogVerbose("Creating ATProto session against {PdsUrl} for identifier {Identifier}.", userConfiguration.PdsUrl, userConfiguration.Identifier);
        var request = new
        {
            identifier = userConfiguration.Identifier,
            password = userConfiguration.AppPassword,
        };

        using var response = await SendWithRetriesAsync(
            ct => client.PostAsJsonAsync(CreateSessionPath, request, _jsonOptions, ct),
            "createSession",
            cancellationToken).ConfigureAwait(false);
        LogVerbose("ATProto session created successfully for identifier {Identifier}.", userConfiguration.Identifier);
        var session = await response.Content.ReadFromJsonAsync<AtProtoSessionResponse>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        return session ?? throw new InvalidOperationException("Empty session response.");
    }

    /// <summary>
    /// Lists records in a collection.
    /// </summary>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="serviceUrl">The PDS URL.</param>
    /// <param name="session">The current session.</param>
    /// <param name="collection">The collection NSID.</param>
    /// <param name="cursor">The optional cursor.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page of records.</returns>
    public async Task<AtProtoListRecordsResponse<TRecord>> ListRecordsAsync<TRecord>(
        string serviceUrl,
        AtProtoSessionResponse session,
        string collection,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var client = CreateAuthorizedClient(serviceUrl, session.AccessJwt);
        var uriBuilder = new StringBuilder();
        uriBuilder.Append(ListRecordsPath);
        uriBuilder.Append("?repo=");
        uriBuilder.Append(Uri.EscapeDataString(session.Did));
        uriBuilder.Append("&collection=");
        uriBuilder.Append(Uri.EscapeDataString(collection));
        uriBuilder.Append("&limit=100");

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            uriBuilder.Append("&cursor=");
            uriBuilder.Append(Uri.EscapeDataString(cursor));
        }

        LogVerbose("Listing ATProto records in collection {Collection} from {ServiceUrl}.", collection, serviceUrl);
        using var response = await SendWithRetriesAsync(
            ct => client.GetAsync(uriBuilder.ToString(), ct),
            "listRecords",
            cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<AtProtoListRecordsResponse<TRecord>>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        return body ?? throw new InvalidOperationException("Empty response from listRecords.");
    }

    /// <summary>
    /// Fetches a single record by its deterministic rkey.
    /// Returns <see langword="null"/> when the record does not exist. A missing
    /// record surfaces as HTTP 404 on some PDS implementations and as HTTP 400
    /// with an <c>error: "RecordNotFound"</c> body on others (e.g. the reference
    /// Bluesky PDS); both are treated as "absent".
    /// </summary>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="serviceUrl">The PDS URL.</param>
    /// <param name="session">The current session.</param>
    /// <param name="collection">The collection NSID.</param>
    /// <param name="rkey">The record key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The record, or <see langword="null"/> when absent.</returns>
    public async Task<AtProtoRecord<TRecord>?> GetRecordAsync<TRecord>(
        string serviceUrl,
        AtProtoSessionResponse session,
        string collection,
        string rkey,
        CancellationToken cancellationToken)
    {
        var client = CreateAuthorizedClient(serviceUrl, session.AccessJwt);
        var url = $"{GetRecordPath}?repo={Uri.EscapeDataString(session.Did)}&collection={Uri.EscapeDataString(collection)}&rkey={Uri.EscapeDataString(rkey)}";

        for (var attempt = 1; attempt <= MaxRequestAttempts; attempt++)
        {
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.IsSuccessStatusCode)
            {
                LogVerbose("Fetched ATProto record {Collection}/{Rkey}.", collection, rkey);
                return await response.Content.ReadFromJsonAsync<AtProtoRecord<TRecord>>(_jsonOptions, cancellationToken).ConfigureAwait(false);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // The reference PDS returns 400 RecordNotFound (not 404) for a missing record.
            if (response.StatusCode == HttpStatusCode.BadRequest
                && body.Contains("RecordNotFound", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (attempt < MaxRequestAttempts && IsRetryableStatusCode(response.StatusCode))
            {
                var delay = GetRetryDelay(response, body, attempt);
                _logger.LogWarning("ATProto getRecord failed with {StatusCode} on attempt {Attempt}; retrying in {Delay}s.", (int)response.StatusCode, attempt, Math.Ceiling(delay.TotalSeconds));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new HttpRequestException($"ATProto getRecord failed with {(int)response.StatusCode}: {body}");
        }

        throw new InvalidOperationException("ATProto getRecord retry loop exhausted.");
    }

    /// <summary>
    /// Creates a record.
    /// </summary>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="serviceUrl">The PDS URL.</param>
    /// <param name="session">The current session.</param>
    /// <param name="collection">The collection name.</param>
    /// <param name="record">The record payload.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created record response.</returns>
    public async Task<AtProtoCreateRecordResponse> CreateRecordAsync<TRecord>(
        string serviceUrl,
        AtProtoSessionResponse session,
        string collection,
        TRecord record,
        CancellationToken cancellationToken)
    {
        var client = CreateAuthorizedClient(serviceUrl, session.AccessJwt);
        var request = new
        {
            repo = session.Did,
            collection,
            record,
        };

        if (Plugin.Instance.Configuration.EnableDebugLogging)
        {
            _logger.LogInformation("[PopfeedDebug] Creating ATProto record in {Collection}: {Record}", collection, JsonSerializer.Serialize(record, _jsonOptions));
        }

        using var response = await SendWithRetriesAsync(
            ct => client.PostAsJsonAsync(CreateRecordPath, request, _jsonOptions, ct),
            "createRecord",
            cancellationToken).ConfigureAwait(false);
        LogVerbose("Created ATProto record in collection {Collection} on {ServiceUrl}.", collection, serviceUrl);
        var createResult = await response.Content.ReadFromJsonAsync<AtProtoCreateRecordResponse>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        return createResult ?? throw new InvalidOperationException("Empty create record response.");
    }

    /// <summary>
    /// Replaces an existing record.
    /// </summary>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="serviceUrl">The PDS URL.</param>
    /// <param name="session">The current session.</param>
    /// <param name="collection">The collection name.</param>
    /// <param name="rkey">The record key.</param>
    /// <param name="record">The record payload.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated record response.</returns>
    public async Task<AtProtoCreateRecordResponse> PutRecordAsync<TRecord>(
        string serviceUrl,
        AtProtoSessionResponse session,
        string collection,
        string rkey,
        TRecord record,
        CancellationToken cancellationToken)
    {
        var client = CreateAuthorizedClient(serviceUrl, session.AccessJwt);
        var request = new
        {
            repo = session.Did,
            collection,
            rkey,
            record,
        };

        if (Plugin.Instance.Configuration.EnableDebugLogging)
        {
            _logger.LogInformation("[PopfeedDebug] Updating ATProto record in {Collection} with rkey {Rkey}: {Record}", collection, rkey, JsonSerializer.Serialize(record, _jsonOptions));
        }

        using var response = await SendWithRetriesAsync(
            ct => client.PostAsJsonAsync(PutRecordPath, request, _jsonOptions, ct),
            "putRecord",
            cancellationToken).ConfigureAwait(false);
        LogVerbose("Updated ATProto record in collection {Collection} on {ServiceUrl}.", collection, serviceUrl);
        var updateResult = await response.Content.ReadFromJsonAsync<AtProtoCreateRecordResponse>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        return updateResult ?? throw new InvalidOperationException("Empty put record response.");
    }

    /// <summary>
    /// Uploads a blob to the repo.
    /// </summary>
    /// <param name="serviceUrl">The PDS URL.</param>
    /// <param name="session">The current session.</param>
    /// <param name="stream">The content stream.</param>
    /// <param name="mimeType">The content mime type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The uploaded blob reference.</returns>
    public async Task<AtProtoBlob> UploadBlobAsync(
        string serviceUrl,
        AtProtoSessionResponse session,
        Stream stream,
        string mimeType,
        CancellationToken cancellationToken)
    {
        var client = CreateAuthorizedClient(serviceUrl, session.AccessJwt);
        var initialPosition = stream.CanSeek ? stream.Position : 0;
        using var response = await SendWithRetriesAsync(
            async ct =>
            {
                if (stream.CanSeek)
                {
                    stream.Position = initialPosition;
                }

                using var content = new StreamContent(stream);
                content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
                return await client.PostAsync(UploadBlobPath, content, ct).ConfigureAwait(false);
            },
            "uploadBlob",
            cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<AtProtoUploadBlobResponse>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        return payload?.Blob ?? throw new InvalidOperationException("Empty upload blob response.");
    }

    /// <summary>
    /// Deletes a record.
    /// </summary>
    /// <param name="serviceUrl">The PDS URL.</param>
    /// <param name="session">The current session.</param>
    /// <param name="collection">The collection NSID.</param>
    /// <param name="rkey">The record key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task DeleteRecordAsync(
        string serviceUrl,
        AtProtoSessionResponse session,
        string collection,
        string rkey,
        CancellationToken cancellationToken)
    {
        var client = CreateAuthorizedClient(serviceUrl, session.AccessJwt);
        var request = new
        {
            repo = session.Did,
            collection,
            rkey,
        };

        LogVerbose("Deleting ATProto record from collection {Collection} with rkey {Rkey} on {ServiceUrl}.", collection, rkey, serviceUrl);
        using var response = await SendWithRetriesAsync(
            ct => client.PostAsJsonAsync(DeleteRecordPath, request, _jsonOptions, ct),
            "deleteRecord",
            cancellationToken).ConfigureAwait(false);
    }

    private HttpClient CreateClient(string serviceUrl)
    {
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(NormalizeServiceUrl(serviceUrl), UriKind.Absolute);
        return client;
    }

    private HttpClient CreateAuthorizedClient(string serviceUrl, string accessJwt)
    {
        var client = CreateClient(serviceUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessJwt);
        return client;
    }

    private static string NormalizeServiceUrl(string serviceUrl)
    {
        return serviceUrl.EndsWith("/", StringComparison.Ordinal) ? serviceUrl.TrimEnd('/') : serviceUrl;
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> sendAsync,
        string operationName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxRequestAttempts; attempt++)
        {
            var response = await sendAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var statusCode = response.StatusCode;

            if (attempt < MaxRequestAttempts && IsRetryableStatusCode(statusCode))
            {
                var delay = GetRetryDelay(response, body, attempt);
                _logger.LogWarning(
                    "ATProto {OperationName} failed with {StatusCode} on attempt {Attempt}/{MaxAttempts}. Waiting {DelaySeconds}s before retry. Body: {Body}",
                    operationName,
                    (int)statusCode,
                    attempt,
                    MaxRequestAttempts,
                    Math.Ceiling(delay.TotalSeconds),
                    body);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            response.Dispose();
            throw new HttpRequestException($"ATProto request failed with {(int)statusCode}: {body}");
        }

        throw new InvalidOperationException($"ATProto request retry loop exhausted for {operationName}.");
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.TooManyRequests
            || (int)statusCode >= 500;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, string body, int attempt)
    {
        var headerDelay = response.Headers.RetryAfter?.Delta;
        if (headerDelay.HasValue && headerDelay.Value > TimeSpan.Zero)
        {
            return headerDelay.Value;
        }

        if (response.Headers.RetryAfter?.Date is DateTimeOffset retryAt)
        {
            var untilRetry = retryAt - DateTimeOffset.UtcNow;
            if (untilRetry > TimeSpan.Zero)
            {
                return untilRetry;
            }
        }

        var waitForMatch = _waitForRegex.Match(body);
        if (waitForMatch.Success && int.TryParse(waitForMatch.Groups[1].Value, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds + 1);
        }

        var exponentialSeconds = Math.Min(60, (int)Math.Pow(2, attempt));
        return TimeSpan.FromSeconds(exponentialSeconds);
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