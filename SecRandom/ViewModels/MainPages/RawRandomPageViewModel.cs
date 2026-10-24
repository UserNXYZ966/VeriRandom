using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SecRandom.Core.Services.Config;
using SecRandom.Core.Services.Draw;
using SecRandom.Core.Services.Verification;
using SecRandom.Langs.MainPages.RawRandom;
using SecRandom.Services.Verification;

namespace SecRandom.ViewModels.MainPages;

/// <summary>
///     主界面「随机数」页：在用户给的范围里生成一个裸随机数。
/// </summary>
/// <remarks>
///     <para>
///         <b>为什么不用抽签内核</b>：<c>DrawEngine</c> 要求具体候选池，而这里的范围可以是 1..10⁹——把范围物化成候选
///         既浪费又没有意义。裸随机数的正确做法是<b>拒绝采样</b>（<see cref="RejectionSampler" />）：种子经 SHA-256
///         计数器模式展开，落在尾部不满一整块的值丢掉重来，因此不存在取模偏差。
///     </para>
///     <para>
///         <b>可复核性与"公平"是两件事</b>：只要参与派生的熵全部来自<b>单一</b>可验证源（NIST / drand），事后就能用那个
///         公开脉冲重算同一个数；一旦混入 random.org 或本地 CSPRNG，本次结果只能证明"输入产出这个数"，不能作为可复核
///         证据——界面对此给出阻断式说明，而不是脚注。无论哪种组合，它都<b>不能</b>证明抽中了某个人：没有名单就没有
///         roster digest。
///     </para>
///     <para>
///         <b>为什么不复用 <c>BeaconEntropyProvider</c></b>：那个服务按 <c>BeaconSourceId</c> 绑死单一来源，并为信标
///         序号写锚点文件——那是抽签的语义（一次抽取占一个序号）。本页允许一次勾多个源，走的是"就地取数 + 哈希合并"，
///         不占抽签序号，也不写抽签证明。
///     </para>
/// </remarks>
public partial class RawRandomPageViewModel : ViewModelBase
{
    /// <summary>Sentinel id for the local CSPRNG pseudo-source, which is not part of the beacon catalog.</summary>
    public const string LocalSourceId = "local-csprng";

    private readonly ExternalEntropySourceClient _entropySourceClient;
    private readonly MainConfigHandler _configHandler;
    private readonly ILogger<RawRandomPageViewModel> _logger;

    /// <summary>
    ///     True while the constructor seeds the observable properties from configuration: a first write would
    ///     otherwise persist the defaults back over the saved values before the user touched anything.
    /// </summary>
    private bool _isLoading;

    public RawRandomPageViewModel(
        MainConfigHandler configHandler,
        ExternalEntropySourceClient entropySourceClient,
        ILogger<RawRandomPageViewModel> logger) : base(configHandler)
    {
        _configHandler = configHandler;
        _entropySourceClient = entropySourceClient;
        _logger = logger;

        var more = configHandler.Data.MoreSettings;

        _isLoading = true;
        LowerBoundText = more.RawRandomLowerBound.ToString();
        UpperBoundText = more.RawRandomUpperBound.ToString();
        IsSettingsLocked = more.RawRandomSettingsLocked;

        EntropySources = new ObservableCollection<EntropySourceOption>(
            BeaconSourceCatalog.All.Select(source => new EntropySourceOption(
                source.Id,
                DisplayNameOf(source),
                IsVerifiable(source))));
        EntropySources.Add(new EntropySourceOption(LocalSourceId, Resources.C_LocalCsprng, false));

        // The first verifiable source is the one preselected: a user who never opens the choice gets the reproducible
        // path rather than the one that silently cannot be checked afterwards.
        if (EntropySources.Count > 0)
            EntropySources[0].IsSelected = true;

        foreach (var option in EntropySources)
            option.PropertyChanged += OnEntropySourceChanged;

        _isLoading = false;

        StatusText = Resources.M_Ready;
        RefreshVerifiabilityNotice();
    }

    public ObservableCollection<EntropySourceOption> EntropySources { get; }

    [ObservableProperty] private string _lowerBoundText = "0";
    [ObservableProperty] private string _upperBoundText = "100";
    [ObservableProperty] private bool _isSettingsLocked;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _resultText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _verifiabilityNotice = string.Empty;

    /// <summary>Red warning styling is driven from here so the AXAML keeps a single selector to react to.</summary>
    [ObservableProperty] private bool _isVerifiabilityReduced;

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (IsBusy)
            return;

        if (!TryReadRange(out var lower, out var upper))
        {
            StatusText = Resources.M_RangeInvalid;
            return;
        }

        var selected = EntropySources.Where(option => option.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText = Resources.M_NoEntropySource;
            return;
        }

        IsBusy = true;
        StatusText = Resources.M_Generating;

