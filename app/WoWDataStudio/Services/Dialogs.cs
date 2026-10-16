using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace WoWDataStudio.Services;

public enum DialogKind { Info, Warning, Error, Question, Success }

/// <summary>Plain dialogs with clearly labelled buttons (no Yes/No guessing).</summary>
public static class Dialogs
{
    public static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    /// <summary>Shows a message; returns the index of the clicked button (0 = first), or -1 when closed.</summary>
    public static int Show(string title, string message, DialogKind kind, string[] buttons, string? details = null, string? linkText = null, string? linkTarget = null)
    {
        var win = Build(title, message, kind, buttons, details, linkText, linkTarget, out var result);
        if (Owner != null && Owner != win && Owner.IsVisible) win.Owner = Owner;
        win.ShowDialog();
        return result();
    }

    public static Window Build(string title, string message, DialogKind kind, string[] buttons, string? details, string? linkText, string? linkTarget, out Func<int> result)
    {
        int clicked = -1;
        var win = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, MaxWidth = 640, MinWidth = 380, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        win.SetResourceReference(FrameworkElement.StyleProperty, "AppWindow");
        var (glyph, brushKey) = kind switch
        {
            DialogKind.Error => ("", "Bad"),
            DialogKind.Warning => ("", "Warn"),
            DialogKind.Question => ("", "Accent"),
            DialogKind.Success => ("", "Good"),
            _ => ("", "Accent"),
        };
        var root = new DockPanel { Margin = new Thickness(20), LastChildFill = true };
        var buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;
            var b = new Button { Content = buttons[i], Margin = new Thickness(8, 0, 0, 0), MinWidth = 90, IsDefault = i == 0, IsCancel = i == buttons.Length - 1 && buttons.Length > 1 };
            b.SetResourceReference(FrameworkElement.StyleProperty, i == 0 ? "PrimaryButton" : "BaseButton");
            b.Click += (_, _) => { clicked = idx; win.Close(); };
            buttonsPanel.Children.Add(b);
        }
        DockPanel.SetDock(buttonsPanel, Dock.Bottom);
        root.Children.Add(buttonsPanel);
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 30, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        DockPanel.SetDock(icon, Dock.Left);
        root.Children.Add(icon);
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 540 });
        if (!string.IsNullOrEmpty(details))
        {
            var tb = new TextBox
            {
                Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, MaxWidth = 540, Margin = new Thickness(0, 10, 0, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            };
            body.Children.Add(tb);
        }
        if (linkText != null && linkTarget != null)
        {
            var link = new Hyperlink(new Run(linkText));
            link.Click += (_, _) => OpenPath(linkTarget);
            body.Children.Add(new TextBlock(link) { Margin = new Thickness(0, 10, 0, 0) });
        }
        root.Children.Add(body);
        win.Content = root;
        result = () => clicked;
        return win;
    }

    public static void Error(string title, string message, string? details = null) => Show(title, message, DialogKind.Error, new[] { "OK" }, details);
    public static void Info(string title, string message, string? details = null) => Show(title, message, DialogKind.Info, new[] { "OK" }, details);
    public static void Success(string title, string message, string? details = null) => Show(title, message, DialogKind.Success, new[] { "OK" }, details);
    public static bool Confirm(string title, string message, string yes, string no = "Cancel", DialogKind kind = DialogKind.Question, string? details = null) =>
        Show(title, message, kind, new[] { yes, no }, details) == 0;

    public static string? PickFolder(string title, string? initial = null)
    {
        var d = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) d.InitialDirectory = initial;
        return d.ShowDialog(Owner) == true ? d.FolderName : null;
    }

    public static string? PickOpenFile(string title, string filter, string? initialDir = null)
    {
        var d = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir)) d.InitialDirectory = initialDir;
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static string? PickSaveFile(string title, string filter, string fileName, string? initialDir = null)
    {
        var d = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, OverwritePrompt = true, AddExtension = true };
        if (!string.IsNullOrEmpty(initialDir))
        {
            Directory.CreateDirectory(initialDir);
            d.InitialDirectory = initialDir;
        }
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    /// <summary>Open a file or folder with the shell (Explorer, the default editor...).</summary>
    public static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { Error("Could not open", $"Windows could not open {path}.", e.Message); }
    }

    /// <summary>Open Explorer with the file selected.</summary>
    public static void ShowInExplorer(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
        catch (Exception e) { Error("Could not open the folder", e.Message); }
    }
}
