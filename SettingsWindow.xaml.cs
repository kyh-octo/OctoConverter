using System.Windows;
using OctoConverter.Services;

namespace OctoConverter;

/// <summary>설정 창. 현재는 자동 업데이트 항목만 있다.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private UpdateInfo? _update;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ChkAutoUpdate.IsChecked = settings.CheckForUpdates;
        TxtUpdateStatus.Text = UpdateText.F("UpdCurrent", UpdateService.CurrentVersion);
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdate.IsEnabled = false;
        BtnInstallUpdate.IsEnabled = false;
        TxtUpdateStatus.Text = UpdateText.T("UpdChecking");
        try
        {
            _update = await UpdateService.CheckAsync();
            if (!_update.IsNewer)
                TxtUpdateStatus.Text = UpdateText.F("UpdLatest", UpdateService.CurrentVersion);
            else if (string.IsNullOrEmpty(_update.InstallerUrl))
                TxtUpdateStatus.Text = UpdateText.F("UpdNoInstaller", _update.Version);
            else
            {
                TxtUpdateStatus.Text = UpdateText.F("UpdAvailable", _update.Version, UpdateService.CurrentVersion);
                BtnInstallUpdate.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            _update = null;
            TxtUpdateStatus.Text = UpdateText.F("UpdCheckFailed", ex.Message);
        }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
        }
    }

    private void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        // 설치 후 앱이 다시 시작되므로 자동 확인 설정을 미리 저장해 둔다
        _settings.CheckForUpdates = ChkAutoUpdate.IsChecked == true;
        _settings.Save();
        UpdateDownloadWindow.Run(this, _update);
    }

    private void ReleaseNotes_Click(object sender, RoutedEventArgs e) =>
        UpdateService.OpenReleasePage(_update?.ReleaseUrl);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _settings.CheckForUpdates = ChkAutoUpdate.IsChecked == true;
        _settings.Save();
        DialogResult = true;
    }
}
