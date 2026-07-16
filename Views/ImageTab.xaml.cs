using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using OctoConverter.Models;
using OctoConverter.Services;

namespace OctoConverter.Views;

public partial class ImageTab : UserControl
{
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _estimateCts;

    private static readonly string[] Extensions =
    [
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".jfif", ".ico",
        ".avif", ".heic", ".heif", ".tga", ".dds", ".jxr", ".wdp", ".jp2"
    ];

    /// <summary>FFmpeg 프로세스로 인코딩하는 출력 형식.</summary>
    private static bool UsesFFmpeg(string ext) => ext is ".webp" or ".avif";

    public ImageTab()
    {
        InitializeComponent();
        FileList.DialogFilter =
            "이미지 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.jfif;*.ico;" +
            "*.avif;*.heic;*.heif;*.tga;*.dds;*.jxr;*.wdp;*.jp2|모든 파일|*.*";
        foreach (var ext in Extensions) FileList.AcceptedExtensions.Add(ext);
        FileList.FilesAdded += OnFilesAdded;
        FileList.ListChanged += (_, _) => RequestEstimate();
    }

    private void Tab_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateVisibility();
    }

    private async void OnFilesAdded(IReadOnlyList<FileItem> items)
    {
        foreach (var item in items)
        {
            try
            {
                var (w, h) = await Task.Run(() => ImageCodec.GetPixelSize(item.FilePath));
                item.Info = $"{w}×{h}";
            }
            catch
            {
                // WIC이 못 읽는 형식(AVIF·HEIC 등)은 ffprobe로 시도
                var mi = await MediaProbe.ProbeAsync(item.FilePath);
                item.Info = mi is { Width: > 0 } ? $"{mi.Width}×{mi.Height}" : "정보 읽기 실패";
            }
        }
    }

    // ===== 설정 =====

    private sealed record ImageSettings(
        string Ext, int Quality, int ResizeMode, double ResizeW, double ResizeH, long? TargetBytes);

    private ImageSettings? ReadSettings(bool showErrors)
    {
        if (FormatBox?.SelectedItem is not ComboBoxItem formatItem) return null;
        var ext = (string)formatItem.Tag;
        int quality = (int)QualitySlider.Value;
        int mode = ResizeBox.SelectedIndex;
        double.TryParse(ResizeW.Text, out var w);
        double.TryParse(ResizeH.Text, out var h);

        long? target = null;
        if (TargetSizeCheck.IsChecked == true && SupportsTargetSize(ext))
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
        return new ImageSettings(ext, quality, mode, w, h, target);
    }

    private static bool SupportsTargetSize(string ext) => ext is ".jpg" or ".webp" or ".avif";
    private static bool SupportsQuality(string ext) => ext is ".jpg" or ".webp" or ".avif";

    private static (int W, int H) ComputeTargetSize(int srcW, int srcH, ImageSettings s)
    {
        switch (s.ResizeMode)
        {
            case 1: // 비율(%)
                var pct = s.ResizeW > 0 ? s.ResizeW / 100.0 : 1;
                return (Math.Max(1, (int)Math.Round(srcW * pct)),
                        Math.Max(1, (int)Math.Round(srcH * pct)));
            case 2: // 가로 기준
                if (s.ResizeW <= 0) return (srcW, srcH);
                return ((int)s.ResizeW, Math.Max(1, (int)Math.Round(srcH * s.ResizeW / srcW)));
            case 3: // 세로 기준
                if (s.ResizeW <= 0) return (srcW, srcH);
                return (Math.Max(1, (int)Math.Round(srcW * s.ResizeW / srcH)), (int)s.ResizeW);
            case 4: // 정확한 크기
                return (s.ResizeW > 0 ? (int)s.ResizeW : srcW,
                        s.ResizeH > 0 ? (int)s.ResizeH : srcH);
            default:
                return (srcW, srcH);
        }
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateVisibility();
        RequestEstimate();
    }

    private void UpdateVisibility()
    {
        if (FormatBox?.SelectedItem is not ComboBoxItem formatItem || ResizeBox is null) return;
        var ext = (string)formatItem.Tag;

        QualityPanel.Visibility = SupportsQuality(ext) ? Visibility.Visible : Visibility.Collapsed;

        int mode = ResizeBox.SelectedIndex;
        ResizeW.Visibility = mode >= 1 ? Visibility.Visible : Visibility.Collapsed;
        ResizeX.Visibility = mode == 4 ? Visibility.Visible : Visibility.Collapsed;
        ResizeH.Visibility = mode == 4 ? Visibility.Visible : Visibility.Collapsed;

        bool canTarget = SupportsTargetSize(ext);
        TargetSizeCheck.IsEnabled = canTarget;
        TargetSizeBox.IsEnabled = canTarget;
        TargetUnitBox.IsEnabled = canTarget;
        if (!canTarget) TargetSizeCheck.IsChecked = false;
    }

    // ===== 변환 =====

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var items = FileList.Files.ToList();
        if (items.Count == 0)
        {
            MessageBox.Show("변환할 파일을 추가하세요.", "OctoConverter",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var s = ReadSettings(showErrors: true);
        if (s is null) return;
        if (UsesFFmpeg(s.Ext) && !FFmpegService.IsAvailable)
        {
            MessageBox.Show("WebP·AVIF 변환에는 FFmpeg이 필요합니다. 상단 배너에서 설치해 주세요.",
                "OctoConverter", MessageBoxButton.OK, MessageBoxImage.Information);
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
        SetConverting(true);
        try
        {
            int parallel = UsesFFmpeg(s.Ext) ? 2 : Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
            var (ok, fail, cancel) = await ConversionRunner.RunAsync(items, parallel,
                (item, ct) => Task.Run(() => ConvertOneAsync(item, s, outFolder, ct), ct), _cts.Token);
            EstimateText.Text = $"완료 {ok}개"
                + (fail > 0 ? $" · 실패 {fail}개" : "")
                + (cancel > 0 ? $" · 취소 {cancel}개" : "");
        }
        finally
        {
            SetConverting(false);
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task ConvertOneAsync(FileItem item, ImageSettings s, string? outFolder, CancellationToken ct)
    {
        var src = await ImageCodec.LoadAsync(item.FilePath, ct);
        item.Progress = 25;

        var (tw, th) = ComputeTargetSize(src.PixelWidth, src.PixelHeight, s);
        var resized = ImageCodec.Resize(src, tw, th);
        item.Progress = 45;

        var outPath = ConversionRunner.GetOutputPath(item.FilePath, outFolder, s.Ext);
        item.OutputPath = outPath;

        if (UsesFFmpeg(s.Ext))
        {
            await EncodeViaFFmpegAsync(resized, s, outPath, ct);
        }
        else
        {
            var data = s.TargetBytes is long tb
                ? ImageCodec.EncodeToTarget(resized, s.Ext, tb, out _)
                : ImageCodec.Encode(resized, s.Ext, s.Quality);
            item.Progress = 85;
            await File.WriteAllBytesAsync(outPath, data, ct);
        }
    }

    /// <summary>WebP·AVIF: 임시 PNG를 거쳐 FFmpeg으로 인코딩. 목표 용량은 품질 이진 탐색.</summary>
    private static async Task EncodeViaFFmpegAsync(BitmapSource img, ImageSettings s, string outPath, CancellationToken ct)
    {
        // AVIF는 알파를 지원하지 않는 파이프라인(yuv420p)이라 흰 배경으로 합성
        var source = s.Ext == ".avif" ? ImageCodec.FlattenWhite(img) : img;
        var tmp = Path.Combine(Path.GetTempPath(), "octoimg_" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await File.WriteAllBytesAsync(tmp, ImageCodec.Encode(source, ".png", 100), ct);

            if (s.TargetBytes is long target)
            {
                int lo = 5, hi = 100, best = -1, last = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    await RunImage(tmp, outPath, s.Ext, mid, ct);
                    last = mid;
                    if (new FileInfo(outPath).Length <= target) { best = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                int final = best > 0 ? best : 5;
                if (final != last) await RunImage(tmp, outPath, s.Ext, final, ct);
            }
            else
            {
                await RunImage(tmp, outPath, s.Ext, s.Quality, ct);
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static Task RunImage(string input, string output, string ext, int quality, CancellationToken ct)
    {
        string args = ext switch
        {
            ".avif" =>
                // 품질(10~100) → libaom CRF(1~63) 역매핑
                $"-i {FFmpegService.Quote(input)} -frames:v 1 -c:v libaom-av1 -still-picture 1 " +
                $"-crf {Math.Clamp((int)Math.Round((100 - quality) * 0.58), 1, 63)} -b:v 0 " +
                $"-cpu-used 6 -row-mt 1 -pix_fmt yuv420p {FFmpegService.Quote(output)}",
            _ =>
                $"-i {FFmpegService.Quote(input)} -c:v libwebp -quality {quality} {FFmpegService.Quote(output)}"
        };
        return FFmpegService.RunAsync(args, 0, null, ct);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetConverting(bool on)
    {
        ConvertBtn.IsEnabled = !on;
        CancelBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===== 예상 용량 (첫 파일을 실제 인코딩해서 추정) =====

    private async void RequestEstimate()
    {
        _estimateCts?.Cancel();
        var cts = _estimateCts = new CancellationTokenSource();

        var s = ReadSettings(showErrors: false);
        var first = FileList.Files.FirstOrDefault();
        if (s is null || first is null)
        {
            EstimateText.Text = "";
            return;
        }
        if (UsesFFmpeg(s.Ext))
        {
            EstimateText.Text = s.TargetBytes is long t
                ? $"목표 용량 모드: 파일당 {Formatters.Bytes(t)} 이하로 품질 자동 조절"
                : "WebP·AVIF는 변환 후 실제 용량이 표시됩니다.";
            return;
        }

        try
        {
            await Task.Delay(400, cts.Token); // 입력 중 연타 방지
            long totalIn = FileList.Files.Sum(f => f.SizeBytes);
            long est = await Task.Run(() =>
            {
                var img = ImageCodec.Load(first.FilePath);
                var (tw, th) = ComputeTargetSize(img.PixelWidth, img.PixelHeight, s);
                var resized = ImageCodec.Resize(img, tw, th);
                var data = s.TargetBytes is long tb
                    ? ImageCodec.EncodeToTarget(resized, s.Ext, tb, out _)
                    : ImageCodec.Encode(resized, s.Ext, s.Quality);
                return data.LongLength;
            }, cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            double ratio = first.SizeBytes > 0 ? (double)est / first.SizeBytes : 1;
            string text = $"예상 용량: {first.FileName} → {Formatters.Bytes(est)}";
            if (FileList.Files.Count > 1)
                text += $" · 전체 약 {Formatters.Bytes(totalIn * ratio)}";
            EstimateText.Text = text;
        }
        catch (OperationCanceledException) { }
        catch { EstimateText.Text = ""; }
    }
}
