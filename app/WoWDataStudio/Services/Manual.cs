using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace WoWDataStudio.Services;

/// <summary>
/// The user manual (docs\manual\*.md, rendered to HTML by tools\ManualBuilder at build time) and F1 context help.
/// <para>Every screen names its chapter with the inherited attached property <c>s:Manual.Topic="model-viewer"</c>
/// (the chapter's file name without extension). F1 (WPF's ApplicationCommands.Help) opens the chapter of the focused
/// element, or of the nearest parent that names one; the Help menu opens the contents.</para>
/// <para>Where the HTML is looked for, walking up from the app folder: <c>manual\</c> (the installed layout:
/// <c>&lt;install&gt;\bin\WoWDataStudio.exe</c> + <c>&lt;install&gt;\manual\</c>), then <c>build\manual\</c> (a developer build
/// of WoWDataTool); without HTML, the Markdown source <c>docs\manual\</c> is opened.</para>
/// </summary>
public static class Manual
{
    public const string Contents = "index";

    public static readonly DependencyProperty TopicProperty = DependencyProperty.RegisterAttached(
        "Topic", typeof(string), typeof(Manual), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static string? GetTopic(DependencyObject d) => (string?)d.GetValue(TopicProperty);

    public static void SetTopic(DependencyObject d, string? value) => d.SetValue(TopicProperty, value);

    /// <summary>Opens a chapter (CommandParameter = the chapter's file name; empty = the contents).</summary>
    public static IRelayCommand<string?> OpenCommand { get; } = new RelayCommand<string?>(t => Open(t));

    /// <summary>F1 everywhere: called once at start-up.</summary>
    public static void RegisterF1()
    {
        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(ApplicationCommands.Help, (sender, e) =>
        {
            var topic = (e.OriginalSource as DependencyObject is { } d ? GetTopic(d) : null) ?? (sender as DependencyObject is { } w ? GetTopic(w) : null);
            Open(topic);
            e.Handled = true;
        }));
    }

    /// <summary>The file to open for a chapter: rendered HTML if present, else the Markdown source, else null.</summary>
    public static string? Locate(string? topic, string? startDir = null) => WoWData.Core.ManualLocator.Locate(topic, startDir);

    public static void Open(string? topic)
    {
        var file = Locate(topic);
        if (file == null)
        {
            Dialogs.Error("The user manual was not found",
                "The manual is installed next to the app (the \"manual\" folder of the installation). Repair it by running the installer again.");
            return;
        }
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception e) { Dialogs.Error("Could not open the user manual", $"Windows could not open {file}.", e.Message); }
    }
}
