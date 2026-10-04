using System.Drawing;
using System.Windows.Forms;
using LeaveMeAlone.ViewModels;

namespace LeaveMeAlone.Views;

/// <summary>
/// System-tray icon with a quick-toggle menu. Uses the WinForms <see cref="NotifyIcon"/>
/// (available via &lt;UseWindowsForms&gt;) hosted inside the WPF app. WPF types are fully
/// qualified to avoid ambiguity with the WinForms names in scope.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Window _window;
    private readonly MainViewModel _viewModel;

    public TrayIcon(System.Windows.Window window, MainViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "LeaveMeAlone",
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        BuildMenu();
    }

    private void BuildMenu()
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();

        menu.Items.Add(new ToolStripMenuItem("LeaveMeAlone") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        if (_viewModel.Bundles.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("No bundles yet") { Enabled = false });
        }
        else
        {
            foreach (var bundle in _viewModel.Bundles)
            {
                var captured = bundle;
                var item = new ToolStripMenuItem(bundle.Name)
                {
                    Checked = bundle.Enabled,
                    CheckOnClick = false,
                    Enabled = _viewModel.IsElevated,
                };
                item.Click += async (_, _) => await _viewModel.ToggleAsync(captured, !captured.Enabled);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new ToolStripSeparator());

        var show = new ToolStripMenuItem("Show window");
        show.Click += (_, _) => ShowWindow();
        menu.Items.Add(show);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        menu.Items.Add(exit);
    }

    // The app icon is a WPF resource (Assets/app.ico); fall back to a stock icon if it can't be read.
    private static Icon LoadAppIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        if (resource is null) return SystemIcons.Shield;
        using var stream = resource.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = System.Windows.WindowState.Normal;
        _window.Activate();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}
