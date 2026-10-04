using System.ComponentModel;
using System.Windows;
using OctoConverter.Services;

namespace OctoConverter;

public partial class FFmpegDownloadWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private bool _finished;

    public FFmpegDownloadWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var progress = new Progress<(double Percent, string Message)>(p =>
        {
            Progress.Value = p.Percent;
            StatusText.Text = p.Message;
        });

        try
        {
            await FFmpegService.DownloadAsync(progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 사용자가 취소
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "설치에 실패했습니다: " + ex.Message +
                "\n\n인터넷 연결을 확인해 주세요. 또는 ffmpeg.exe / ffprobe.exe를 직접 받아 " +
                "프로그램 폴더에 넣어도 인식됩니다.",
                "FFmpeg 설치", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _finished = true;
            CancelBtn.IsEnabled = false;
            Close();
            _cts.Dispose();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelBtn.IsEnabled = false;
        StatusText.Text = "취소 처리 중... 현재 작업이 정리되면 창이 닫힙니다.";
        _cts.Cancel();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            Cancel_Click(this, new RoutedEventArgs());
        }
    }
}
