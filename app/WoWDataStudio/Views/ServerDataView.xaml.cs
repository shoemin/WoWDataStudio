using System.Windows.Controls;
using System.Windows.Input;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>0.9.0: the Server data tab (the server's world database). The logic is in ServerDataViewModel.</summary>
public partial class ServerDataView : UserControl
{
    public ServerDataView()
    {
        InitializeComponent();
    }

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        CommitField(box);
        e.Handled = true;
    }

    private void Field_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) CommitField(box);
    }

    private static void CommitField(TextBox box)
    {
        if (box.DataContext is not ServerFieldView f) return;
        if (f.Text == box.Text) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        f.CommitCommand.Execute(null);
    }
}
