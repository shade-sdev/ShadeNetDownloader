using ShadeNetDownloader.ViewModels;
using Wpf.Ui.Controls;

namespace ShadeNetDownloader.Views;

public partial class LinkGrabberDialog : ContentDialog
{
    public LinkGrabberDialog(LinkGrabberViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
