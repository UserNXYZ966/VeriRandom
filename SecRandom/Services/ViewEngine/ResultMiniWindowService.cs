using SecRandom.ViewModels.MainPages;
using SecRandom.Views.MainPages;

namespace SecRandom.Services.ViewEngine;

/// <summary>
///     Opens the roll-call result as a borderless topmost window and steps the main window aside, which is the
///     "shrink the result into a floating card" action on the roll-call page.
/// </summary>
/// <remarks>
///     The order is load-bearing: the floating window is shown <em>before</em> the main window is hidden, because
///     Avalonia shuts the application down when the last window closes — the same rule the timer mini window follows.
///     Desktop only: the floating window is a desktop window, and on a phone or tablet the main view hosts the
///     result in place instead.
/// </remarks>
public sealed class ResultMiniWindowService(RollCallPageViewModel viewModel)
{
    private ResultMiniWindow? _window;

    public void Show()
    {
        if (_window is { IsVisible: true })
        {
            _window.Activate();
            return;
        }

        _window = new ResultMiniWindow(viewModel, RestoreMainWindow);
        _window.Closed += (_, _) => _window = null;
        _window.Show();

        App.SetMainWindowVisibility("hide");
    }

    public void RestoreMainWindow()
    {
        CloseWindow();
        App.ShowMainWindow();
    }

    internal void CloseWindow()
    {
        _window?.AllowClose();
        _window?.Close();
        _window = null;
    }
}
