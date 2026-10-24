using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SecRandom.Core.Attributes;
using SecRandom.Core.Enums;
using SecRandom.Core.Icons;
using SecRandom.Langs.MainPages.RawRandom;
using SecRandom.ViewModels.MainPages;

namespace SecRandom.Views.MainPages;

/// <summary>
///     主界面「随机数」页：在给定范围内生成一个裸随机数。
/// </summary>
[PageInfo("main.rawRandom", FluentIcons.NumberSymbolFilled, location: PageLocation.Bottom, hidePageTitle: true)]
public sealed partial class RawRandomPage : UserControl
{
    public RawRandomPage(RawRandomPageViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }

    public RawRandomPageViewModel ViewModel { get; }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
