using System.Windows;
using System.Windows.Controls;
using DiagKit.Models;

namespace DiagKit.Controls;

/// <summary>Status LED. The look lives in Themes/Theme.xaml; Busy pulses.</summary>
public sealed class Led : Control
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(LedState), typeof(Led), new FrameworkPropertyMetadata(LedState.Off));

    public LedState State
    {
        get => (LedState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }
}
