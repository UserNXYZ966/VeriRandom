using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SecRandom.ViewModels.MainPages;

namespace SecRandom.Views.MainPages;

/// <summary>
///     Borderless topmost window that shows the current roll-call result while the main window steps aside, so a
///     classroom can keep the courseware on screen and still see who answers next.
/// </summary>
/// <remarks>
///     It hosts the same <see cref="RollCallResultPresenter" /> over the Host's singleton
///     <see cref="RollCallPageViewModel" />, so nothing is copied: the result, its display settings, and its
///     animation all stay the shared session's. Closing always means "go back to the main window", because leaving
///     the app as a floating card with no window is a worse trap than an extra click.
/// </remarks>
public sealed partial class ResultMiniWindow : Window
{
    private readonly Action _restore;
    private bool _allowClose;

    public ResultMiniWindow(RollCallPageViewModel viewModel, Action restore)
    {
        _restore = restore;
        DataContext = viewModel;
        InitializeComponent();

        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Closing += OnClosing;
    }

    internal void AllowClose() => _allowClose = true;

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
            return;

        e.Cancel = true;
        _restore();
    }

    private void Restore(object? sender, RoutedEventArgs e) => _restore();

    // 拖动沿用计时器小窗的做法：整块可拖动，但按在按钮上时交给按钮。
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || IsButtonDescendant(e.Source as Visual))
            return;

        BeginMoveDrag(e);
    }

    private static bool IsButtonDescendant(Visual? visual)
    {
        while (visual is not null)
        {
            if (visual is Button)
                return true;
            visual = visual.GetVisualParent();
        }

        return false;
    }
}
