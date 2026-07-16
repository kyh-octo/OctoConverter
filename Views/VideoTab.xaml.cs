using System.IO;
using System.Windows;
using System.Windows.Controls;
using OctoConverter.Models;
using OctoConverter.Services;

namespace OctoConverter.Views;

public partial class VideoTab : UserControl
{
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _estimateCts;

    public VideoTab()
    {
        InitializeComponent();
        FileList.DialogFilter =
            "동영상 파일|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv;*.flv;*.m4v;*.ts;*.gif;" +
            "*.3gp;*.mpg;*.mpeg;*.vob;*.mts;*.m2ts;*.ogv;*.asf;*.f4v;*.divx;*.rm;*.rmvb|모든 파일|*.*";
        foreach (var ext in new[]
                 {
                     ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".flv", ".m4v", ".ts", ".gif",
                     ".3gp", ".mpg", ".mpeg", ".vob", ".mts", ".m2ts", ".ogv", ".asf", ".f4v",
                     ".divx", ".rm", ".rmvb"
                 })
            FileList.AcceptedExtensions.Add(ext);
        FileList.FilesAdded += OnFilesAdded;
        FileList.ListChanged += (_, _) => RequestEstimate();
    }

    private void Tab_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateVisibility();
        RequestEstimate();
    }

    private async void OnFilesAdded(IReadOnlyList<FileItem> items)
    {
        foreach (var item in items)
        {
            var info = await MediaProbe.ProbeAsync(item.FilePath);
            if (info is not null) item.Info = MediaProbe.Summary(info);
            else if (!FFmpegService.IsAvailable) item.Info = "FFmpeg 필요";
        }
        RequestEstimate();
    }

    // ===== 설정 =====

    private sealed record VideoSettings(
        string Format, string Ext, int TargetHeight, int CustomWidth, int CustomHeight, int Fps,
        int QualityMode /*0=CRF 1=Bitrate 2=Target*/, int Crf, int BitrateKbps, double TargetMB,
        int AudioKbps, bool RemoveAudio);

    private static bool IsAudioOnly(string format) => format is "mp3" or "m4a" or "wav";

    /// <summary>x264·VP9·MPEG-4는 2-pass를 지원, x265·SVT-AV1·WMV2는 단일 패스 ABR로 처리.</summary>
    private static bool SupportsTwoPass(string format) =>
        format is "mp4-h264" or "mkv" or "mov" or "webm" or "avi";

    private VideoSettings? ReadSettings(bool showErrors)
    {
        if (FormatBox?.SelectedItem is not ComboBoxItem formatItem) return null;
        var format = (string)formatItem.Tag;
        var ext = format switch
        {
            "mp4-h264" or "mp4-h265" or "mp4-av1" => ".mp4",
            "mkv" => ".mkv",
            "mov" => ".mov",
            "webm" => ".webm",
            "avi" => ".avi",
            "wmv" => ".wmv",
            "m4a" => ".m4a",
            "wav" => ".wav",
            _ => ".mp3"
        };

        int targetHeight = ResolutionBox.SelectedItem is ComboBoxItem r ? int.Parse((string)r.Tag) : 0;
        int.TryParse(CustomW.Text, out var cw);
        int.TryParse(CustomH.Text, out var chh);
        int fps = FpsBox.SelectedItem is ComboBoxItem f ? int.Parse((string)f.Tag) : 0;

        int mode = ModeCrf.IsChecked == true ? 0 : ModeBitrate.IsChecked == true ? 1 : 2;
        int crf = (int)CrfSlider.Value;

        int bitrate = 0;
        if (mode == 1 && (!int.TryParse(BitrateBox.Text, out bitrate) || bitrate <= 0))
        {
            if (showErrors)
                MessageBox.Show("비트레이트(kbps)를 올바르게 입력하세요.", "OctoConverter",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        double targetMb = 0;
        if (mode == 2 && (!double.TryParse(TargetSizeBox.Text, out targetMb) || targetMb <= 0))
        {
            if (showErrors)
                MessageBox.Show("목표 용량(MB)을 올바르게 입력하세요.", "OctoConverter",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        int audioKbps = AudioBitrateBox.SelectedItem is ComboBoxItem a ? int.Parse((string)a.Tag) : 128;
        return new VideoSettings(format, ext, targetHeight, cw, chh, fps, mode, crf, bitrate, targetMb,
            audioKbps, RemoveAudioCheck.IsChecked == true);
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateVisibility();
        RequestEstimate();
    }

    private void UpdateVisibility()
    {
        var s = ReadSettings(false);
        if (s is null || ResolutionPanel is null) return;

        bool audioOnly = IsAudioOnly(s.Format);
        ResolutionPanel.Visibility = audioOnly ? Visibility.Collapsed : Visibility.Visible;
        FpsPanel.Visibility = audioOnly ? Visibility.Collapsed : Visibility.Visible;
        QualityModePanel.Visibility = audioOnly ? Visibility.Collapsed : Visibility.Visible;
        RemoveAudioCheck.Visibility = audioOnly ? Visibility.Collapsed : Visibility.Visible;
        AudioPanel.Visibility = s.Format == "wav" ? Visibility.Collapsed : Visibility.Visible;

        bool custom = s.TargetHeight == -1;
        CustomW.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        CustomX.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        CustomH.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===== 변환 =====

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (!FFmpegService.IsAvailable)
        {
            MessageBox.Show("동영상 변환에는 FFmpeg이 필요합니다. 상단 배너에서 설치해 주세요.",
                "OctoConverter", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var items = FileList.Files.ToList();
        if (items.Count == 0)
        {
            MessageBox.Show("변환할 파일을 추가하세요.", "OctoConverter",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var s = ReadSettings(showErrors: true);
        if (s is null) return;

        string? outFolder;
        try { outFolder = Output.GetOutputFolder(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "OctoConverter", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _cts = new CancellationTokenSource();
        ConvertBtn.IsEnabled = false;
        CancelBtn.Visibility = Visibility.Visible;
        try
        {
            // 동영상 인코딩은 CPU를 많이 쓰므로 동시 2개까지만
            var (ok, fail, cancel) = await ConversionRunner.RunAsync(items, 2,
                (item, ct) => ConvertOneAsync(item, s, outFolder, ct), _cts.Token);
            EstimateText.Text = $"완료 {ok}개"
                + (fail > 0 ? $" · 실패 {fail}개" : "")
                + (cancel > 0 ? $" · 취소 {cancel}개" : "");
        }
        finally
        {
            ConvertBtn.IsEnabled = true;
            CancelBtn.Visibility = Visibility.Collapsed;
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task ConvertOneAsync(FileItem item, VideoSettings s, string? outFolder, CancellationToken ct)
    {
        var info = await MediaProbe.ProbeAsync(item.FilePath);
        double duration = info?.Duration ?? 0;
        var progress = new Progress<double>(p => item.Progress = p);

        var outPath = ConversionRunner.GetOutputPath(item.FilePath, outFolder, s.Ext);
        item.OutputPath = outPath;
        string input = $"-i {FFmpegService.Quote(item.FilePath)}";
        string q = FFmpegService.Quote(outPath);

        // 오디오만 추출
        if (IsAudioOnly(s.Format))
        {
            string codecArgs = s.Format switch
            {
                "mp3" => $"-c:a libmp3lame -b:a {Math.Max(s.AudioKbps, 128)}k",
                "m4a" => $"-c:a aac -b:a {Math.Max(s.AudioKbps, 128)}k",
                _ => "-c:a pcm_s16le"
            };
            await FFmpegService.RunAsync($"{input} -vn {codecArgs} {q}", duration, progress, ct);
            return;
        }

        string vf = BuildVideoFilter(info, s);
        string vcodec = s.Format switch
        {
            "mp4-h265" => "-c:v libx265 -preset medium -tag:v hvc1",
            "mp4-av1" => "-c:v libsvtav1 -preset 7",
            "webm" => "-c:v libvpx-vp9 -row-mt 1 -deadline good -cpu-used 2",
            "avi" => "-c:v mpeg4 -vtag xvid",
            "wmv" => "-c:v wmv2",
            _ => "-c:v libx264 -preset medium"
        };
        string audio = s.RemoveAudio
            ? "-an"
            : s.Format switch
            {
                "webm" => $"-c:a libopus -b:a {s.AudioKbps}k",
                "avi" => $"-c:a libmp3lame -b:a {s.AudioKbps}k",
                "wmv" => $"-c:a wmav2 -b:a {s.AudioKbps}k",
                _ => $"-c:a aac -b:a {s.AudioKbps}k"
            };
        string container = s.Ext is ".mp4" or ".mov" ? "-movflags +faststart " : "";
        string common = $"{vf} -pix_fmt yuv420p {container}";

        if (s.QualityMode == 2)
        {
            // 목표 용량: 오디오 몫을 뺀 영상 비트레이트를 계산
            if (duration <= 0)
                throw new InvalidOperationException("길이를 알 수 없어 목표 용량을 계산할 수 없습니다.");
            int totalKbps = (int)(s.TargetMB * 1024 * 1024 * 8 / 1000 / duration * 0.97);
            int audioKbps = s.RemoveAudio ? 0 : s.AudioKbps;
            int videoKbps = Math.Max(50, totalKbps - audioKbps);

            if (!SupportsTwoPass(s.Format))
            {
                await FFmpegService.RunAsync(
                    $"{input} {vcodec} -b:v {videoKbps}k {common}{audio} {q}",
                    duration, progress, ct);
            }
            else
            {
                var log = Path.Combine(Path.GetTempPath(), "octo2pass_" + Guid.NewGuid().ToString("N"));
                try
                {
                    var pass1 = new Progress<double>(p => item.Progress = p / 2);
                    var pass2 = new Progress<double>(p => item.Progress = 50 + p / 2);
                    await FFmpegService.RunAsync(
                        $"{input} {vcodec} -b:v {videoKbps}k -pass 1 -passlogfile {FFmpegService.Quote(log)} " +
                        $"{vf} -an -f null NUL",
                        duration, pass1, ct);
                    await FFmpegService.RunAsync(
                        $"{input} {vcodec} -b:v {videoKbps}k -pass 2 -passlogfile {FFmpegService.Quote(log)} " +
                        $"{common}{audio} {q}",
                        duration, pass2, ct);
                }
                finally
                {
                    CleanupPassLogs(log);
                }
            }
        }
        else if (s.QualityMode == 1)
        {
            await FFmpegService.RunAsync(
                $"{input} {vcodec} -b:v {s.BitrateKbps}k {common}{audio} {q}",
                duration, progress, ct);
        }
        else
        {
            await FFmpegService.RunAsync(
                $"{input} {vcodec} {CrfArgs(s)} {common}{audio} {q}",
                duration, progress, ct);
        }
    }

    /// <summary>코덱별 품질 스케일 차이를 보정한 CRF/qscale 인자.</summary>
    private static string CrfArgs(VideoSettings s) => s.Format switch
    {
        "webm" => $"-crf {Math.Min(s.Crf + 8, 63)} -b:v 0",
        "mp4-av1" => $"-crf {Math.Min(s.Crf + 12, 63)}",
        // MPEG-4·WMV2는 CRF가 없어 qscale(2~31)로 근사 변환
        "avi" or "wmv" => $"-q:v {Math.Clamp((int)Math.Round(2 + (s.Crf - 14) * 1.1), 2, 31)}",
        _ => $"-crf {s.Crf}"
    };

    private static string BuildVideoFilter(MediaInfo? info, VideoSettings s)
    {
        var filters = new List<string>();
        if (s.Fps > 0) filters.Add($"fps={s.Fps}");

        if (s.TargetHeight == -1 && s.CustomWidth > 0 && s.CustomHeight > 0)
            filters.Add($"scale={s.CustomWidth & ~1}:{s.CustomHeight & ~1}");
        else if (s.TargetHeight > 0 && (info is null || info.Height == 0 || info.Height > s.TargetHeight))
            filters.Add($"scale=-2:{s.TargetHeight}");
        else
            filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2"); // 홀수 해상도 방지

        return $"-vf \"{string.Join(",", filters)}\"";
    }

    private static void CleanupPassLogs(string logBase)
    {
        try
        {
            var dir = Path.GetDirectoryName(logBase)!;
            var name = Path.GetFileName(logBase);
            foreach (var f in Directory.EnumerateFiles(dir, name + "*"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ===== 예상 용량 =====

    private async void RequestEstimate()
    {
        _estimateCts?.Cancel();
        var cts = _estimateCts = new CancellationTokenSource();

        var s = ReadSettings(false);
        if (s is null || FileList.Files.Count == 0 || !FFmpegService.IsAvailable)
        {
            EstimateText.Text = "";
            return;
        }

        try { await Task.Delay(350, cts.Token); }
        catch (OperationCanceledException) { return; }

        double total = 0;
        int counted = 0;
        foreach (var f in FileList.Files.ToList())
        {
            var info = await MediaProbe.ProbeAsync(f.FilePath);
            if (cts.IsCancellationRequested) return;
            if (info is null || info.Duration <= 0) continue;
            total += EstimateBytes(info, s);
            counted++;
        }
        if (cts.IsCancellationRequested) return;

        if (counted == 0) { EstimateText.Text = ""; return; }
        string note = s.QualityMode == 0 && !IsAudioOnly(s.Format) ? " (CRF 특성상 근사치)" : "";
        EstimateText.Text = $"예상 용량: 약 {Formatters.Bytes(total)} ({counted}개 기준){note}";
    }

    private static double EstimateBytes(MediaInfo info, VideoSettings s)
    {
        double dur = info.Duration;
        if (IsAudioOnly(s.Format))
        {
            if (s.Format == "wav")
            {
                int sr = info.SampleRate > 0 ? info.SampleRate : 44100;
                int ch = info.Channels > 0 ? info.Channels : 2;
                return dur * sr * ch * 2;
            }
            return dur * Math.Max(s.AudioKbps, 128) * 1000 / 8;
        }

        if (s.QualityMode == 2)
            return s.TargetMB * 1024 * 1024;

        double audioBps = s.RemoveAudio ? 0 : s.AudioKbps * 1000;

        if (s.QualityMode == 1)
            return (s.BitrateKbps * 1000.0 + audioBps) * dur / 8;

        // CRF 모드: 해상도×프레임 기반 경험적 근사 (H.264 CRF23 ≈ 0.09 bit/pixel)
        double w = info.Width, h = info.Height;
        if (s.TargetHeight > 0 && h > s.TargetHeight)
        {
            w = w * s.TargetHeight / h;
            h = s.TargetHeight;
        }
        else if (s.TargetHeight == -1 && s.CustomWidth > 0 && s.CustomHeight > 0)
        {
            w = s.CustomWidth;
            h = s.CustomHeight;
        }
        double fps = s.Fps > 0 ? s.Fps : (info.Fps > 0 ? info.Fps : 30);
        double bpp = (s.Format switch
        {
            "mp4-h265" => 0.055,
            "mp4-av1" => 0.045,
            "webm" => 0.06,
            "avi" => 0.20,
            "wmv" => 0.22,
            _ => 0.09
        }) * Math.Pow(2, (23 - s.Crf) / 6.0);
        double videoBps = w * h * fps * bpp;
        return (videoBps + audioBps) * dur / 8;
    }
}
