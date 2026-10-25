using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SecRandom.Core.Models.SlideTrigger;
using SecRandom.Core.Services.Config;
using SecRandom.Core.Services.SlideTrigger;
using SecRandom.Platforms.Abstractions;

namespace SecRandom.Services.SlideTrigger;

/// <summary>What the watcher saw, for whatever surface wants to report it.</summary>
public sealed record SlideTriggerDetection(
    SlideTriggerPayloadResult Payload,
    DateTimeOffset DetectedAt,
    bool AlreadyUsedThisSession);

/// <summary>
///     Watches the projected slide and decodes a courseware marker when the teacher turns to one.
/// </summary>
/// <remarks>
///     <para>
///         Input-driven, not a screen recorder. A cheap timer asks the platform one question — "when did input last
///         happen" — and only a <em>change</em> in that answer starts a burst of captures. Nothing is read while the
///         room is idle, and nothing is read unless a full-screen presentation is in front.
///     </para>
///     <para>
///         A burst is three shots because a slide transition animates for up to about a second: one shot at +200 ms can
///         land on a half-drawn slide, and the failure would look like "the marker wasn't recognised". The burst stops at
///         the first frame that decodes.
///     </para>
///     <para>
///         This class deliberately stops at "a marker was read". Turning that into a draw is a separate step that has to
///         pass the same gates a local draw does, and keeping the two apart is what lets the detection be tested on a
///         projector without a draw ever happening.
///     </para>
/// </remarks>
public sealed class SlideTriggerWatcher : IDisposable
{
    /// <summary>How often the input clock is read. This is a syscall, not a capture.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Offsets from the input event at which a frame is taken; the first one that decodes wins.</summary>
    private static readonly TimeSpan[] BurstOffsets =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(600),
        TimeSpan.FromMilliseconds(1200)
    ];

    private readonly ISlideTriggerScreenProbe _probe;
    private readonly SlideTriggerScreenDecoder _decoder;
    private readonly SlideTriggerStore _store;
    private readonly MainConfigHandler _configHandler;
    private readonly ILogger<SlideTriggerWatcher> _logger;

    private readonly Timer _timer;

    private DateTimeOffset? _lastInputAt;
    private int _polling;
    private int _bursting;

    public SlideTriggerWatcher(
        IPlatformServiceRoot platform,
        SlideTriggerScreenDecoder decoder,
        SlideTriggerStore store,
        MainConfigHandler configHandler,
        ILogger<SlideTriggerWatcher> logger)
    {
        _probe = platform.ScreenProbe;
        _decoder = decoder;
        _store = store;
        _configHandler = configHandler;
        _logger = logger;

        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised after a frame decoded into a marker, on a background thread.</summary>
    public event EventHandler<SlideTriggerDetection>? MarkerDetected;

    public bool IsSupported => _probe.IsSupported;

    /// <summary>
    ///     The user's opt-in. The poll loop re-reads it every tick, so flipping it takes effect immediately without
    ///     starting or stopping the timer.
    /// </summary>
    public bool IsEnabled
    {
        get => _configHandler.Data.MoreSettings.SlideTriggerWatcherEnabled;
        set
        {
            if (_configHandler.Data.MoreSettings.SlideTriggerWatcherEnabled == value)
                return;

            _configHandler.Data.MoreSettings.SlideTriggerWatcherEnabled = value;

            // Forgetting the baseline on enable means the first tick after switching on cannot mistake stale input for
            // a fresh key press.
            _lastInputAt = null;
            _configHandler.Save();
        }
    }

    public void Start()
    {
        if (!_probe.IsSupported)
        {
            _logger.LogInformation("课件标记自动识别不可用：当前平台没有屏幕探测实现");
            return;
        }

        _timer.Change(PollInterval, PollInterval);
    }

    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    private void Poll()
    {
        // A tick can overlap the previous one when a capture runs long; the interlocked guard drops the overlap instead
        // of queueing work that would then fire several bursts for one key press.
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return;

        try
        {
            if (!_configHandler.Data.MoreSettings.SlideTriggerWatcherEnabled)
            {
                _lastInputAt = null;
                return;
            }

            if (Volatile.Read(ref _bursting) == 1)
                return;

            var lastInputAt = _probe.GetLastInputAt();
            if (lastInputAt is null)
                return;

            var previous = _lastInputAt;
            _lastInputAt = lastInputAt;

            // First tick only records the baseline: otherwise every program start counts as "input just happened".
            if (previous is null || lastInputAt <= previous)
                return;

            if (!_probe.IsPresentationForeground())
                return;

            Interlocked.Exchange(ref _bursting, 1);
            _ = RunBurstAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "课件标记采样失败");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private async Task RunBurstAsync()
    {
        var detectedAt = DateTimeOffset.Now;

        try
        {
            foreach (var offset in BurstOffsets)
            {
                await Task.Delay(offset).ConfigureAwait(false);

                var frame = _probe.CaptureForegroundDisplay();
                if (frame is null)
                    continue;

                var payload = _decoder.Decode(frame);
                if (payload is not { Success: true, Parameters: { } parameters })
                    continue;

                var definition = _store.Find(payload.Id);
                var alreadyUsed = definition?.IsConsumedIn(_store.Session) ?? false;

                if (!alreadyUsed)
                {
                    // Consumption is recorded immediately: a crash right after this must not let the same slide fire
                    // twice when the app comes back.
                    if (definition is not null)
                        _store.MarkConsumed(definition, DateTimeOffset.Now);

                    _logger.LogInformation(
                        "检测到课件标记 {Id}：{Count} 人，名单 {List}，时机 {Timing}",
                        payload.Id,
                        parameters.Count,
                        parameters.StudentListName ?? "(当前名单)",
                        parameters.Timing);
                }

                MarkerDetected?.Invoke(this, new SlideTriggerDetection(payload, detectedAt, alreadyUsed));
                return;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "课件标记识别失败");
        }
        finally
        {
            Interlocked.Exchange(ref _bursting, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>
///     Keeps the watcher's timer running for the lifetime of the host. It starts the poll loop and nothing else: whether
///     anything is actually sampled is decided per tick from the user's switch.
/// </summary>
public sealed class SlideTriggerWatcherHostedService(SlideTriggerWatcher watcher) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        watcher.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        watcher.Stop();
        return Task.CompletedTask;
    }
}
