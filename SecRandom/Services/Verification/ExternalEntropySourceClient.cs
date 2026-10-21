using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SecRandom.Core.Models.Verification;
using SecRandom.Core.Services.Verification;

namespace SecRandom.Services.Verification;

/// <summary>
///     Fetches the pulse a draw derives its seed from, for whichever source the user selected.
/// </summary>
/// <remarks>
///     <para>
///         Only the NIST branch is <em>verified</em> at fetch time, and that verification lives in
///         <see cref="NistBeaconClient" />. A drand round carries a BLS signature this build records but cannot check
///         — verifying it needs BLS12-381 pairing, i.e. a third-party library the project does not take — so a drand
///         pulse is trustworthy only <em>after</em> someone re-checks the recorded round, randomness and signature
///         against the chain key embedded in <see cref="BeaconSourceCatalog.DrandQuicknet" />. That is the same
///         "declare now, verify later" stance the reference (Own) chain takes.
///     </para>
///     <para>
///         random.org publishes nothing to verify at all: no timeline, no signature, no index. Its bytes are recorded
///         so the draw stays reproducible, not so they can be proven — an operator can fetch many blocks and keep the
///         one they like, exactly as with a local CSPRNG. What it buys is atmospheric-noise randomness instead of a
///         deterministic local generator.
///     </para>
/// </remarks>
public sealed class ExternalEntropySourceClient(
    INistBeaconClient nistClient,
    IHttpClientFactory httpClientFactory,
    ILogger<ExternalEntropySourceClient> logger)
{
    /// <summary>random.org answers one decimal byte per line; 32 of them make the 512-bit output a pulse carries.</summary>
    private const int RandomOrgByteCount = 32;

    /// <summary>Fetches are on the draw's critical path, so they get a short bound instead of the client default.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BeaconPulse> FetchLatestAsync(
        BeaconSource source,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(endpoint);

        return source.Kind switch
        {
            BeaconSourceKind.NistBeaconV2 => await nistClient
                .GetLatestPulseAsync(endpoint, cancellationToken).ConfigureAwait(false),
            BeaconSourceKind.DrandChain => await FetchDrandAsync(source, cancellationToken).ConfigureAwait(false),
            _ => await FetchRandomOrgAsync(endpoint, cancellationToken).ConfigureAwait(false)
        };
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient(nameof(ExternalEntropySourceClient));
        client.Timeout = RequestTimeout;
        return client;
    }

    private async Task<BeaconPulse> FetchDrandAsync(BeaconSource source, CancellationToken cancellationToken)
    {
        var requestUri = new Uri($"{source.Endpoint.TrimEnd('/')}/{source.ChainHash}/public/latest");
        using var client = CreateClient();
        using var response = await client.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"drand round request failed with {(int)response.StatusCode}: {error}", null, response.StatusCode);
        }

        var round = await response.Content
            .ReadFromJsonAsync<DrandRound>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("drand returned an empty round.");

        if (round.Round <= 0 || string.IsNullOrWhiteSpace(round.Randomness) || string.IsNullOrWhiteSpace(round.Signature))
            throw new InvalidDataException("drand round did not include a round number, randomness and signature.");

        // Validates the 512-bit output shape up front; a malformed round must never reach the seed derivation.
        _ = BeaconSeedDerivation.DecodeOutputValue(round.Randomness);

        var publishedAt = BeaconSourceCatalog.RoundTime(source, round.Round)
            ?? throw new InvalidDataException($"drand round {round.Round} has no publication time.");

        logger.LogDebug("已获取 drand 轮次：Round={Round}，链哈希={ChainHash}。", round.Round, source.ChainHash);

        // The chain hash rides in the certificate slot: it answers the same "which authority published this" question
        // and the proof has no other free field for it. drand is unchained, so there is no chain index.
        return new BeaconPulse(
            round.Round,
            0,
            source.PeriodSeconds,
            publishedAt,
            round.Randomness,
            round.Signature,
            source.ChainHash);
    }

    private async Task<BeaconPulse> FetchRandomOrgAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var requestUri = new Uri(
            endpoint,
            $"integers/?num={RandomOrgByteCount}&min=0&max=255&col=1&base=10&format=plain&rnd=new");
        using var client = CreateClient();
        using var response = await client.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"random.org request failed with {(int)response.StatusCode}: {error}", null, response.StatusCode);
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[RandomOrgByteCount];
        var count = 0;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            if (count == RandomOrgByteCount
                || !byte.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                throw new InvalidDataException("random.org returned an unexpected number of values or a non-byte.");

            bytes[count++] = value;
        }

        if (count != RandomOrgByteCount)
            throw new InvalidDataException(
                $"random.org returned {count} values instead of {RandomOrgByteCount}.");

        var builder = new StringBuilder(RandomOrgByteCount * 2);
        foreach (var value in bytes)
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));

        var output = builder.ToString();
        _ = BeaconSeedDerivation.DecodeOutputValue(output);

        logger.LogDebug("已获取 random.org 随机字节 {Count} 个。", count);

        // No round and no signature exist here, so the index stays 0 and the fetched bytes are the whole identity.
        return new BeaconPulse(0, 0, 0, DateTimeOffset.UtcNow, output, string.Empty, "random.org");
    }

    private sealed record DrandRound(long Round, string Randomness, string Signature);
}
