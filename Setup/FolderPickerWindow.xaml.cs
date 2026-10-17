using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DiagKit.Controls;

namespace DiagKit.Setup;

public partial class FolderPickerWindow : Window
{
    private readonly FolderPickerViewModel _vm;

    public FolderPickerWindow(string start)
    {
        InitializeComponent();
        _vm = new FolderPickerViewModel(start);
        DataContext = _vm;
        DarkTitleBar.Apply(this);
        _vm.PropertyChanged += OnViewModelChanged;
        Loaded += (_, _) =>
        {
            _vm.Start();
            FolderList.Focus();
        };
    }

    /// <summary>The folder the user picked; set when the dialog returns true.</summary>
    public string? Result { get; private set; }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderPickerViewModel.IsCreating) && _vm.IsCreating)
        {
            // Ready to type, with the suggested name selected so typing replaces it.
            Dispatcher.BeginInvoke(() =>
            {
                NewFolderBox.Focus();
                NewFolderBox.SelectAll();
            });
        }
    }

    private void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FolderItem f)
        {
            _vm.OpenCommand.Execute(f);
        }
    }

    private void FolderList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back)
        {
            _vm.UpCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _vm.SelectedFolder != null && Keyboard.Modifiers == ModifierKeys.None)
        {
            // Enter opens the highlighted folder, like Explorer; "Use this folder" takes the current selection.
            _vm.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        Result = _vm.Chosen;
        DialogResult = true;
    }
}
