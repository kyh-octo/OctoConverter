using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using OctoConverter.Models;
using OctoConverter.Services;

namespace OctoConverter.Views;

public partial class IconTab : UserControl
{
    private CancellationTokenSource? _cts;

    public IconTab()
    {
        InitializeComponent();
        FileList.DialogFilter =
            "이미지 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.jfif;" +
            "*.avif;*.heic;*.heif;*.tga;*.dds;*.jxr;*.wdp;*.jp2|모든 파일|*.*";
        foreach (var ext in new[]
                 {
                     ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".jfif",
                     ".avif", ".heic", ".heif", ".tga", ".dds", ".jxr", ".wdp", ".jp2"
                 })
            FileList.AcceptedExtensions.Add(ext);
        FileList.FilesAdded += OnFilesAdded;
    }

    private async void OnFilesAdded(IReadOnlyList<FileItem> items)
    {
        foreach (var item in items)
        {
            try
            {
                var (w, h) = await Task.Run(() => ImageCodec.GetPixelSize(item.FilePath));
                item.Info = $"{w}×{h}" + (w != h ? " (정사각형 아님)" : "");
            }
            catch
            {
                // WIC이 못 읽는 형식(AVIF·HEIC 등)은 ffprobe로 시도
                var mi = await MediaProbe.ProbeAsync(item.FilePath);
                item.Info = mi is { Width: > 0 }
                    ? $"{mi.Width}×{mi.Height}" + (mi.Width != mi.Height ? " (정사각형 아님)" : "")
                    : "정보 읽기 실패";
            }
        }
    }

    private int[] SelectedSizes()
    {
        var list = new List<int>();
        if (Size16.IsChecked == true) list.Add(16);
        if (Size24.IsChecked == true) list.Add(24);
        if (Size32.IsChecked == true) list.Add(32);
        if (Size48.IsChecked == true) list.Add(48);
        if (Size64.IsChecked == true) list.Add(64);
        if (Size128.IsChecked == true) list.Add(128);
        if (Size256.IsChecked == true) list.Add(256);
        return list.ToArray();
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var items = FileList.Files.ToList();
        if (items.Count == 0)
        {
            MessageBox.Show("변환할 이미지를 추가하세요.", "OctoConverter",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var sizes = SelectedSizes();
        if (sizes.Length == 0)
        {
            MessageBox.Show("포함할 크기를 한 개 이상 선택하세요.", "OctoConverter",
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
            var (ok, fail, cancel) = await ConversionRunner.RunAsync(items,
                Math.Clamp(Environment.ProcessorCount - 1, 1, 4),
                (item, ct) => Task.Run(() => ConvertOneAsync(item, sizes, outFolder, ct), ct), _cts.Token);
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

    private async Task ConvertOneAsync(FileItem item, int[] sizes, string? outFolder, CancellationToken ct)
    {
        var src = await ImageCodec.LoadAsync(item.FilePath, ct);
        var images = new List<(int, BitmapSource)>(sizes.Length);
        int done = 0;
        foreach (var size in sizes)
        {
            ct.ThrowIfCancellationRequested();
            images.Add((size, ImageCodec.FitSquare(src, size)));
            item.Progress = ++done * 90.0 / sizes.Length;
        }

        var outPath = ConversionRunner.GetOutputPath(item.FilePath, outFolder, ".ico");
        item.OutputPath = outPath;
        IcoWriter.Write(outPath, images);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
