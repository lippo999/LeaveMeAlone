using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LeaveMeAlone.Services;
using LeaveMeAlone.ViewModels;

namespace LeaveMeAlone.Views;

/// <summary>Shell: sidebar with the two features (Block / Unblock) as equal pages.</summary>
public partial class MainWindow : Window
{
    private static readonly Duration PageFade = new(TimeSpan.FromMilliseconds(220));
    private const double PageSlide = 10;

    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel, UnblockViewModel unblockViewModel, LogViewModel logViewModel)
    {
        InitializeComponent();
        WindowTheme.UseDarkCaption(this);
        _viewModel = viewModel;

        DataContext = viewModel;
        BlockPage.DataContext = viewModel;
        NavUnblock.DataContext = unblockViewModel;
        UnblockPage.DataContext = unblockViewModel;
        NavLogs.DataContext = logViewModel;
        LogPage.DataContext = logViewModel;

        ShowElevation(viewModel.IsElevated);
        StartupSwitch.IsChecked = StartupRegistration.IsEnabled();

        Loaded += async (_, _) => await BlockPage.InitializeAsync();
        StateChanged += MainWindow_StateChanged;
    }

    private void ShowElevation(bool elevated)
    {
        BannerPanel.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
        AdminDot.Fill = (Brush)FindResource(elevated ? "Signal" : "Warn");
        AdminText.Text = elevated ? "Running as administrator" : "Read-only — not administrator";
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // Minimize hides to the tray; the tray menu (or its "Show window" item) restores it.
        if (WindowState == WindowState.Minimized) Hide();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (BlockPage is null || UnblockPage is null || LogPage is null) return; // fires during InitializeComponent
        FrameworkElement show = sender == NavUnblock ? UnblockPage : sender == NavLogs ? LogPage : BlockPage;
        foreach (var page in new FrameworkElement[] { BlockPage, UnblockPage, LogPage })
            page.Visibility = page == show ? Visibility.Visible : Visibility.Collapsed;
        Animate(show);
    }

    // Short fade + rise so switching pages reads as a change of place.
    private static void Animate(FrameworkElement page)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, PageFade) { EasingFunction = ease });
        if (page.RenderTransform is TranslateTransform slide)
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(PageSlide, 0, PageFade) { EasingFunction = ease });
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        var enable = StartupSwitch.IsChecked == true;
        try
        {
            StartupRegistration.SetEnabled(enable);
            _viewModel.StatusMessage = enable ? "Will start with Windows." : "Will no longer start with Windows.";
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"Failed to update startup setting: {ex.Message}";
            StartupSwitch.SetCurrentValue(ToggleButton.IsCheckedProperty, StartupRegistration.IsEnabled());
        }
    }
}
