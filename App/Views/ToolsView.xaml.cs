using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiagKit.ViewModels;

namespace DiagKit.Views;

public partial class ToolsView : UserControl
{
    public ToolsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) =>
        {
            if (DataContext is ToolsViewModel vm)
            {
                vm.FocusSearchRequested -= FocusSearch;
            }
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ToolsViewModel old)
        {
            old.FocusSearchRequested -= FocusSearch;
        }

        if (e.NewValue is ToolsViewModel vm)
        {
            vm.FocusSearchRequested += FocusSearch;
            if (vm.TakeFocusRequest())
            {
                FocusSearch();
            }
        }
    }

    private void FocusSearch()
    {
        // The page may have just been created by navigation; wait until it is in the visual tree.
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }
}
