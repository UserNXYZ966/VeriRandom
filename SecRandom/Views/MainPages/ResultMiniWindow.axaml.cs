using System;
using System.Linq;
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
///     <para>
///         It hosts the same <see cref="RollCallResultPresenter" /> over the Host's singleton
///         <see cref="RollCallPageViewModel" />, so nothing is copied: the result, its display settings, and its
///         animation all stay the shared session's. Closing always means "go back to the main window", because leaving
///         the app as a floating card with no window is a worse trap than an extra click.
///     </para>
///     <para>
///         The window is borderless, so it has no system frame to grab: both dragging and resizing are ours. Dragging
///         accepts touch as well as a mouse button, and the eight resize grips are sized for fingertips rather than
///         cursors, because this card is mostly used on a classroom touchscreen.
///     </para>
/// </remarks>
public sealed partial class ResultMiniWindow : Window
{
    private readonly Action _restore;
    private bool _allowClose;

    public ResultMiniWindow(RollCallPageViewModel viewModel, Action restore, double width, double height)
    {
        _restore = restore;
        DataContext = viewModel;

        Width = width;
        Height = height;

        InitializeComponent();

        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Closing += OnClosing;

        ConfigureResizeGrips();
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
        if (!IsPrimaryContact(e) || IsButtonDescendant(e.Source as Visual))
            return;

        BeginMoveDrag(e);
    }

    /// <summary>
    ///     One grip per window edge and corner, all going through the platform's own resize loop. Doing it natively
    ///     instead of tracking pointer deltas keeps the drag smooth at any screen scale, and on a touchscreen it is
    ///     what lets the OS keep resizing after the finger has left the grip.
    /// </summary>
    private void ResizeGrip_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string edgeName } || !IsPrimaryContact(e))
            return;

        if (!Enum.TryParse<WindowEdge>(edgeName, out var edge))
            return;

        e.Handled = true;
        BeginResizeDrag(edge, e);
    }

    /// <summary>
    ///     A finger press reports no "left button", so the old mouse-only check silently disabled dragging on the very
    ///     screens this card is made for. Touch contacts are accepted as primary contacts instead.
    /// </summary>
    private bool IsPrimaryContact(PointerPressedEventArgs e) =>
        e.Pointer.Type == PointerType.Touch
        || e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;

    private void ConfigureResizeGrips()
    {
        foreach (var grip in ResizeGrips.Children.OfType<Control>().Where(control => control.Tag is string))
        {
            grip.Cursor = new Cursor(CursorFor((string)grip.Tag!));
        }
    }

    private static StandardCursorType CursorFor(string edgeName) => edgeName switch
    {
        "North" or "South" => StandardCursorType.SizeNorthSouth,
        "West" or "East" => StandardCursorType.SizeWestEast,
        "NorthWest" => StandardCursorType.TopLeftCorner,
        "NorthEast" => StandardCursorType.TopRightCorner,
        "SouthWest" => StandardCursorType.BottomLeftCorner,
        "SouthEast" => StandardCursorType.BottomRightCorner,
        _ => StandardCursorType.Arrow
    };

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
