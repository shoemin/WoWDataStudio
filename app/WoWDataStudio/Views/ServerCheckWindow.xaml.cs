using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WoWData.Core.ServerData;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>"Check client and server...": the differences between the game's tables and the server's, and "make them
/// match" in either direction (WoWData.Core.ServerData.ServerConsistency; "wds server check / match").</summary>
public partial class ServerCheckWindow : Window
{
    private const string All = "everything";
    private readonly ServerDataViewModel _vm;
    private readonly ServerEditor _ed;

    public ServerCheckWindow(ServerDataViewModel vm, ServerEditor ed, string? area)
    {
        InitializeComponent();
        _vm = vm;
        _ed = ed;
        AreaBox.ItemsSource = new[] { All }.Concat(ServerConsistency.Areas).ToList();
        AreaBox.SelectedItem = area ?? "spells";
        Update();
    }

    public List<ConsistencyIssue> Issues { get; private set; } = new();

    /// <summary>Run the check (also for tests and screenshots).</summary>
    public async Task RunAsync(string? area = null, string? ids = null)
    {
        if (area != null) AreaBox.SelectedItem = area;
        if (ids != null) IdsBox.Text = ids;
        var a = AreaBox.SelectedItem as string;
        HashSet<long>? set = null;
        if (IdsBox.Text.Trim().Length > 0)
        {
            set = new HashSet<long>();
            foreach (var x in IdsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (long.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) set.Add(v);
        }
        RunButton.IsEnabled = false;
        SummaryText.Text = "Checking...";
        try
        {
            Issues = await Task.Run(() => ServerConsistency.Check(_ed, a == All ? null : a, set, 20000));
            IssueList.ItemsSource = Issues;
            SummaryText.Text = Issues.Count == 0 ? "The game and the server agree." : $"{Issues.Count:N0} difference(s)" + (Issues.Count >= 20000 ? " (the first 20,000)" : "") + ". Select one (or several) and make them match.";
            if (Issues.Count > 0) IssueList.SelectedIndex = 0;
        }
        catch (ServerDataException e)
        {
            SummaryText.Text = e.Message;
        }
        finally
        {
            RunButton.IsEnabled = true;
            Update();
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private List<ConsistencyIssue> Selected => IssueList.SelectedItems.OfType<ConsistencyIssue>().ToList();

    private void IssueList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Update();

    private void Update()
    {
        var sel = Selected;
        var first = sel.FirstOrDefault();
        FieldList.ItemsSource = first?.Fields.OrderBy(f => f.Same).ToList();
        AdviceText.Text = first == null ? "" : first.Text;
        ServerButton.IsEnabled = sel.Any(i => i.CanMatchServer);
        ClientButton.IsEnabled = sel.Any(i => i.CanMatchClient);
    }

    /// <summary>"Make the server match the game" for the selected differences (edits in the Server data tab).</summary>
    public int MatchServer(IEnumerable<ConsistencyIssue> issues)
    {
        int n = 0;
        var errors = new List<string>();
        foreach (var i in issues.Where(i => i.CanMatchServer))
        {
            var r = ServerConsistency.MatchServer(_ed, i);
            if (r.Ok) n++;
            else errors.Add($"{i.ClientTable} {i.Id}: {r.Message}");
        }
        StatusText.Text = $"{n} server row(s) now take the game's values (unsaved: \"Save to my workspace\" in the Server data tab keeps them)." + (errors.Count > 0 ? " " + string.Join(" ", errors.Take(3)) : "");
        _vm.AfterMatch(StatusText.Text);
        return n;
    }

    /// <summary>"Make the game match the server" for the selected differences (the game's tables, saved into the workspace).</summary>
    public int MatchClient(IEnumerable<ConsistencyIssue> issues)
    {
        int n = 0;
        var msgs = new List<string>();
        foreach (var i in issues.Where(i => i.CanMatchClient))
        {
            try
            {
                msgs.Add(ServerConsistency.MatchClient(_ed, i));
                n++;
            }
            catch (ServerDataException e) { msgs.Add(e.Message); }
        }
        StatusText.Text = $"{n} row(s) of the game's tables now take the server's values (in your workspace: test them in the game or build the release patch). " + string.Join(" ", msgs.Take(2));
        _vm.AfterMatch(StatusText.Text);
        return n;
    }

    private async void MatchServer_Click(object sender, RoutedEventArgs e)
    {
        MatchServer(Selected);
        await RunAsync();
    }

    private async void MatchClient_Click(object sender, RoutedEventArgs e)
    {
        MatchClient(Selected);
        await RunAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
