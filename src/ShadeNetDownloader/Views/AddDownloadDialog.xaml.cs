using System.Windows;
using ShadeNetDownloader.ViewModels;
using Wpf.Ui.Controls;

namespace ShadeNetDownloader.Views;

public partial class AddDownloadDialog : ContentDialog
{
    public AddDownloadDialog(AddDownloadViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // The link box is where the user starts typing/pasting.
        Loaded += (_, _) => UrlBox.Focus();

        // "Grab the links on it": this dialog closes, the link grabber opens (see DialogService)
        viewModel.CloseRequested += () => Hide();
    }
}
