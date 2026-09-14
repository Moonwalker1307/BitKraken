using Avalonia.Controls;
using Avalonia.Input;
using BitKraken.Services;

namespace BitKraken.Views;

public partial class RemoveDialog : Window
{
    public RemoveDialog() : this([]) { }

    public RemoveDialog(IReadOnlyList<string> names)
    {
        InitializeComponent();

        TitleText.Text = names.Count > 1 ? $"Remove {names.Count} torrents?" : "Remove torrent?";
        NameText.Text = names.Count == 1 ? names[0] : string.Join(", ", names);

        CancelButton.Click += (_, _) => Close(RemoveChoice.Cancel);
        RemoveButton.Click += (_, _) => Close(DeleteFilesBox.IsChecked == true ? RemoveChoice.RemoveDeleteData : RemoveChoice.RemoveKeepData);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(RemoveChoice.Cancel);
        };
    }
}
