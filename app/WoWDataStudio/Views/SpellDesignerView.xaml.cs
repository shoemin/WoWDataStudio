using System.Windows.Controls;
using System.Windows.Input;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>0.12.0: the Spell designer tab (see SpellDesignerViewModel).</summary>
public partial class SpellDesignerView : UserControl
{
    public SpellDesignerView() => InitializeComponent();

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SpellDesignerViewModel vm) vm.OpenSelectedCommand.Execute(null);
    }
}
