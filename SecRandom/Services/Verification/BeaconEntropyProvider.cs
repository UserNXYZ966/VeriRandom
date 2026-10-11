using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SecRandom.Core.Models.Verification;
using SecRandom.Core.Services.Config;
using SecRandom.Core.Services.Verification;
using SecRandom.Shared;
using SecRandom.Shared.Models.Verification;

namespace SecRandom.Services.Verification;

/// <summary>
///     Reserves a beacon-derived seed for one draw. The pulse and the per-pulse sequence number are the
///     only inputs, so the operator cannot inject custom entropy. The sequence anchor is persisted to
///     <c>data/proofs/beacon-state.json</c> before the seed is returned, which turns a crash or a failed
///     draw into a visible gap rather than a reused seed.
/// </summary>
public sealed class BeaconEntropyProvider(
    INistBeaconClient beaconClient,
    MainConfigHandler configHandler,
    ILogger<BeaconEntropyProvider> logger)
{
    private const string StateFileName = "beacon-state.json";

    /// <summary>
    ///     Provider family of the beacon this build fetches. It also prefixes every recorded beacon source id,
    ///     so a mixed-source draw can still be traced back to the authority that published each pulse.
    /// </summary>
    private const string BeaconProviderId = "nist-beacon-v2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _stateLoaded;
    private BeaconSequenceState? _state;

    private readonly object _prefetchGate = new();
    private (Uri Endpoint, Task<BeaconPulse> Pulse)? _prefetched;

    /// <summary>
    ///     Starts fetching the latest pulse without reserving anything, so a caller that knows a draw is about
    ///     to happen (the quick-draw rolling animation is the intended window) can hide the HTTP round trip
    ///     behind it. Deliberately does <em>not</em> touch the per-pulse sequence — only
    ///     <see cref="ReserveAsync" /> does — so a prefetch whose draw never happens consumes no sequence and
    ///     leaves no gap in the proof chain. A failed prefetch is dropped quietly: the draw fetches again and
    ///     fails loudly if the beacon is genuinely unreachable, because a draw must never fall back to a
    ///     local seed.
    /// </summary>
    public void Prefetch()
    {
        if (!IsEnabled)
            return;

        lock (_prefetchGate)
        {
            if (_prefetched is { Pulse.IsCompleted: false })
                return;

            // The endpoint is remembered with the task: an operator may switch endpoints between the
            // prefetch and the draw, and a pulse from the old endpoint must never be recorded as if it came
            // from the new one.
            var endpoint = BeaconEndpointPolicy.Normalize(configHandler.Data.General.Verification.BeaconEndpoint);
            _prefetched = (endpoint, beaconClient.GetLatestPulseAsync(endpoint, CancellationToken.None));
        }
    }

    private async Task<BeaconPulse> TakePrefetchedPulseAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        (Uri Endpoint, Task<BeaconPulse> Pulse)? pending;
        lock (_prefetchGate)
        {
            pending = _prefetched;
            _prefetched = null;
        }

        if (pending is { } candidate && candidate.Endpoint == endpoint)
        {
            try
            {
                return await candidate.Pulse.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "信标脉冲预取结果不可用，抽取前重新获取。");
            }
        }

        return await beaconClient.GetLatestPulseAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    public bool IsEnabled => configHandler.Data.General.Verification.BeaconEntropyEnabled;

    public async Task<BeaconSeedReservation> ReserveAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var endpoint = BeaconEndpointPolicy.Normalize(configHandler.Data.General.Verification.BeaconEndpoint);
            var pulse = await TakePrefetchedPulseAsync(endpoint, cancellationToken).ConfigureAwait(false);

            var state = LoadState();
            var sequence = BeaconSequencePolicy.ResolveNext(state, pulse, out var failure);
            if (failure != BeaconSequenceFailure.None)
                throw new BeaconEntropyException(DescribeFailure(failure, pulse, state));

            SaveState(new BeaconSequenceState(pulse.PulseIndex, pulse.ChainIndex, pulse.OutputValue, sequence));
            var seed = BeaconSeedDerivation.Derive(pulse, sequence);

            logger.LogInformation(
                "已预留信标熵：PulseIndex={PulseIndex}，ChainIndex={ChainIndex}，Sequence={Sequence}，端点={Endpoint}。",
                pulse.PulseIndex, pulse.ChainIndex, sequence, endpoint);

            return new BeaconSeedReservation(seed, new DrawProofBeacon
            {
                Provider = BeaconProviderId,
                Endpoint = endpoint.ToString(),
                PulseIndex = pulse.PulseIndex,
                ChainIndex = pulse.ChainIndex,
                PeriodSeconds = pulse.PeriodSeconds,
                PulseTimeStamp = pulse.TimeStamp,
                OutputValue = pulse.OutputValue,
                SignatureValue = pulse.SignatureValue,
                CertificateId = pulse.CertificateId,
                CipherSuite = pulse.CipherSuite,
                BeaconSources = [$"{BeaconProviderId}@{endpoint}"],
                Sequence = sequence,
                Derivation = BeaconSeedDerivation.DerivationId
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string DescribeFailure(BeaconSequenceFailure failure, BeaconPulse pulse,
        BeaconSequenceState? state)
    {
        return failure switch
        {
            BeaconSequenceFailure.PulseIndexRegression =>
                $"信标脉冲回退：当前 {pulse.PulseIndex}，已记录 {state?.PulseIndex}。已拒绝本次抽取。",
            BeaconSequenceFailure.PulseValueConflict =>
                $"信标脉冲 {pulse.PulseIndex} 的输出值与已记录值不一致。已拒绝本次抽取。",
            _ => "信标序号状态异常。"
        };
    }

    private static string StateFilePath => Utils.GetFilePath("proofs", StateFileName);

    private BeaconSequenceState? LoadState()
    {
        if (_stateLoaded)
            return _state;

        _stateLoaded = true;
        var path = StateFilePath;
        if (!File.Exists(path))
            return _state = null;

        try
        {
            var document = JsonSerializer.Deserialize<BeaconSequenceStateDocument>(File.ReadAllText(path), JsonOptions);
            _state = document is null || document.PulseIndex < 0 || document.Sequence < 0
                ? null
                : new BeaconSequenceState(document.PulseIndex, document.ChainIndex, document.OutputValue ?? string.Empty,
                    document.Sequence);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // A corrupt anchor cannot be trusted to prevent reuse, so it is treated as absent: the next
            // draw restarts at sequence 0 and the resulting duplicate becomes visible in the proof chain.
            logger.LogWarning(exception, "读取信标序号状态失败，将从 0 重新开始记录。");
            _state = null;
        }

        return _state;
    }

    private void SaveState(BeaconSequenceState state)
    {
        var path = StateFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        var document = new BeaconSequenceStateDocument
        {
            PulseIndex = state.PulseIndex,
            ChainIndex = state.ChainIndex,
            OutputValue = state.OutputValue,
            Sequence = state.Sequence
        };
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporaryPath, path, true);
        _state = state;
        _stateLoaded = true;
    }

    private sealed class BeaconSequenceStateDocument
    {
        public long PulseIndex { get; set; } = -1;
        public long ChainIndex { get; set; }
        public string OutputValue { get; set; } = string.Empty;
        public long Sequence { get; set; } = -1;
    }
}
