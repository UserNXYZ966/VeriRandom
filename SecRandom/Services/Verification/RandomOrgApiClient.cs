using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SecRandom.Services.Verification;

/// <summary>Quota reported by random.org for one API key.</summary>
public sealed record RandomOrgUsage(int BitsLeft, int RequestsLeft);

/// <summary>Raised when the key itself is rejected, as opposed to the request being unreadable.</summary>
public sealed class RandomOrgKeyRejectedException(string message) : Exception(message);

/// <summary>
///     Minimal client for random.org's JSON-RPC API, used to check that a pasted key actually works before it is
///     stored. It deliberately does not fetch random numbers: the draw path keeps using the plain endpoint until the
///     signed path is wired, and a validation request costs one request out of the key's quota rather than touching
///     any draw.
/// </summary>
public sealed class RandomOrgApiClient(IHttpClientFactory httpClientFactory, ILogger<RandomOrgApiClient> logger)
{
    private const string Endpoint = "https://api.random.org/json-rpc/4/invoke";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The usage numbers for <paramref name="apiKey" />. A rejected key throws
    ///     <see cref="RandomOrgKeyRejectedException" /> with random.org's own message; anything unreadable throws.
    /// </summary>
    public async Task<RandomOrgUsage> GetUsageAsync(string apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var request = new JsonRpcRequest("getUsage", new GetUsageParameters(apiKey.Trim()), 1);
        using var client = httpClientFactory.CreateClient(nameof(RandomOrgApiClient));
        client.Timeout = RequestTimeout;

        using var response = await client
            .PostAsJsonAsync(Endpoint, request, JsonOptions, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"random.org 返回 HTTP {(int)response.StatusCode}：{Trim(body)}", null, response.StatusCode);

        var envelope = JsonSerializer.Deserialize<JsonRpcResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("random.org 返回了空响应。");

        if (envelope.Error is { } error)
        {
            // -10xx is random.org's range for "key is bad/expired": that is the answer the user needs to see, and it
            // must not be reported as "network problem".
            throw new RandomOrgKeyRejectedException($"random.org 拒绝了该密钥（{error.Code}）：{error.Message}");
        }

        var usage = envelope.Result?.Usage
            ?? throw new InvalidOperationException("random.org 的响应里没有用量信息。");

        logger.LogInformation(
            "random.org 密钥校验通过：剩余位数={BitsLeft}，剩余请求={RequestsLeft}。",
            usage.BitsLeft, usage.RequestsLeft);

        return new RandomOrgUsage(usage.BitsLeft, usage.RequestsLeft);
    }

    private static string Trim(string body)
    {
        var trimmed = body.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
    }

    private sealed record JsonRpcRequest(string Method, GetUsageParameters Params, int Id)
    {
        public string JsonRpc { get; init; } = "2.0";
    }

    private sealed record GetUsageParameters(string ApiKey);

    private sealed record JsonRpcResponse(JsonRpcError? Error, JsonRpcResult? Result);

    private sealed record JsonRpcError(int Code, string Message);

    private sealed record JsonRpcResult(RandomOrgUsagePayload Usage);

    private sealed record RandomOrgUsagePayload(int BitsLeft, int RequestsLeft);
}
