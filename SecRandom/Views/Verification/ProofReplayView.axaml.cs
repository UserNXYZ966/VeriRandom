using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using SecRandom.Core.Services.Verification;
using SecRandom.Core.Views;
using SecRandom.Services.Verification;
using SecRandom.Shared.Models.Verification;
using LR = SecRandom.Langs.SettingsPages.General.Verification.Resources;

namespace SecRandom.Views.Verification;

/// <summary>
///     The proof replay window: a read-only canvas that lays the retained proofs out in hash-chain order with their
///     draw time, and a panel that replays and grades whichever node the user selects.
/// </summary>
/// <remarks>
///     The canvas is display and selection only — nothing on it is editable or draggable, because its whole point is
///     to show what the chain actually recorded rather than a picture the user could reshape. Replay and the evidence
///     score live in the same panel on purpose: both answer questions about the same node, and the score is what says
///     whether the record being replayed is itself backed by anything.
/// </remarks>
public sealed partial class ProofReplayView : ViewBase
{
    private const double NodeSpacing = 136;
    private const double NodeWidth = 112;
    private const double NodeHeight = 60;
    private const double NodeTop = 84;

    private readonly ProofReplayFileService _replayService;
    private readonly Dictionary<string, Border> _nodeBorders = [];
    private IReadOnlyList<ProofReplayNode> _nodes = [];
    /// <summary>Folder the nodes were read from; null means this installation's own proof store.</summary>
    private string? _root;
    private ProofReplayNode? _selected;
    private ProofRosterContext? _roster;

    public ProofReplayView(ProofReplayFileService replayService)
    {
        _replayService = replayService;
        Header = LR.S_ProofReplay;
        DataContext = this;
        InitializeComponent();

        LoadNodes();
    }

    private void LoadNodes()
    {
        _nodes = _replayService.LoadNodes(_root);
        RenderChain();

        var summary = _nodes.Count == 0
            ? LR.M_ProofReplay_Empty
            : string.Format(LR.M_ProofReplay_Summary, _nodes.Count, _nodes[0].ChainIndex, _nodes[^1].ChainIndex);

        // The folder is stated before the counts: after loading someone else's proofs, "which chain am I looking at"
        // is the first thing a reviewer needs to be sure of.
        SummaryText.Text = _root is null ? summary : $"{_root}{Environment.NewLine}{summary}";

        _selected = null;
        ReplayButton.IsEnabled = false;
        UseCurrentRosterButton.IsEnabled = false;
        AssessTrustButton.IsEnabled = false;
        SelectedTitleText.Text = LR.M_ProofReplay_SelectHint;
        SelectedDetailText.Text = string.Empty;
    }