        try
        {
            var seed = await DeriveSeedAsync(selected).ConfigureAwait(true);
            var value = RejectionSampler.Sample(lower, upper, seed);

            ResultText = string.Format(Resources.M_ResultFormat, value);
            StatusText = IsVerifiabilityReduced
                ? Resources.M_GeneratedNotVerifiable
                : Resources.M_GeneratedVerifiable;
        }
        catch (Exception exception)
        {
            // A fetch failure must read as "nothing was produced" rather than as a number: showing a value derived
            // from fewer sources than the user picked would misrepresent where the entropy came from.
            _logger.LogWarning(exception, "裸随机数生成失败");
            StatusText = string.Format(Resources.M_GenerateFailedFormat, exception.Message);
            ResultText = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    ///     Hashes every selected source's published output, in catalog order, into one 32-byte seed. Local CSPRNG
    ///     material is hashed in the same way; the domain separator keeps this derivation apart from the draw seed
    ///     derivations even though both end up as a SHA-256 digest.
    /// </summary>
    private async Task<byte[]> DeriveSeedAsync(IReadOnlyList<EntropySourceOption> selected)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes("VeriRandomRaw/v1"));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        foreach (var option in selected)
        {
            hash.AppendData(Encoding.ASCII.GetBytes(option.Id));

            if (option.Id == LocalSourceId)
            {
                hash.AppendData(RandomNumberGenerator.GetBytes(32));
                continue;
            }

            var source = BeaconSourceCatalog.All.First(candidate => candidate.Id == option.Id);
            var pulse = await _entropySourceClient
                .FetchLatestAsync(source, new Uri(source.Endpoint), cancellation.Token)
                .ConfigureAwait(true);

            hash.AppendData(Encoding.ASCII.GetBytes(pulse.OutputValue));
        }

        return hash.GetHashAndReset();
    }

    private bool TryReadRange(out long lower, out long upper)
    {
        lower = 0;
        upper = 0;

        return long.TryParse(LowerBoundText, out lower)
               && long.TryParse(UpperBoundText, out upper)
               && upper >= lower;
    }

    private static string DisplayNameOf(BeaconSource source) => source.Kind switch
    {
        BeaconSourceKind.NistBeaconV2 => Resources.C_SourceNist,
        BeaconSourceKind.DrandChain => Resources.C_SourceDrand,
        BeaconSourceKind.RandomOrgEntropy => Resources.C_SourceRandomOrg,
        _ => source.Id
    };

    /// <summary>
    ///     random.org publishes neither timeline nor signature over a fetched block, so a verifier cannot tell
    ///     which block was used or how many were fetched — the very property NIST and drand provide.
    /// </summary>
    private static bool IsVerifiable(BeaconSource source) => source.Kind is BeaconSourceKind.NistBeaconV2
        or BeaconSourceKind.DrandChain;

    private void OnEntropySourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EntropySourceOption.IsSelected))
            RefreshVerifiabilityNotice();
    }

    private void RefreshVerifiabilityNotice()
    {
        var selected = EntropySources.Where(option => option.IsSelected).ToList();

        IsVerifiabilityReduced = selected.Count != 1 || !selected[0].IsVerifiable;
        VerifiabilityNotice = selected.Count switch
        {
            0 => Resources.M_NoEntropySource,
            _ when !IsVerifiabilityReduced => Resources.M_VerifiableNotice,
            _ => Resources.M_NotVerifiableNotice
        };
    }

    partial void OnLowerBoundTextChanged(string value) => PersistRange();

    partial void OnUpperBoundTextChanged(string value) => PersistRange();

    partial void OnIsSettingsLockedChanged(bool value)
    {
        if (_isLoading)
            return;

        _configHandler.Data.MoreSettings.RawRandomSettingsLocked = value;
        _configHandler.Save();
    }

    /// <summary>
    ///     Saves both bounds together on every edit, but only when both parse and the range is consistent: a
    ///     half-typed "1" on the way to "100" must not overwrite the stored upper bound with a smaller one.
    /// </summary>
    private void PersistRange()
    {
        if (_isLoading)
            return;

        if (!TryReadRange(out var lower, out var upper))
            return;

        var more = _configHandler.Data.MoreSettings;
        if (more.RawRandomLowerBound == lower && more.RawRandomUpperBound == upper)
            return;

        more.RawRandomLowerBound = lower;
        more.RawRandomUpperBound = upper;
        _configHandler.Save();
    }
}

/// <summary>One selectable entropy source on the raw-number page.</summary>
public partial class EntropySourceOption(string id, string displayName, bool isVerifiable) : ObservableObject
{
    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    /// <summary>False for random.org and the local CSPRNG, which cannot be re-checked after the fact.</summary>
    public bool IsVerifiable { get; } = isVerifiable;

    [ObservableProperty] private bool _isSelected;
}
