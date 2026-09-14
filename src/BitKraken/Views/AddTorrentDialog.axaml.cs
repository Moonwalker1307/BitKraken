using Avalonia.Controls;
using Avalonia.Input;
using BitKraken.ViewModels;

namespace BitKraken.Views;

public partial class AddTorrentDialog : Window
{
    public AddTorrentDialog()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is AddTorrentViewModel vm)
                vm.Completed += result => Close(result);
        };

        Opened += (_, _) => SourceBox.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(null);
        };
    }
}
