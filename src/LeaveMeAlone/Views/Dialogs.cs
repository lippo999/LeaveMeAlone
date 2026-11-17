using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LeaveMeAlone.Models;
using LeaveMeAlone.Services;
using LeaveMeAlone.Services.Unblock;
using LeaveMeAlone.Services.Unblock.Scan;

namespace LeaveMeAlone.Views;

/// <summary>
/// Small modal dialogs built in code (no XAML) so they can share the app theme brushes
/// and stay in a single file. Each returns null when the user cancels.
/// </summary>
public static class Dialogs
{
    public static string? PromptText(Window owner, string title, string prompt, string initial = "")
    {
        var window = NewDialog(owner, title, 440, 215);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Label(prompt));

        var box = NewTextBox(initial);
        box.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(box);

        string? result = null;
        panel.Children.Add(ButtonRow(
            ("OK", true, () =>
            {
                if (string.IsNullOrWhiteSpace(box.Text)) return;
                result = box.Text.Trim();
                window.DialogResult = true;
            }),
            ("Cancel", false, () => window.DialogResult = false)));

        window.Content = panel;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? result : null;
    }

    public static FirewallTarget? PromptIpTarget(Window owner)
    {
        var window = NewDialog(owner, "Add IP / server", 470, 380);
        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(Label("Remote IP address or CIDR range (e.g. 203.0.113.5 or 203.0.113.0/24)"));
        var address = NewTextBox("");
        address.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(address);

        panel.Children.Add(Label("Remote port (optional, e.g. 27015). Leave blank for all ports."));
        var port = NewTextBox("");
        port.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(port);

        panel.Children.Add(Label("Protocol"));
        var protocol = new ComboBox
        {
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brush("Text"),
            Background = Brush("SurfaceAlt"),
        };
        protocol.Items.Add("Any");
        protocol.Items.Add("TCP");
        protocol.Items.Add("UDP");
        protocol.SelectedIndex = 0;
        panel.Children.Add(protocol);

        FirewallTarget? result = null;
        panel.Children.Add(ButtonRow(
            ("Add", true, () =>
            {
                if (string.IsNullOrWhiteSpace(address.Text)) return;
                result = new FirewallTarget
                {
                    Type = TargetType.RemoteAddress,
                    Value = address.Text.Trim(),
                    RemotePort = string.IsNullOrWhiteSpace(port.Text) ? null : port.Text.Trim(),
                    Protocol = (protocol.SelectedItem as string) ?? "Any",
                    DisplayName = address.Text.Trim(),
                };
                window.DialogResult = true;
            }),
            ("Cancel", false, () => window.DialogResult = false)));

        window.Content = panel;
        window.Loaded += (_, _) => address.Focus();
        return window.ShowDialog() == true ? result : null;
    }

    public static IReadOnlyList<FirewallTarget>? PromptCommonLaunchers(Window owner, IReadOnlyList<DetectedLauncher> detected)
    {
        var window = NewDialog(owner, "Add common launcher", 520, 460);
        var root = new DockPanel { Margin = new Thickness(20) };

        var header = Label("Detected launchers on this PC. Check the executables to block:");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var checks = new List<(CheckBox box, string launcher, string path)>();
        var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var launcher in detected)
        {
            list.Children.Add(new TextBlock
            {
                Text = launcher.Name,
                Foreground = Brush("Text"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 4),
            });
            foreach (var path in launcher.ExistingPaths)
            {
                var box = new CheckBox
                {
                    Content = path,
                    IsChecked = true,
                    Foreground = Brush("TextMuted"),
                    Margin = new Thickness(6, 2, 0, 2),
                };
                checks.Add((box, launcher.Name, path));
                list.Children.Add(box);
            }
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        };

        IReadOnlyList<FirewallTarget>? result = null;
        var buttons = ButtonRow(
            ("Add selected", true, () =>
            {
                result = checks
                    .Where(c => c.box.IsChecked == true)
                    .Select(c => CommonLaunchers.ToTarget(c.launcher, c.path))
                    .ToList();
                window.DialogResult = true;
            }),
            ("Cancel", false, () => window.DialogResult = false));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(scroll);

        window.Content = root;
        return window.ShowDialog() == true ? result : null;
    }

    /// <summary>Lets the user pick which blocked Steam servers found by a scan get unblocked (all checked by default).</summary>
    public static IReadOnlyList<ScanFinding>? PromptScanFindings(Window owner, IReadOnlyList<ScanFinding> findings)
    {
        var window = NewDialog(owner, "Blocked Steam servers found", 600, 500);
        var root = new DockPanel { Margin = new Thickness(20) };

        var header = Label($"{findings.Count} Steam server(s) are blocked on this network and aren't rerouted yet. " +
                           "Check the ones to fix — only these hosts are changed:");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var checks = new List<(CheckBox box, ScanFinding finding)>();
        var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var finding in findings)
        {
            var box = new CheckBox
            {
                IsChecked = true,
                Margin = new Thickness(0, 3, 0, 3),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = finding.Domain, Foreground = Brush("Text") },
                        new TextBlock
                        {
                            Text = $"{ScanKindLabel(finding.Diagnosis)} · found in {string.Join(", ", finding.Sources)}",
                            Foreground = Brush("TextFaint"),
                            FontSize = 11.5,
                        },
                    },
                },
            };
            checks.Add((box, finding));
            list.Children.Add(box);
        }

        IReadOnlyList<ScanFinding>? result = null;
        var buttons = ButtonRow(
            ("Fix selected", true, () =>
            {
                result = checks.Where(c => c.box.IsChecked == true).Select(c => c.finding).ToList();
                window.DialogResult = true;
            }),
            ("Cancel", false, () => window.DialogResult = false));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });

        window.Content = root;
        return window.ShowDialog() == true ? result : null;
    }

    private static string ScanKindLabel(DomainDiagnosis d) => d.SniBlocked
        ? "SNI filtered → local relay"
        : "DNS poisoned → pin real IP";

    // ----- shared helpers -----

    private static Window NewDialog(Window owner, string title, double width, double height) => new Window
    {
        Title = title,
        Owner = owner,
        Width = width,
        Height = height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ResizeMode = ResizeMode.NoResize,
        WindowStyle = WindowStyle.ToolWindow,
        Background = Brush("Surface"),
        FontFamily = (FontFamily)Application.Current.Resources["FontUi"],
        ShowInTaskbar = false,
    }.WithDarkCaption();

    private static Window WithDarkCaption(this Window window)
    {
        WindowTheme.UseDarkCaption(window);
        return window;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = Brush("TextMuted"),
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0),
    };

    private static TextBox NewTextBox(string initial) => new()
    {
        Text = initial,
        Style = (Style)Application.Current.Resources["Field"],
    };

    private static StackPanel ButtonRow(params (string text, bool primary, Action onClick)[] defs)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        foreach (var def in defs)
        {
            var button = new Button
            {
                Content = def.text,
                MinWidth = 90,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)Application.Current.Resources[def.primary ? "BtnPrimary" : "BtnBase"],
            };
            button.Click += (_, _) => def.onClick();
            row.Children.Add(button);
        }
        return row;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
