using System.Text;
using System.Windows;
using System.Windows.Controls;
using OctoConverter.Models;
using OctoConverter.Services;

namespace OctoConverter.Views;

public partial class MusicTab : UserControl
{
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _estimateCts;

    public MusicTab()
    {
        InitializeComponent();
        FileList.DialogFilter =
            "오디오·동영상 파일|*.mp3;*.wav;*.flac;*.m4a;*.m4b;*.aac;*.ogg;*.oga;*.opus;*.wma;" +
            "*.aiff;*.aif;*.ape;*.wv;*.ac3;*.dts;*.mp2;*.mka;*.amr;*.caf;" +
            "*.mp4;*.mkv;*.webm;*.avi;*.mov;*.wmv;*.flv;*.ts;*.3gp;*.mpg;*.mpeg;*.ogv|모든 파일|*.*";
        foreach (var ext in new[]
                 {
                     ".mp3", ".wav", ".flac", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".wma",
                     ".aiff", ".aif", ".ape", ".wv", ".ac3", ".dts", ".mp2", ".mka", ".amr", ".caf",
                     ".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".flv", ".ts", ".3gp", ".mpg", ".mpeg", ".ogv"
                 })
            FileList.AcceptedExtensions.Add(ext);
        FileList.FilesAdded += OnFilesAdded;
        FileList.ListChanged += (_, _) => RequestEstimate();
    }

    private void Tab_Loaded(object sender, RoutedEventArgs e) => RequestEstimate();

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

    private sealed record MusicSettings(
        string Ext, bool TargetMode, int Kbps, double TargetMB,
        int SampleRate, int Channels, bool Loudnorm);

    private MusicSettings? ReadSettings(bool showErrors)
    {
        if (FormatBox?.SelectedItem is not ComboBoxItem formatItem) return null;
        var ext = (string)formatItem.Tag;

        int kbps = BitrateBox.SelectedItem is ComboBoxItem b ? int.Parse((string)b.Tag) : 192;
        bool targetMode = ModeTarget.IsChecked == true;
        double targetMb = 0;
        if (targetMode)
        {
            if (!double.TryParse(TargetSizeBox.Text, out targetMb) || targetMb <= 0)
            {
                if (showErrors)
                    MessageBox.Show("목표 용량(MB)을 올바르게 입력하세요.", "OctoConverter",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
        }

        int sampleRate = SampleRateBox.SelectedItem is ComboBoxItem sr ? int.Parse((string)sr.Tag) : 0;
        int channels = ChannelBox.SelectedItem is ComboBoxItem ch ? int.Parse((string)ch.Tag) : 0;
        return new MusicSettings(ext, targetMode, kbps, targetMb, sampleRate, channels,
            LoudnormCheck.IsChecked == true);
    }

    private static bool SupportsBitrate(string ext) => ext is ".mp3" or ".m4a" or ".ogg" or ".opus";

    /// <summary>ALAC은 M4A 컨테이너를 쓴다.</summary>
    private static string OutExtOf(string ext) => ext == ".alac" ? ".m4a" : ext;

    private static int ComputeTargetKbps(double targetMb, double duration, string ext)
    {
        if (duration <= 0)
            throw new InvalidOperationException("길이를 알 수 없어 목표 용량을 계산할 수 없습니다.");
        // 컨테이너 오버헤드 약 3% 여유
        int kbps = (int)(targetMb * 1024 * 1024 * 8 / 1000 / duration * 0.97);
        int max = ext switch { ".ogg" => 480, ".opus" => 500, _ => 320 };
        return Math.Clamp(kbps, 32, max);
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
        if (s is null) return;
        bool lossy = SupportsBitrate(s.Ext);
        ModeBitrate.IsEnabled = lossy;
        ModeTarget.IsEnabled = lossy;
        BitrateBox.IsEnabled = lossy && ModeBitrate.IsChecked == true;
        TargetSizeBox.IsEnabled = lossy && ModeTarget.IsChecked == true;
        if (!lossy) ModeBitrate.IsChecked = true;
    }

    // ===== 변환 =====

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (!FFmpegService.IsAvailable)
        {
            MessageBox.Show("음악 변환에는 FFmpeg이 필요합니다. 상단 배너에서 설치해 주세요.",
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
        if (s.TargetMode && !SupportsBitrate(s.Ext))
        {
            MessageBox.Show("목표 용량은 MP3·M4A·Opus·OGG에서만 사용할 수 있습니다.", "OctoConverter",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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

    private async Task ConvertOneAsync(FileItem item, MusicSettings s, string? outFolder, CancellationToken ct)
    {
        var info = await MediaProbe.ProbeAsync(item.FilePath);
        double duration = info?.Duration ?? 0;
        int kbps = s.TargetMode ? ComputeTargetKbps(s.TargetMB, duration, s.Ext) : s.Kbps;

        // libopus는 44.1kHz를 지원하지 않으므로 48kHz로 대체
        int sampleRate = s.SampleRate;
        if (s.Ext == ".opus" && sampleRate == 44100) sampleRate = 48000;

        var args = new StringBuilder();
        args.Append($"-i {FFmpegService.Quote(item.FilePath)} -vn ");
        if (s.Loudnorm) args.Append("-af loudnorm ");
        if (sampleRate > 0) args.Append($"-ar {sampleRate} ");
        if (s.Channels > 0) args.Append($"-ac {s.Channels} ");
        args.Append(s.Ext switch
        {
            ".mp3" => $"-c:a libmp3lame -b:a {kbps}k ",
            ".wav" => "-c:a pcm_s16le ",
            ".flac" => "-c:a flac ",
            ".m4a" => $"-c:a aac -b:a {kbps}k ",
            ".ogg" => $"-c:a libvorbis -b:a {kbps}k ",
            ".opus" => $"-c:a libopus -b:a {kbps}k ",
            ".aiff" => "-c:a pcm_s16be ",
            ".alac" => "-c:a alac ",
            _ => throw new NotSupportedException("지원하지 않는 형식: " + s.Ext)
        });

        var outPath = ConversionRunner.GetOutputPath(item.FilePath, outFolder, OutExtOf(s.Ext));
        item.OutputPath = outPath;
        args.Append(FFmpegService.Quote(outPath));

        await FFmpegService.RunAsync(args.ToString(), duration,
            new Progress<double>(p => item.Progress = p), ct);
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

        EstimateText.Text = counted == 0
            ? ""
            : $"예상 용량: 약 {Formatters.Bytes(total)} ({counted}개 기준)";
    }

    private static double EstimateBytes(MediaInfo info, MusicSettings s)
    {
        double dur = info.Duration;
        int sr = s.SampleRate > 0 ? s.SampleRate : (info.SampleRate > 0 ? info.SampleRate : 44100);
        int ch = s.Channels > 0 ? s.Channels : (info.Channels > 0 ? info.Channels : 2);

        if (s.Ext is ".wav" or ".aiff") return dur * sr * ch * 2;
        if (s.Ext == ".flac") return dur * sr * ch * 2 * 0.6;  // 일반적인 FLAC 압축률
        if (s.Ext == ".alac") return dur * sr * ch * 2 * 0.65; // 일반적인 ALAC 압축률

        if (s.TargetMode) return s.TargetMB * 1024 * 1024;
        return dur * s.Kbps * 1000 / 8;
    }
}
