using Avalonia.Controls;
using Avalonia.Input;
using BitKraken.ViewModels;

namespace BitKraken.Views;

public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
                vm.Completed += saved => Close(saved);
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(false);
        };
    }
}
