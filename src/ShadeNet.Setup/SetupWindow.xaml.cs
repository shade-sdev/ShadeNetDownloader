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

        if (options.Mode == SetupMode.Update)
        {
            // an update has no questions: it works, then starts the program again
            ChoicePanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            TitleText.Text = "Updating ShadeNet Downloader";
            StatusText.Text = "Waiting for ShadeNet to close…";
            VersionText.Text = "Please wait a moment";
            Loaded += (_, _) => _ = RunUpdateAsync();
            return;
        }

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

    async Task RunUpdateAsync()
    {
        _cts = new CancellationTokenSource();
        try
        {
            await Task.Run(() => App.WaitForProcess(_options.WaitForProcessId));
            StatusText.Text = "Installing the new version…";
            var progress = new Progress<double>(p => Bar.Value = p);
            var version = await Task.Run(() =>
            {
                if (string.IsNullOrEmpty(_options.PackagePath) || !File.Exists(_options.PackagePath)) throw new SetupException("The update package is missing.", 4);
                using var package = File.OpenRead(_options.PackagePath);
                return new InstallEngine(_options, App.Log).Update(package, null, p => ((IProgress<double>)progress).Report(p), _cts.Token);
            });

            _installed = true;
            Bar.Value = 1;
            VersionText.Text = "Version " + version;
            StatusText.Text = "Done. Starting ShadeNet Downloader…";
            if (_options.LaunchAfter) App.StartApp(_options);
            await Task.Delay(900);
            Close();
        }
        catch (Exception ex) when (ex is SetupException or OperationCanceledException or IOException)
        {
            App.Log("update failed: " + ex.Message);
            ShowUpdateFailed(ex is SetupException ? ex.Message : "The update could not be applied: " + ex.Message);
        }
        catch (Exception ex)
        {
            App.Log("update failed: " + ex);
            ShowUpdateFailed("The update could not be applied: " + ex.Message);
        }
    }

    /// <summary>The program was put back as it was; the way on is to start it again.</summary>
    void ShowUpdateFailed(string message)
    {
        Show(message + " ShadeNet Downloader is unchanged.", failed: true);
        RetryButton.Visibility = Visibility.Collapsed;
        TitleText.Text = "The update did not work";
        LaunchBox.Content = "Open ShadeNet Downloader again";
        LaunchBox.Visibility = Visibility.Visible;
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
            ShowExtensionGuide();
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
        ExtensionPanel.Visibility = Visibility.Collapsed;
        DoneText.Text = message;
        LaunchBox.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = failed ? "Setup stopped" : "All done";
    }

    /// <summary>The browser keeps the last step for the user: show it right here, with the folder already on the clipboard.</summary>
    void ShowExtensionGuide()
    {
        var folder = ExtensionGuide.FolderFor(_options);
        if (!_options.BrowserIntegration || !Directory.Exists(folder)) return;

        ExtensionFolderBox.Text = folder;
        ExtensionNote.Text = CopyToClipboard(folder) ? "The folder is copied: press Ctrl+V in the box that opens." : "";

        BrowserButtons.Children.Clear();
        var browsers = ExtensionGuide.Installed();
        foreach (var browser in browsers)
        {
            var button = new Button { Content = "Open " + browser.Name, Tag = browser, Margin = new Thickness(0, 6, 8, 0), Style = browsers[0] == browser ? (Style)FindResource("Primary") : null };
            button.Click += OnOpenBrowser;
            BrowserButtons.Children.Add(button);
        }

        if (browsers.Count == 0)
            ExtensionNote.Text = "No Chrome, Edge or Brave was found. Open your browser, type chrome://extensions (or edge://extensions) in the address bar and follow steps 2 and 3. " + ExtensionNote.Text;

        ExtensionPanel.Visibility = Visibility.Visible;
    }

    void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        var browser = (ExtensionBrowser)((Button)sender).Tag;
        if (!ExtensionGuide.TryOpen(browser))
            ExtensionNote.Text = $"{browser.Name} could not be started. Open it yourself and type {browser.ExtensionsAddress} in the address bar.";
    }

    void OnCopyExtensionFolder(object sender, RoutedEventArgs e) =>
        ExtensionNote.Text = CopyToClipboard(ExtensionFolderBox.Text) ? "Copied. Press Ctrl+V in the box that opens." : "Could not copy. Select the folder above and press Ctrl+C.";

    void OnShowExtensionFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ExtensionFolderBox.Text) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.Log("could not open the extension folder: " + ex.Message);
        }
    }

    /// <summary>Another program may hold the clipboard for a moment; the folder is also shown in the box, so a failed copy is not an error.</summary>
    static bool CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
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
        if ((_installed || _options.Mode == SetupMode.Update) && LaunchBox.IsChecked == true && File.Exists(_options.AppExe))
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
