using System.Windows.Controls;
using System.Windows.Input;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>0.12.0: the Classes and talents tab (see ClassTalentViewModel).</summary>
public partial class ClassTalentView : UserControl
{
    public ClassTalentView() => InitializeComponent();

    /// <summary>For the screenshots: show an inner tab (0 trees, 1 spells by level, 2 class masks, 3 new class).</summary>
    public void ShowInnerTab(int index) => InnerTabs.SelectedIndex = index;

    private void Spells_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ClassTalentViewModel vm) vm.OpenClassSpellCommand.Execute(null);
    }
}
