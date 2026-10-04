using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using LeaveMeAlone.ViewModels;

namespace LeaveMeAlone.Views;

/// <summary>Logs page: live activity log with a problems filter, copy, open folder and clear.</summary>
public partial class LogView : UserControl
{
    public LogView() => InitializeComponent();

    private LogViewModel? ViewModel => DataContext as LogViewModel;

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.ProblemsOnly = ProblemsTab.IsChecked == true;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsEmpty) return;
        try
        {
            Clipboard.SetText(ViewModel.ToText());
        }
        catch (COMException)
        {
            // Another app is holding the clipboard; a second click works.
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Folder is not { } folder) return;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ViewModel?.Clear();
}
