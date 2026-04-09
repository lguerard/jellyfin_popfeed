using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const string ListRecordsPath = "/xrpc/com.atproto.repo.listRecords";
    private const string DeleteRecordPath = "/xrpc/com.atproto.repo.deleteRecord";
    private const string UploadBlobPath = "/xrpc/com.atproto.repo.uploadBlob";
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PopfeedAtProtoClient> _logger;

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

        using var response = await client.PostAsJsonAsync(CreateSessionPath, request, _jsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        LogVerbose("ATProto session created successfully for identifier {Identifier}.", userConfiguration.Identifier);
        return (await response.Content.ReadFromJsonAsync<AtProtoSessionResponse>(_jsonOptions, cancellationToken).ConfigureAwait(false))!;
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
        using var response = await client.GetAsync(uriBuilder.ToString(), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<AtProtoListRecordsResponse<TRecord>>(_jsonOptions, cancellationToken).ConfigureAwait(false))!;
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

        using var response = await client.PostAsJsonAsync(CreateRecordPath, request, _jsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        LogVerbose("Created ATProto record in collection {Collection} on {ServiceUrl}.", collection, serviceUrl);
        return (await response.Content.ReadFromJsonAsync<AtProtoCreateRecordResponse>(_jsonOptions, cancellationToken).ConfigureAwait(false))!;
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

        using var response = await client.PostAsJsonAsync(PutRecordPath, request, _jsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        LogVerbose("Updated ATProto record in collection {Collection} on {ServiceUrl}.", collection, serviceUrl);
        return (await response.Content.ReadFromJsonAsync<AtProtoCreateRecordResponse>(_jsonOptions, cancellationToken).ConfigureAwait(false))!;
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
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        using var response = await client.PostAsync(UploadBlobPath, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<AtProtoUploadBlobResponse>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        return payload!.Blob;
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
        using var response = await client.PostAsJsonAsync(DeleteRecordPath, request, _jsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
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

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"ATProto request failed with {(int)response.StatusCode}: {body}");
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