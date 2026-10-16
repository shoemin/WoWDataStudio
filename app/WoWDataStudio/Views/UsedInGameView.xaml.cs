using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WoWData.Core.Usage;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>Renders a <see cref="UsageReport"/>: a headline, then one block per section (title, summary, lines with
/// links, "and N more", tail lines), and the note about the server database when sections need it. Built in code
/// because each line mixes plain text and links (WPF cannot bind Inlines).</summary>
public partial class UsedInGameView : UserControl
{
    private UsedInGameViewModel? _vm;

    public UsedInGameView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as UsedInGameViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
            Render();
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UsedInGameViewModel.Report)) Render();
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    /// <summary>The section blocks currently shown (screenshots scroll to one of them).</summary>
    public FrameworkElement? SectionElement(string id) =>
        Sections.Children.OfType<FrameworkElement>().FirstOrDefault(f => f.Tag as string == id);

    private void Render()
    {
        Headline.Inlines.Clear();
        Sections.Children.Clear();
        ServerNote.Visibility = Visibility.Collapsed;
        var rep = _vm?.Report;
        if (rep == null) return;
        AddSpans(Headline.Inlines, rep.Headline);
        foreach (var s in rep.Sections) Sections.Children.Add(SectionBlock(s));
        if (rep.Notes.Count > 0)
        {
            ServerNoteText.Text = string.Join("\n", rep.Notes);
            ServerNote.Visibility = Visibility.Visible;
        }
    }

    private FrameworkElement SectionBlock(UsageSection s)
    {
        var panel = new StackPanel();
        var title = new TextBlock { Text = s.Title.ToUpperInvariant(), FontWeight = FontWeights.SemiBold, FontSize = 11, Foreground = Res("TextMuted"), Margin = new Thickness(0, 0, 0, 1) };
        panel.Children.Add(title);
        if (s.Summary.Length > 0)
            panel.Children.Add(new TextBlock { Text = s.Summary, TextWrapping = TextWrapping.Wrap, FontWeight = s.Id == "affects" ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, 2) });
        foreach (var l in s.Lines) panel.Children.Add(LineBlock(l));
        if (s.More > 0)
            panel.Children.Add(new TextBlock { Text = $"and {s.More:N0} more", Foreground = Res("TextMuted"), FontStyle = FontStyles.Italic, Margin = new Thickness(12, 1, 0, 0) });
        foreach (var l in s.Tail) panel.Children.Add(LineBlock(l));
        return new Border
        {
            Tag = s.Id,
            Child = panel,
            BorderBrush = Res("Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 6, 0, 6),
            Background = s.Id == "affects" ? Res("AccentSoft") : Brushes.Transparent,
        };
    }

    private FrameworkElement LineBlock(UsageLine l)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12 + 16 * l.Indent, 1, 0, 1) };
        AddSpans(tb.Inlines, l);
        return tb;
    }

    private void AddSpans(InlineCollection inlines, UsageLine l)
    {
        foreach (var s in l.Spans)
        {
            var run = new Run(s.Text);
            if (s.Bold) run.FontWeight = FontWeights.SemiBold;
            if (s.Color != null && ColorConverter.ConvertFromString(s.Color) is Color c) run.Foreground = new SolidColorBrush(c);
            else if (s.Muted) run.Foreground = Res("TextMuted");
            if (s.Key != null && _vm != null && _vm.CanNavigate(s.Key))
            {
                var link = new Hyperlink(run)
                {
                    Command = _vm.NavigateCommand,
                    CommandParameter = s.Key,
                    ToolTip = "Show " + s.Text.Trim() + " (" + s.Key + ")",
                };
                // blue links are underlined on hover; links in an item's quality colour keep their underline (the colour
                // alone does not say "link")
                var rest = s.Color == null ? null : TextDecorations.Underline;
                link.TextDecorations = rest;
                if (s.Color == null) link.Foreground = Res("Accent");
                link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
                link.MouseLeave += (_, _) => link.TextDecorations = rest;
                inlines.Add(link);
            }
            else inlines.Add(run);
        }
    }
}
