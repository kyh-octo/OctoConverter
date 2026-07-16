using System.Windows;
using System.Windows.Controls;
using OctoConverter.Services;
using OctoConverter.Views;

namespace OctoConverter;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureTabContent();
        await Task.Run(FFmpegService.Locate);
        UpdateFFmpegStatus();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Tabs)) EnsureTabContent();
    }

    /// <summary>선택된 탭의 화면을 처음 선택될 때 한 번만 생성한다.</summary>
    private void EnsureTabContent()
    {
        if (Tabs?.SelectedItem is not TabItem tab || tab.Content is not null) return;
        tab.Content = (string?)tab.Tag switch
        {
            "image" => new ImageTab(),
            "animation" => new AnimationTab(),
            "icon" => new IconTab(),
            "document" => new DocumentTab(),
            "music" => new MusicTab(),
            "video" => new VideoTab(),
            _ => null
        };
    }

    private void UpdateFFmpegStatus()
    {
        if (FFmpegService.IsAvailable)
        {
            FFmpegStatus.Text = "FFmpeg 사용 가능 ✓";
            FFmpegBanner.Visibility = Visibility.Collapsed;
        }
        else
        {
            FFmpegStatus.Text = "FFmpeg 미설치";
            FFmpegBanner.Visibility = Visibility.Visible;
        }
    }

    private void InstallFFmpeg_Click(object sender, RoutedEventArgs e)
    {
        var win = new FFmpegDownloadWindow { Owner = this };
        win.ShowDialog();
        UpdateFFmpegStatus();
    }
}
