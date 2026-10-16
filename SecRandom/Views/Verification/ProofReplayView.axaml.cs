using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SecRandom.Core.Services.Verification;
using SecRandom.Core.Views;
using SecRandom.Services.Verification;
using SecRandom.Shared.Models.Verification;
using LR = SecRandom.Langs.SettingsPages.General.Verification.Resources;

namespace SecRandom.Views.Verification;

/// <summary>
///     The proof replay window: a read-only canvas that lays the retained proofs out in hash-chain order with their
///     draw time, and a panel that replays whichever node the user selects.
/// </summary>
/// <remarks>
///     The canvas is display and selection only — nothing on it is editable or draggable, because its whole point is
///     to show what the chain actually recorded rather than a picture the user could reshape.
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
        _nodes = _replayService.LoadNodes();
        RenderChain();

        SummaryText.Text = _nodes.Count == 0
            ? LR.M_ProofReplay_Empty
            : string.Format(LR.M_ProofReplay_Summary, _nodes.Count, _nodes[0].ChainIndex, _nodes[^1].ChainIndex);
        SelectedTitleText.Text = LR.M_ProofReplay_SelectHint;
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
                    StartPoint = new Avalonia.Point(left - NodeSpacing + NodeWidth, NodeTop + NodeHeight / 2),
                    EndPoint = new Avalonia.Point(left, NodeTop + NodeHeight / 2),
                    Classes = { "chainLink" }
                };
                ChainCanvas.Children.Add(link);
            }

            var border = new Border
            {
                Classes = { "chainNode" },
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
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            Foreground = TryBrush("TextFillColorSecondaryBrush")
                        }
                    }
                }
            };

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
            border.Classes.Set("selected", string.Equals(path, node.Path, StringComparison.Ordinal));

        SelectedTitleText.Text = $"#{node.ChainIndex}  {node.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        SelectedDetailText.Text = string.Format(
            LR.M_ProofReplay_NodeDetail,
            node.Kind == VerificationDrawKind.Student ? LR.O_ProofReplay_Students : LR.O_ProofReplay_Prizes,
            node.WinnerCount,
            node.CandidateCount,
            node.FileName,
            node.CanReplay ? string.Empty : LR.M_ProofReplay_NotReplayable);

        ReplayButton.IsEnabled = node.CanReplay;
        ReplayStatusText.Text = string.Empty;
        ReplayDetailText.Text = string.Empty;
    }

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

    private IBrush? TryBrush(string key) =>
        this.TryFindResource(key, out var resource) && resource is IBrush brush ? brush : null;
}
