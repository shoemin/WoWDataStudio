using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WoWDataStudio.Helpers;

/// <summary>
/// Greys out a control that writes (an import, an install, a save...) while another version is open read only, and
/// shows why on hover: <c>h:WriteGuard.Reason="{Binding WriteRefusal}"</c>. With a reason the control is disabled and
/// its tooltip is the reason (shown on the disabled control too); when the reason goes away (your 1.12 game is open
/// again: the same view, another view model) the control's own IsEnabled and ToolTip (values or bindings) come back.
/// A null reason from the start changes nothing at all, so your 1.12 game's controls stay exactly as they are.
/// </summary>
public static class WriteGuard
{
    public static readonly DependencyProperty ReasonProperty = DependencyProperty.RegisterAttached(
        "Reason", typeof(string), typeof(WriteGuard), new PropertyMetadata(null, OnReasonChanged));

    private static readonly DependencyProperty SavedProperty = DependencyProperty.RegisterAttached(
        "Saved", typeof(Saved), typeof(WriteGuard), new PropertyMetadata(null));

    public static string? GetReason(DependencyObject d) => (string?)d.GetValue(ReasonProperty);
    public static void SetReason(DependencyObject d, string? value) => d.SetValue(ReasonProperty, value);

    /// <summary>What the control had before it was greyed out.</summary>
    private sealed class Saved
    {
        public BindingBase? EnabledBinding;
        public object? Enabled;
        public BindingBase? TipBinding;
        public object? Tip;
        public object? ShowOnDisabled;
    }

    private static void OnReasonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        var reason = e.NewValue as string;
        var saved = (Saved?)el.GetValue(SavedProperty);
        if (!string.IsNullOrEmpty(reason))
        {
            if (saved == null)
            {
                saved = new Saved
                {
                    EnabledBinding = BindingOperations.GetBindingBase(el, UIElement.IsEnabledProperty),
                    Enabled = el.ReadLocalValue(UIElement.IsEnabledProperty),
                    TipBinding = BindingOperations.GetBindingBase(el, FrameworkElement.ToolTipProperty),
                    Tip = el.ReadLocalValue(FrameworkElement.ToolTipProperty),
                    ShowOnDisabled = el.ReadLocalValue(ToolTipService.ShowOnDisabledProperty),
                };
                el.SetValue(SavedProperty, saved);
            }
            el.ToolTip = reason;
            ToolTipService.SetShowOnDisabled(el, true);
            el.IsEnabled = false;
            return;
        }
        if (saved == null) return;
        Restore(el, UIElement.IsEnabledProperty, saved.EnabledBinding, saved.Enabled);
        Restore(el, FrameworkElement.ToolTipProperty, saved.TipBinding, saved.Tip);
        Restore(el, ToolTipService.ShowOnDisabledProperty, null, saved.ShowOnDisabled);
        el.ClearValue(SavedProperty);
    }

    private static void Restore(FrameworkElement el, DependencyProperty p, BindingBase? binding, object? value)
    {
        if (binding != null) el.SetBinding(p, binding);
        else if (value == null || value == DependencyProperty.UnsetValue) el.ClearValue(p);
        else el.SetValue(p, value);
    }
}
