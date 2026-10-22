using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace ShadeNet.Setup;

public partial class SetupWindow : Window
{
    readonly InstallOptions _options;
    CancellationTokenSource? _cts;
    bool _installed;

    public SetupWindow(InstallOptions options)
    {
        _options = options;
        InitializeComponent();

        DirBox.Text = options.InstallDir;
        DesktopBox.IsChecked = options.DesktopShortcut;
        StartupBox.IsChecked = options.StartWithWindows;
        BrowserBox.IsChecked = options.BrowserIntegration;
        VersionText.Text = "Version " + App.VersionText;

        // an existing installation: this is an update, and the user's data is not touched
        var existing = new InstallEngine(options).InstalledVersion;
        if (existing != null)
        {
            TitleText.Text = "Update ShadeNet Downloader";
            InstallButton.Content = "Update";
            IntroText.Text = $"Version {existing} is installed. Your downloads, settings and chosen locations are kept.";
        }
    }

    void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Install ShadeNet Downloader in", InitialDirectory = Directory.Exists(DirBox.Text) ? DirBox.Text : null };
        if (dialog.ShowDialog(this) != true) return;

        // a chosen folder gets its own subfolder when it is not already ours or empty: never mix into someone's files
        var chosen = dialog.FolderName;
        var empty = !Directory.EnumerateFileSystemEntries(chosen).Any();
        var ours = File.Exists(Path.Combine(chosen, InstallEngine.ManifestName));
        DirBox.Text = empty || ours ? chosen : Path.Combine(chosen, InstallOptions.ProductName);
    }

    async void OnInstall(object sender, RoutedEventArgs e)
    {
        var dir = DirBox.Text.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(dir))
        {
            MessageBox.Show(this, "Choose a full folder path, such as C:\\Users\\you\\AppData\\Local\\Programs\\ShadeNet Downloader.", "Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _options.InstallDir = dir;
        _options.DesktopShortcut = DesktopBox.IsChecked == true;
        _options.StartWithWindows = StartupBox.IsChecked == true;
        _options.BrowserIntegration = BrowserBox.IsChecked == true;

        ChoicePanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Text = "Installing…";
        Bar.Value = 0;
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<double>(p => Bar.Value = p);
            await Task.Run(() =>
            {
                using var payload = App.OpenPayload(_options);
                new InstallEngine(_options, App.Log).Install(payload, App.VersionText, p => ((IProgress<double>)progress).Report(p), _cts.Token);
            });
            _installed = true;
            Show("ShadeNet Downloader is installed. You can start it from the Start menu" + (_options.DesktopShortcut ? " or the desktop." : "."), failed: false);
        }
        catch (OperationCanceledException)
        {
            Show("Setup was cancelled.", failed: true);
        }
        catch (SetupException ex)
        {
            Show(ex.Message, failed: true);
        }
        catch (Exception ex)
        {
            App.Log("failed: " + ex);
            Show("Setup could not finish: " + ex.Message, failed: true);
        }
    }

    void Show(string message, bool failed)
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
        DoneText.Text = message;
        LaunchBox.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = failed ? "Setup stopped" : "All done";
    }

    void OnRetry(object sender, RoutedEventArgs e)
    {
        DonePanel.Visibility = Visibility.Collapsed;
        ChoicePanel.Visibility = Visibility.Visible;
        TitleText.Text = "Install ShadeNet Downloader";
    }

    void OnCancel(object sender, RoutedEventArgs e) => Close();

    void OnClose(object sender, RoutedEventArgs e)
    {
        if (_installed && LaunchBox.IsChecked == true && File.Exists(_options.AppExe))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_options.AppExe) { UseShellExecute = false, WorkingDirectory = _options.InstallDir });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                App.Log("could not start the program: " + ex.Message);
            }
        }

        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
