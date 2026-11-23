using KytyPS5Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KytyPS5Launcher;

public sealed partial class MainPage : Page
{
    private readonly UpdateService _service = new();
    private ReleaseInfo? _latest;
    private bool _busy;
    private string? _startupError;

    public MainPage()
    {
        InitializeComponent();
        try { _service.Initialize(); }
        catch (Exception ex) { _startupError = ex.Message; ShowError(ex); }
        Refresh();
    }

    private void Refresh()
    {
        InstalledText.Text = _service.InstalledVersion ?? "Not installed";
        LatestText.Text = _latest?.Tag ?? "Not checked";
        LocationText.Text = $"Installation folder: {_service.Root}";
        CheckButton.IsEnabled = !_busy && _startupError is null;
        InstallButton.IsEnabled = !_busy && _startupError is null && _latest is not null && _latest.Tag != _service.InstalledVersion;
        InstallButton.Content = _service.InstalledVersion is null ? "Install" : "Update";
        LaunchButton.IsEnabled = !_busy && _startupError is null && _service.InstalledVersion is not null;
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        _busy = true;
        _latest = null;
        Notice.IsOpen = false;
        ProgressText.Text = "Checking the latest release...";
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        Refresh();
        try
        {
            _latest = await _service.CheckAsync();
            ProgressText.Text = _latest.Tag == _service.InstalledVersion
                ? "The latest release is installed."
                : "A release is ready to install.";
        }
        catch (Exception ex) { ProgressText.Text = "Update check failed."; ShowError(ex); }
        finally { _busy = false; Progress.Visibility = Visibility.Collapsed; Refresh(); }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_latest is null) return;
        _busy = true;
        Notice.IsOpen = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        ProgressText.Text = "Preparing installation...";
        Refresh();
        var progress = new Progress<UpdateProgress>(value =>
        {
            ProgressText.Text = value.Message;
            Progress.IsIndeterminate = value.Percent is null;
            if (value.Percent is double percent) Progress.Value = percent;
        });
        try
        {
            await _service.InstallAsync(_latest, progress);
            ProgressText.Text = "Installation complete.";
            Notice.Severity = InfoBarSeverity.Success;
            Notice.Title = "Ready to launch";
            Notice.Message = "The release was installed successfully.";
            Notice.IsOpen = true;
        }
        catch (Exception ex)
        {
            ProgressText.Text = "Installation failed. The previous installation remains available.";
            ShowError(ex);
        }
        finally { _busy = false; Progress.Visibility = Visibility.Collapsed; Refresh(); }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        try { _service.Launch(); ProgressText.Text = "KytyPS5 launched."; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowError(Exception ex)
    {
        Notice.Severity = InfoBarSeverity.Error;
        Notice.Title = "Unable to continue";
        Notice.Message = ex.Message;
        Notice.IsOpen = true;
    }
}
