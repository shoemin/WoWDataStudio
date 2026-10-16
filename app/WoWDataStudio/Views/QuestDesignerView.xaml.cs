using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>The Quest chains tab (QuestDesignerViewModel): find, the chain's flowchart, the quest editor.</summary>
public partial class QuestDesignerView : UserControl
{
    public QuestDesignerView()
    {
        InitializeComponent();
        ChainView.QuestClicked += id => (DataContext as QuestDesignerViewModel)?.OpenQuest(id);
    }

    private async void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is QuestDesignerViewModel vm && vm.SelectedResult is { } r) await vm.OpenAsync(r.Id);
    }

    /// <summary>A field's text is committed when the box loses the focus (Ctrl+Enter commits at once).</summary>
    private void Field_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QuestFieldView f }) f.CommitIfChanged();
    }

    /// <summary>For the screenshots: fit the flowchart into its view.</summary>
    public void FitChain() => ChainView.Fit();
}
