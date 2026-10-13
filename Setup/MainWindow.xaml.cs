using System.Windows;
using DiagKit.Controls;

namespace DiagKit.Setup;

public partial class MainWindow : Window
{
    public MainWindow(SetupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        DarkTitleBar.Apply(this);
        Closing += (_, e) =>
        {
            // Closing mid-download cancels cleanly instead of leaving a half-written tool folder.
            if (vm.IsWorking)
            {
                vm.CancelCommand.Execute(null);
                e.Cancel = true;
            }
        };
    }
}
