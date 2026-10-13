using System.Windows;
using System.Windows.Controls;
using DiagKit.Models;

namespace DiagKit.Models
{
    /// <summary>Visual state of a status LED. Shared by tools, health-check steps, findings and setup progress.</summary>
    public enum LedState
    {
        Off,
        Ok,
        Info,
        Warning,
        Critical,
        Busy,
    }
}

namespace DiagKit.Controls
{
    /// <summary>Status LED. The look lives in Theme.xaml; Busy pulses.</summary>
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
}
