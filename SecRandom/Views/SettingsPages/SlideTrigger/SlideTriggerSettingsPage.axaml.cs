using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SecRandom.Core.Abstraction;
using SecRandom.Core.Attributes;
using SecRandom.Core.Icons;
using SecRandom.Core.Models.SlideTrigger;
using SecRandom.Langs.SettingsPages.SlideTrigger;
using SecRandom.Services.SlideTrigger;

namespace SecRandom.Views.SettingsPages.SlideTrigger;

/// <summary>
///     Where a teacher creates the markers that go into their courseware, and manages the ones already made.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately a settings page rather than a classroom control surface: a marker is prepared while assembling a
///         deck, never in front of a class. Keeping it out of the floating window also means the draw-time UI stays as
///         small as it is.
///     </para>
///     <para>
///         Parameter rows are read by name from the page instead of through a view model. There is no shared state to
///         keep in sync here — the marker library is the source of truth and it lives in <see cref="SlideTriggerStore" /> —
///         so a view model would only add a second copy that can disagree with it.
///     </para>
/// </remarks>
[PageInfo("settings.slideTrigger", FluentIcons.NumberSymbolFilled)]
public sealed partial class SlideTriggerSettingsPage : UserControl
{
    private readonly SlideTriggerStore _store;
    private readonly SlideTriggerMarkerImageService _images;

    public SlideTriggerSettingsPage()
    {
        _store = IAppHost.GetService<SlideTriggerStore>();
        _images = IAppHost.GetService<SlideTriggerMarkerImageService>();

        DataContext = this;
        InitializeComponent();
        RefreshLibrary();
    }

    // ---------------------------------------------------------------- generation

    private async void Generate_OnClick(object? sender, RoutedEventArgs e) => await GenerateAsync(saveToFile: false);

    private async void GenerateAndSave_OnClick(object? sender, RoutedEventArgs e) => await GenerateAsync(saveToFile: true);

    private async Task GenerateAsync(bool saveToFile)
    {
        var parameters = ReadParameters();
        var expiresAt = ReadExpiry();

        SlideTriggerDefinition definition;
        try
        {
            definition = _store.Create(NameBox.Text ?? string.Empty, parameters, expiresAt);
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = string.Format(Resources.M_GenerateFailedFormat, exception.Message);
            return;
        }

        if (saveToFile)
        {
            var path = await _images.ExportAsync(
                TopLevel.GetTopLevel(this),
                definition.MarkerText,
                SafeFileName(definition.Name),
                Resources.S_ExportPicker,
                Resources.C_PngFileType);

            StatusText.Text = path is null
                ? Resources.M_ExportCancelled
                : string.Format(Resources.M_SavedFormat, path);
        }
        else
        {
            var copied = await _images.TryCopyToClipboardAsync(TopLevel.GetTopLevel(this), definition.MarkerText);
            StatusText.Text = copied
                ? string.Format(Resources.M_CopiedFormat, Describe(definition))
                : Resources.M_ClipboardUnavailable;
        }

        RefreshLibrary();
    }

    private SlideTriggerParameters ReadParameters() => new()
    {
        StudentListName = ListBox.Text,
        Count = (int)(CountBox.Value ?? 1),
        AllowRepeat = AllowRepeatBox.IsChecked == true,
        IgnoreHistoryWeights = IgnoreWeightsBox.IsChecked == true,
        ShowAnimation = ShowAnimationBox.IsChecked == true,
        VoiceEnabled = VoiceBox.IsChecked == true,
        DelaySeconds = (double)(DelayBox.Value ?? 1),
        TriggerAfterInputs = (int)(InputsBox.Value ?? 1),
        ArmedTimeoutSeconds = (double)(TimeoutBox.Value ?? 15),
        Timing = TimingBox.SelectedIndex == 1
            ? SlideTriggerTiming.AfterInputs
            : SlideTriggerTiming.AfterDelay,
        Gender = GenderBox.Text,
        Group = GroupBox.Text
    };

