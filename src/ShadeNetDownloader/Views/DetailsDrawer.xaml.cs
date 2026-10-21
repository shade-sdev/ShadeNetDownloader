using System.Windows;
using System.Windows.Controls;

namespace ShadeNetDownloader.Views;

public partial class DetailsDrawer : UserControl
{
    public DetailsDrawer() => InitializeComponent();

    /// <summary>The user asked to close the drawer (the X button).</summary>
    public event EventHandler? CloseRequested;

    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
