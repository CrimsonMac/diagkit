using System;
using System.Windows;
using DiagKit.Controls;

namespace DiagKit.Uninstall;

public partial class MainWindow : Window
{
    public MainWindow(UninstallViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        DarkTitleBar.Apply(this);
        Closing += (_, e) => e.Cancel = vm.IsWorking;
        Closed += (_, _) => vm.OnClosed();
    }
}
