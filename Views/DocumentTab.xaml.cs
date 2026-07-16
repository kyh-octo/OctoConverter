using System.IO;
using System.Windows;
using System.Windows.Controls;
using OctoConverter.Models;
using OctoConverter.Services;

namespace OctoConverter.Views;

public partial class DocumentTab : UserControl
{
    private CancellationTokenSource? _cts;

    public DocumentTab()
    {
        InitializeComponent();
        FileList.DialogFilter =
            "문서·이미지 파일|*.doc;*.docx;*.docm;*.rtf;*.odt;*.txt;" +
            "*.xls;*.xlsx;*.xlsm;*.csv;*.ods;*.ppt;*.pptx;*.pptm;*.odp;" +
            "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.avif;*.heic|모든 파일|*.*";
        foreach (var ext in DocumentConverter.WordExtensions
                     .Concat(DocumentConverter.ExcelExtensions)
                     .Concat(DocumentConverter.PowerPointExtensions)
                     .Concat(DocumentConverter.ImageExtensions))
            FileList.AcceptedExtensions.Add(ext);
        FileList.FilesAdded += OnFilesAdded;
    }

    private void Tab_Loaded(object sender, RoutedEventArgs e)
    {
        EngineText.Text = DocumentConverter.EngineSummary();
    }

    private async void OnFilesAdded(IReadOnlyList<FileItem> items)
    {
        foreach (var item in items)
        {
            var kind = DocumentConverter.KindOf(Path.GetExtension(item.FilePath));
            if (kind == DocKind.Image)
            {
                try
                {
                    var (w, h) = await Task.Run(() => ImageCodec.GetPixelSize(item.FilePath));
                    item.Info = $"이미지 · {w}×{h}";
                }
                catch
                {
                    var mi = await MediaProbe.ProbeAsync(item.FilePath);
                    item.Info = mi is { Width: > 0 } ? $"이미지 · {mi.Width}×{mi.Height}" : "이미지";
                }
            }
            else
            {
                item.Info = DocumentConverter.KindName(kind);
                if (!DocumentConverter.CanConvert(kind))
                    item.Info += " · 변환 엔진 없음";
            }
        }
    }

    private sealed record DocSettings(PdfPageMode PageMode, int Quality, bool Merge);

    private DocSettings ReadSettings() => new(
        (PdfPageMode)PageModeBox.SelectedIndex,
        (int)QualitySlider.Value,
        MergeCheck.IsChecked == true);

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

        // 변환 불가능한 문서 종류가 섞여 있으면 미리 안내
        var missing = items
            .Select(f => DocumentConverter.KindOf(Path.GetExtension(f.FilePath)))
            .Where(k => k != DocKind.Image && !DocumentConverter.CanConvert(k))
            .Distinct()
            .Select(DocumentConverter.KindName)
            .ToList();
        if (missing.Count > 0)
        {
            MessageBox.Show(
                $"{string.Join(", ", missing)} 변환에는 Microsoft Office 또는 LibreOffice 설치가 필요합니다.",
                "OctoConverter", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var s = ReadSettings();
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
            var images = items.Where(f =>
                DocumentConverter.KindOf(Path.GetExtension(f.FilePath)) == DocKind.Image).ToList();
            var docs = items.Except(images).ToList();

            int ok = 0, fail = 0, cancel = 0;

            // 이미지 합치기 모드: 이미지들을 PDF 한 개로
            if (s.Merge && images.Count > 1)
            {
                try
                {
                    await ConvertMergedImagesAsync(images, s, outFolder, _cts.Token);
                    ok += images.Count;
                }
                catch (OperationCanceledException)
                {
                    foreach (var it in images) { it.Status = "취소됨"; }
                    cancel += images.Count;
                }
                catch (Exception ex)
                {
                    foreach (var it in images) { it.Status = "오류"; it.ResultText = ex.Message; }
                    fail += images.Count;
                }
                images = [];
            }

            var remaining = docs.Concat(images).ToList();
            if (remaining.Count > 0 && !_cts.IsCancellationRequested)
            {
                // Office COM은 동시 실행이 불안정하므로 문서가 있으면 순차 처리
                int parallel = docs.Count > 0 ? 1 : Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
                var (o, f, c) = await ConversionRunner.RunAsync(remaining, parallel,
                    (item, ct) => ConvertOneAsync(item, s, outFolder, ct), _cts.Token);
                ok += o; fail += f; cancel += c;
            }

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

    private async Task ConvertOneAsync(FileItem item, DocSettings s, string? outFolder, CancellationToken ct)
    {
        var kind = DocumentConverter.KindOf(Path.GetExtension(item.FilePath));
        var outPath = ConversionRunner.GetOutputPath(item.FilePath, outFolder, ".pdf");
        item.OutputPath = outPath;

        if (kind == DocKind.Image)
        {
            var src = await ImageCodec.LoadAsync(item.FilePath, ct);
            item.Progress = 40;
            await Task.Run(() =>
            {
                var jpeg = ImageCodec.Encode(src, ".jpg", s.Quality);
                item.Progress = 75;
                PdfWriter.WriteImagesPdf(outPath, [(jpeg, src.PixelWidth, src.PixelHeight)], s.PageMode);
            }, ct);
        }
        else
        {
            item.Progress = 25; // Office 변환은 중간 진행률을 알 수 없음
            await DocumentConverter.ConvertOfficeAsync(kind, item.FilePath, outPath, ct);
        }
    }

    private async Task ConvertMergedImagesAsync(List<FileItem> images, DocSettings s,
        string? outFolder, CancellationToken ct)
    {
        foreach (var it in images) { it.Status = "대기"; it.Progress = 0; it.ResultText = ""; }

        var outPath = ConversionRunner.GetOutputPath(images[0].FilePath, outFolder, ".pdf");
        var pages = new List<(byte[] Jpeg, int Width, int Height)>(images.Count);
        int done = 0;
        foreach (var item in images)
        {
            ct.ThrowIfCancellationRequested();
            item.Status = "변환 중";
            var src = await ImageCodec.LoadAsync(item.FilePath, ct);
            var jpeg = await Task.Run(() => ImageCodec.Encode(src, ".jpg", s.Quality), ct);
            pages.Add((jpeg, src.PixelWidth, src.PixelHeight));
            item.Progress = 90;
            done++;
        }
        await Task.Run(() => PdfWriter.WriteImagesPdf(outPath, pages, s.PageMode), ct);

        long size = new FileInfo(outPath).Length;
        images[0].OutputPath = outPath;
        for (int i = 0; i < images.Count; i++)
        {
            images[i].Status = "완료";
            images[i].Progress = 100;
            images[i].ResultText = i == 0
                ? $"{Path.GetFileName(outPath)} ({Formatters.Bytes(size)})"
                : $"→ {Path.GetFileName(outPath)} {i + 1}쪽";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
