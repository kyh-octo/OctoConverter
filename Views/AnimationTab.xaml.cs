using System.Windows;
using System.Windows.Controls;
using OctoConverter.Models;
using OctoConverter.Services;

namespace OctoConverter.Views;

public partial class AnimationTab : UserControl
{
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _estimateCts;

    public AnimationTab()
    {
        InitializeComponent();
        FileList.DialogFilter =
            "애니메이션·동영상 파일|*.gif;*.mp4;*.mov;*.avi;*.webm;*.mkv;*.apng;*.png;*.webp;" +
            "*.wmv;*.flv;*.m4v;*.ts;*.mts;*.m2ts;*.3gp;*.mpg;*.mpeg;*.ogv|모든 파일|*.*";
        foreach (var ext in new[]
                 {
                     ".gif", ".mp4", ".mov", ".avi", ".webm", ".mkv", ".apng", ".png", ".webp",
                     ".wmv", ".flv", ".m4v", ".ts", ".mts", ".m2ts", ".3gp", ".mpg", ".mpeg", ".ogv"
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

    private AnimOptions? ReadSettings(bool showErrors)
    {
        if (FormatBox?.SelectedItem is not ComboBoxItem formatItem) return null;

        long? target = null;
        if (TargetSizeCheck.IsChecked == true)
        {
            if (!double.TryParse(TargetSizeBox.Text, out var t) || t <= 0)
            {
                if (showErrors)
                    MessageBox.Show("목표 용량을 올바르게 입력하세요.", "OctoConverter",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            target = (long)(t * (TargetUnitBox.SelectedIndex == 0 ? 1024 : 1024 * 1024));
        }

        return new AnimOptions(
            (string)formatItem.Tag,
            FpsBox.SelectedItem is ComboBoxItem f ? int.Parse((string)f.Tag) : 0,
            WidthBox.SelectedItem is ComboBoxItem w ? int.Parse((string)w.Tag) : 0,
            ColorsBox.SelectedItem is ComboBoxItem c ? int.Parse((string)c.Tag) : 256,
            DitherCheck.IsChecked == true,
            LoopBox.SelectedIndex == 0,
            (int)WebpQualitySlider.Value,
            target);
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
        if (s is null || ColorsPanel is null) return;
        bool video = s.Ext is ".mp4" or ".webm";
        ColorsPanel.Visibility = s.Ext == ".gif" ? Visibility.Visible : Visibility.Collapsed;
        WebpPanel.Visibility = s.Ext == ".webp" ? Visibility.Visible : Visibility.Collapsed;
        LoopPanel.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
    }

    // ===== 변환 =====

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (!FFmpegService.IsAvailable)
        {
            MessageBox.Show("애니메이션 변환에는 FFmpeg이 필요합니다. 상단 배너에서 설치해 주세요.",
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
            // 목표 용량 모드는 반복 인코딩이라 병렬 1, 일반 변환은 2
            int parallel = s.TargetBytes is null ? 2 : 1;
            var (ok, fail, cancel) = await ConversionRunner.RunAsync(items, parallel,
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

    private async Task ConvertOneAsync(FileItem item, AnimOptions s, string? outFolder, CancellationToken ct)
    {
        var info = await MediaProbe.ProbeAsync(item.FilePath);
        var outPath = ConversionRunner.GetOutputPath(item.FilePath, outFolder, s.Ext);
        item.OutputPath = outPath;
        await AnimationEncoder.ConvertAsync(item.FilePath, outPath, s, info,
            new Progress<double>(p => item.Progress = p),
            msg => item.ResultText = msg, ct);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ===== 예상 용량 (형식별 경험적 근사치) =====

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

        if (s.TargetBytes is long target)
        {
            EstimateText.Text =
                $"목표 용량 모드: 파일당 {Formatters.Bytes(target)} 이하로 자동 조절합니다.";
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
            if (info is null || info.Duration <= 0 || info.Width <= 0) continue;
            total += EstimateBytes(info, s);
            counted++;
        }
        if (cts.IsCancellationRequested) return;

        EstimateText.Text = counted == 0
            ? ""
            : $"예상 용량: 약 {Formatters.Bytes(total)} (근사치, 내용에 따라 차이 큼)";
    }

    private static double EstimateBytes(MediaInfo info, AnimOptions s)
    {
        double srcW = info.Width, srcH = info.Height;
        double w = s.Width > 0 && s.Width < srcW ? s.Width : srcW;
        double h = srcH * (w / srcW);
        double fps = s.Fps > 0 ? s.Fps : (info.Fps > 0 ? info.Fps : 15);
        double frames = fps * info.Duration;
        double pixels = w * h * frames;

        return s.Ext switch
        {
            ".gif" => pixels * 0.10 * (0.5 + 0.5 * s.Colors / 256.0),
            ".apng" => pixels * 0.18,
            ".webp" => pixels * 0.015 * (s.WebpQuality / 75.0),
            ".webm" => w * h * fps * 0.05 * info.Duration / 8, // VP9 CRF32 근사
            _ => w * h * fps * 0.10 * info.Duration / 8,       // MP4 CRF20 근사
        };
    }
}