    /// <summary>Zero days means "never expires", which is what a re-used deck wants.</summary>
    private DateTimeOffset? ReadExpiry()
    {
        var days = (int)(ExpiresBox.Value ?? 0);
        return days <= 0 ? null : DateTimeOffset.UtcNow.AddDays(days);
    }

    // ---------------------------------------------------------------- library

    private void NewSession_OnClick(object? sender, RoutedEventArgs e)
    {
        SessionText.Text = string.Format(Resources.M_SessionFormat, _store.StartNewSession());
        StatusText.Text = Resources.M_SessionRolled;
        RefreshLibrary();
    }

    private void RefreshLibrary()
    {
        SessionText.Text = string.Format(Resources.M_SessionFormat, _store.Session);
        EmptyHint.IsVisible = _store.Triggers.Count == 0;

        MarkerRows.Children.Clear();
        foreach (var definition in _store.Triggers.OrderByDescending(item => item.CreatedAt))
            MarkerRows.Children.Add(BuildRow(definition));
    }

    private Control BuildRow(SlideTriggerDefinition definition)
    {
        var summary = new TextBlock
        {
            Text = $"{definition.Name} · {Describe(definition)}",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var used = definition.IsConsumedIn(_store.Session);
        var state = new TextBlock
        {
            Text = used ? Resources.M_UsedThisSession : Resources.M_NotUsed,
            FontSize = 12,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center
        };

        var copy = new Button { Content = Resources.C_Copy, Tag = definition };
        copy.Click += CopyMarker_OnClick;

        var export = new Button { Content = Resources.C_SaveAs, Tag = definition };
        export.Click += ExportMarker_OnClick;

        var remove = new Button { Content = Resources.C_Delete, Tag = definition };
        remove.Click += DeleteMarker_OnClick;

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { copy, export, remove }
        };

        return new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Colors.Gray, 0.25),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 4,
                        Children = { summary, state }
                    },
                    actions
                }
            }
        };
    }

    private async void CopyMarker_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SlideTriggerDefinition definition })
            return;

        var copied = await _images.TryCopyToClipboardAsync(TopLevel.GetTopLevel(this), definition.MarkerText);
        StatusText.Text = copied
            ? string.Format(Resources.M_CopiedFormat, Describe(definition))
            : Resources.M_ClipboardUnavailable;
    }

    private async void ExportMarker_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SlideTriggerDefinition definition })
            return;

        var path = await _images.ExportAsync(
            TopLevel.GetTopLevel(this),
            definition.MarkerText,
            SafeFileName(definition.Name),
            Resources.S_ExportPicker,
            Resources.C_PngFileType);

        if (path is not null)
            StatusText.Text = string.Format(Resources.M_SavedFormat, path);
    }

    private void DeleteMarker_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SlideTriggerDefinition definition })
            return;

        _store.Remove(definition.Id);
        StatusText.Text = string.Format(Resources.M_DeletedFormat, definition.Name);
        RefreshLibrary();
    }

    // ---------------------------------------------------------------- helpers

    private string Describe(SlideTriggerDefinition definition)
    {
        var parameters = definition.Parameters;
        var parts = new List<string>
        {
            $"{parameters.Count} 人",
            parameters.StudentListName is null ? Resources.M_ActiveList : parameters.StudentListName,
            parameters.Timing == SlideTriggerTiming.AfterInputs
                ? string.Format(Resources.M_TimingInputsFormat, parameters.TriggerAfterInputs)
                : string.Format(Resources.M_TimingDelayFormat, parameters.DelaySeconds)
        };

        if (parameters.AllowRepeat)
            parts.Add(Resources.M_AllowRepeatBadge);

        if (parameters.IgnoreHistoryWeights)
            parts.Add(Resources.M_IgnoreWeightsBadge);

        if (definition.ExpiresAt is { } expiry)
            parts.Add(string.Format(Resources.M_ExpiresFormat, expiry.ToLocalTime().ToString("yyyy-MM-dd")));

        return string.Join(" · ", parts);
    }

    private static string SafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "marker" : cleaned.Trim();
    }
}