    /// <summary>
    ///     Reads proofs from any folder the user picks, so a record produced on another machine can be replayed and
    ///     graded here. The folder is searched recursively, which matches the local <c>&lt;yyyy-MM&gt;/&lt;date&gt;</c>
    ///     layout an exported proof store keeps.
    /// </summary>
    private async void LoadFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LR.S_ProofFolder,
            AllowMultiple = false
        });

        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;

        _root = path;
        LoadNodes();
    }

    private void RenderChain()
    {
        ChainCanvas.Children.Clear();
        _nodeBorders.Clear();

        if (_nodes.Count == 0)
            return;

        ChainCanvas.Width = Math.Max(360, 24 + _nodes.Count * NodeSpacing);

        for (var position = 0; position < _nodes.Count; position++)
        {
            var node = _nodes[position];
            var left = 24 + position * NodeSpacing;

            if (position > 0)
            {
                var link = new Line
                {
                    StartPoint = new Point(left - NodeSpacing + NodeWidth, NodeTop + NodeHeight / 2),
                    EndPoint = new Point(left, NodeTop + NodeHeight / 2)
                };
                link.Classes.Add("chainLink");
                ChainCanvas.Children.Add(link);
            }

            var border = new Border
            {
                Child = new StackPanel
                {
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = node.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                            FontSize = 12,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = $"#{node.ChainIndex}",
                            FontSize = 11,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                        }
                    }
                }
            };
            border.Classes.Add("chainNode");

            border.PointerPressed += (_, _) => Select(node);
            Canvas.SetLeft(border, left);
            Canvas.SetTop(border, NodeTop);
            ChainCanvas.Children.Add(border);
            _nodeBorders[node.Path] = border;
        }
    }

    private void Select(ProofReplayNode node)
    {
        _selected = node;

        foreach (var (path, border) in _nodeBorders)
            SetSelectedClass(border, string.Equals(path, node.Path, StringComparison.Ordinal));

        SelectedTitleText.Text = $"#{node.ChainIndex}  {node.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        SelectedDetailText.Text = string.Format(
            LR.M_ProofReplay_NodeDetail,
            node.Kind == VerificationDrawKind.Student ? LR.O_ProofReplay_Students : LR.O_ProofReplay_Prizes,
            node.WinnerCount,
            node.CandidateCount,
            node.FileName,
            node.CanReplay ? string.Empty : LR.M_ProofReplay_NotReplayable);

        ReplayButton.IsEnabled = node.CanReplay;
        UseCurrentRosterButton.IsEnabled = true;
        ReplayStatusText.Text = string.Empty;
        ReplayDetailText.Text = string.Empty;
        AssessTrustButton.IsEnabled = true;
        TrustResultText.Text = string.Empty;
    }

    /// <summary>
    ///     Scrolls to and selects the newest proof, which is also the chain tail and therefore the rightmost node.
    ///     Proofs are laid out oldest-first, so with a long chain the node the user almost always wants is the one
    ///     that starts off-screen.
    /// </summary>
    private void JumpLatest_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_nodes.Count == 0)
            return;

        Select(_nodes[^1]);

        // Extent is still the pre-layout value while the click is being handled, and an offset clamped against it
        // snaps back to 0, so the scroll is posted after this layout pass instead of applied here.
        Dispatcher.UIThread.Post(
            () => ChainScroll.Offset = new Vector(
                Math.Max(0, ChainScroll.Extent.Width - ChainScroll.Viewport.Width),
                ChainScroll.Offset.Y),
            DispatcherPriority.Background);
    }

    /// <summary>
    ///     Takes the roster that is active right now, for the draw kind of the selected node, and shows it first.
    /// </summary>
    /// <remarks>
    ///     The preview is not decoration: this input is the only thing the window does not take from the proof, so the
    ///     user has to be able to confirm that the list about to be used really is the one the class is looking at.
    ///     The draw kind comes from the selected node, so a prize draw previews the current prize pool.
    /// </remarks>
    private async void UseCurrentRoster_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } node || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        if (!_replayService.TryLoadActiveRoster(node.Kind, out var snapshot) || snapshot is null)
        {
            RosterText.Text = string.Format(LR.M_ProofReplay_NoActiveRoster, KindLabel(node.Kind));
            return;
        }

        var dialog = new FAContentDialog
        {
            Title = LR.C_ProofReplay_UseCurrentRoster,
            Content = BuildRosterPreview(snapshot),
            PrimaryButtonText = LR.C_ProofReplay_UseThisRoster,
            CloseButtonText = LR.C_ProofReplay_Cancel,
            DefaultButton = FAContentDialogButton.Close
        };

        if (await dialog.ShowAsync(topLevel) != FAContentDialogResult.Primary)
            return;

        _roster = snapshot.Context;
        RosterText.Text = string.Format(LR.M_ProofReplay_RosterLoaded, KindLabel(node.Kind), snapshot.Members.Count);
    }

    /// <summary>
    ///     Read-only listing of the members the reference is about to cover. It shows what the digest actually
    ///     commits — number and name, plus the group/gender or quantity that also enters it — so a wrong list is
    ///     recognisable before it is used.
    /// </summary>
    private static Control BuildRosterPreview(ProofRosterSnapshot snapshot)
    {
        var rows = new StackPanel { Spacing = 2 };
        foreach (var member in snapshot.Members)
        {
            var row = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8
            };

            row.Children.Add(new TextBlock { Text = member.Number, MinWidth = 72, FontSize = 12 });
            row.Children.Add(new TextBlock { Text = member.Name, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if (member.Trailing.Length > 0)
                row.Children.Add(new TextBlock { Text = member.Trailing, FontSize = 12 });
            if (!member.Enabled)
            {
                row.Children.Add(new TextBlock
                {
                    Text = LR.M_ProofReplay_RosterDisabled,
                    FontSize = 12,
                    FontStyle = Avalonia.Media.FontStyle.Italic
                });
            }

            rows.Children.Add(row);
        }

        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = snapshot.Name,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.Format(
                LR.M_ProofReplay_RosterLoaded,
                snapshot.Context.Kind == "prize" ? LR.O_ProofReplay_Prizes : LR.O_ProofReplay_Students,
                snapshot.Members.Count),
            FontSize = 12
        });
        panel.Children.Add(new ScrollViewer
        {
            Content = rows,
            MaxHeight = 320,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });

        return panel;
    }

    private static string KindLabel(VerificationDrawKind kind) =>
        kind == VerificationDrawKind.Student ? LR.O_ProofReplay_Students : LR.O_ProofReplay_Prizes;

    private async void LoadRoster_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LR.C_ProofReplay_LoadRoster,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;

        var roster = _replayService.LoadRoster(path, out var error);
        if (roster is null)
        {
            _roster = null;
            RosterText.Text = string.Format(LR.M_ProofReplay_RosterFailed, error ?? string.Empty);
            return;
        }

        _roster = roster;
        RosterText.Text = string.Format(
            LR.M_ProofReplay_RosterLoaded,
            roster.Kind == "prize" ? LR.O_ProofReplay_Prizes : LR.O_ProofReplay_Students,
            roster.Names.Count);
    }

    private void Replay_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } node)
            return;

        var outcome = _replayService.Replay(node, _roster);

        ReplayStatusText.Text = outcome.Report.Status switch
        {
            ProofReplayStatus.Reproduced => LR.M_ProofReplay_Reproduced,
            ProofReplayStatus.Mismatch => LR.M_ProofReplay_Mismatch,
            ProofReplayStatus.NoLocalSeed => LR.M_ProofReplay_NoLocalSeed,
            _ => LR.M_ProofReplay_Unsupported
        };

        var lines = new List<string>();
        if (outcome.Report.Detail is { Length: > 0 } detail)
            lines.Add(detail);

        if (outcome.RosterMatches is { } matches)
        {
            lines.Add(matches ? LR.M_ProofReplay_RosterMatches : LR.M_ProofReplay_RosterDiffers);
            lines.Add(string.Format(LR.M_ProofReplay_RecordedNames, string.Join("、", outcome.RecordedWinnerNames)));
            if (outcome.Report.Status == ProofReplayStatus.Mismatch)
                lines.Add(string.Format(LR.M_ProofReplay_ReplayedNames, string.Join("、", outcome.ReplayedWinnerNames)));
        }
        else
        {
            // Without a roster only the stable record ids can be shown; they are what a roster would resolve.
            lines.Add(string.Format(
                LR.M_ProofReplay_RecordedIds,
                string.Join("、", outcome.Report.RecordedWinners.Select(winner => winner.RecordId.ToString("N")[..8]))));
        }

        ReplayDetailText.Text = string.Join("\n", lines);
    }

    /// <summary>
    ///     Grades the selected node against the sources the user trusts. It grades the selection rather than "the
    ///     newest proof" because the same window is where the user chose the node to look at.
    /// </summary>
    private void AssessTrust_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } node)
            return;

        var trusted = new List<ProofTrustSource>();
        if (TrustBeacon.IsChecked == true)
            trusted.Add(ProofTrustSource.BeaconProvider);
        if (TrustTimestamp.IsChecked == true)
            trusted.Add(ProofTrustSource.TimestampAuthority);
        if (TrustServer.IsChecked == true)
            trusted.Add(ProofTrustSource.SectlServer);
        if (TrustWitness.IsChecked == true)
            trusted.Add(ProofTrustSource.SocialWitness);

        if (trusted.Count == 0)
        {
            TrustResultText.Text = LR.M_ProofTrust_SelectSource;
            return;
        }

        var report = _replayService.AssessTrust(node, trusted, ReadWitnessTime(), SelectedWitnessConfidence());
        TrustResultText.Text = FormatTrustReport(report);
    }

    /// <summary>A remembered draw time only counts when both halves were supplied; otherwise the factor is absent.</summary>
    private DateTimeOffset? ReadWitnessTime()
    {
        if (WitnessDate.SelectedDate is not { } date || WitnessTime.SelectedTime is not { } time)
            return null;

        var local = date.Date + time;
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private WitnessTimeConfidence SelectedWitnessConfidence() => WitnessConfidence.SelectedIndex switch
    {
        1 => WitnessTimeConfidence.Medium,
        2 => WitnessTimeConfidence.Low,
        _ => WitnessTimeConfidence.High
    };

    private static string TrustSourceLabel(ProofTrustSource source) => source switch
    {
        ProofTrustSource.BeaconProvider => LR.C_ProofTrust_Beacon,
        ProofTrustSource.TimestampAuthority => LR.C_ProofTrust_Timestamp,
        ProofTrustSource.SectlServer => LR.C_ProofTrust_Server,
        _ => LR.C_ProofTrust_Witness
    };

    private static string FormatTrustReport(ProofTrustReport report)
    {
        List<string> lines =
        [
            string.Format(CultureInfo.CurrentCulture, LR.M_ProofTrust_Score, report.Score)
        ];

        if (report.IsZeroed)
            lines.Add(LR.M_ProofTrust_Zeroed);
        if (report.PulsePenalized)
        {
            lines.Add(report.PulseTier == ProofTrustPulseTier.BeyondTolerance
                ? LR.M_ProofTrust_PulseExpired
                : LR.M_ProofTrust_PreviousPeriod);
        }

        foreach (var factor in report.Factors)
        {
            var name = TrustSourceLabel(factor.Source);
            lines.Add(factor.State switch
            {
                ProofTrustFactorState.Satisfied =>
                    string.Format(CultureInfo.CurrentCulture, LR.M_ProofTrust_Factor_Ok, name),
                ProofTrustFactorState.PartiallySatisfied =>
                    string.Format(CultureInfo.CurrentCulture, LR.M_ProofTrust_Factor_Partial,
                        name, factor.ErrorSeconds ?? 0d, factor.ToleranceSeconds ?? 0d),
                _ => string.Format(CultureInfo.CurrentCulture, LR.M_ProofTrust_Factor_Missing, name)
            });
        }

        return string.Join(Environment.NewLine, lines);
    }

    // 选中态用类名切换，而不是在代码里塞颜色：样式表里定义观感，代码只负责"谁被选中"。
    private static void SetSelectedClass(Border border, bool selected)
    {
        if (selected)
        {
            if (!border.Classes.Contains("selected"))
                border.Classes.Add("selected");
        }
        else
        {
            border.Classes.Remove("selected");
        }
    }
}
