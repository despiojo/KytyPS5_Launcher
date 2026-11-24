using KytyPS5Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace KytyPS5Launcher;

public sealed partial class MainPage : Page
{
    private readonly UpdateService _service = new();
    private int _nextPage = 1;
    private bool _hasMore;
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
        LocationText.Text = $"Installation folder: {_service.Root}";
        ChangeFolderButton.IsEnabled = !_busy && _startupError is null;
        CheckButton.IsEnabled = !_busy && _startupError is null;
        ReleaseOption? selected = (ReleaseSelector.SelectedItem as ComboBoxItem)?.Tag as ReleaseOption;
        ReleaseSelector.IsEnabled = !_busy && ReleaseSelector.Items.Count > 0;
        MoreButton.IsEnabled = !_busy && _hasMore;
        InstallButton.IsEnabled = !_busy && _startupError is null && selected?.Release is not null && selected.Tag != _service.InstalledVersion;
        InstallButton.Content = _service.InstalledVersion is null ? "Install" : "Apply release";
        LaunchButton.IsEnabled = !_busy && _startupError is null && _service.InstalledVersion is not null;
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        _busy = true;
        Notice.IsOpen = false;
        Refresh();
        try
        {
            var picker = new FolderPicker(((FrameworkElement)sender).XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                Title = "Choose an empty installation folder",
                CommitButtonText = "Select folder"
            };
            PickFolderResult? selected = await picker.PickSingleFolderAsync();
            if (selected is null) return;
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;
            ProgressText.Text = "Copying installation to the selected folder...";
            await _service.ChangeLocationAsync(selected.Path);
            ProgressText.Text = _service.InstalledVersion is null
                ? "Installation folder selected."
                : "Installation copied. The original remains in its previous folder.";
        }
        catch (Exception ex) { ProgressText.Text = "Could not change the installation folder."; ShowError(ex); }
        finally { _busy = false; Progress.Visibility = Visibility.Collapsed; Refresh(); }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        _busy = true;
        _nextPage = 1;
        _hasMore = false;
        ReleaseSelector.Items.Clear();
        LatestText.Text = "Not checked";
        ReleaseAvailabilityText.Text = "";
        Notice.IsOpen = false;
        ProgressText.Text = "Checking releases...";
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        Refresh();
        try
        {
            await LoadReleasesAsync();
            ProgressText.Text = ReleaseSelector.Items.Count == 0
                ? "No releases were found."
                : "Select a release to install.";
        }
        catch (Exception ex) { ProgressText.Text = "Update check failed."; ShowError(ex); }
        finally { _busy = false; Progress.Visibility = Visibility.Collapsed; Refresh(); }
    }

    private async void More_Click(object sender, RoutedEventArgs e)
    {
        _busy = true;
        Notice.IsOpen = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        ProgressText.Text = "Loading older releases...";
        Refresh();
        try
        {
            await LoadReleasesAsync();
            ProgressText.Text = _hasMore ? "Select a release, or load more." : "All releases loaded.";
        }
        catch (Exception ex) { ProgressText.Text = "Could not load older releases."; ShowError(ex); }
        finally { _busy = false; Progress.Visibility = Visibility.Collapsed; Refresh(); }
    }

    private async Task LoadReleasesAsync()
    {
        IReadOnlyList<ReleaseOption> releases = await _service.GetReleasesAsync(_nextPage);
        if (_nextPage == 1) LatestText.Text = releases.FirstOrDefault()?.Tag ?? "No releases";
        var existing = ReleaseSelector.Items.OfType<ComboBoxItem>()
            .Select(item => ((ReleaseOption)item.Tag).Tag).ToHashSet(StringComparer.Ordinal);
        ComboBoxItem? firstAvailable = null;
        foreach (ReleaseOption release in releases)
        {
            if (!existing.Add(release.Tag)) continue;
            var item = new ComboBoxItem
            {
                Content = release.Release is null ? $"{release.Tag} (unavailable)" : release.Tag,
                Tag = release
            };
            ReleaseSelector.Items.Add(item);
            if (firstAvailable is null && release.Release is not null) firstAvailable = item;
        }
        _hasMore = releases.Count == 100;
        _nextPage++;
        if (ReleaseSelector.SelectedItem is null && firstAvailable is not null)
            ReleaseSelector.SelectedItem = firstAvailable;
    }

    private void Release_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ReleaseOption? selected = (ReleaseSelector.SelectedItem as ComboBoxItem)?.Tag as ReleaseOption;
        ReleaseAvailabilityText.Text = selected?.UnavailableReason ??
            (selected is null ? "" : selected.Tag == _service.InstalledVersion ? "This release is already installed." : "Ready to apply this release.");
        Refresh();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        ReleaseInfo? release = ((ReleaseSelector.SelectedItem as ComboBoxItem)?.Tag as ReleaseOption)?.Release;
        if (release is null) return;
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
            await _service.InstallAsync(release, progress);
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
