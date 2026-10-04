using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using OctoConverter.Services;
using OctoConverter.Models;
using OctoConverter.Views;

internal static class Program
{
    private static int passed;
    [STAThread]
    private static int Main(string[] args)
    {
        var dir = Path.Combine(Path.GetTempPath(), "OctoConverter-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            FFmpegService.Locate();
            if (!FFmpegService.IsAvailable) throw new Exception("Regression suite requires FFmpeg and ffprobe.");
            if (args.Contains("--live-download")) LiveDownloadAsync(dir).GetAwaiter().GetResult();
            else { TestUi(dir); RunAsync(dir).GetAwaiter().GetResult(); }
            Console.WriteLine($"PASS: {passed} checks. Artifacts: {dir}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine("Artifacts: " + dir); return 1; }
    }
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception("FAIL: " + message);
        passed++;
        Console.WriteLine("PASS: " + message);
    }
    private static void TestUi(string dir)
    {
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/OctoConverter;component/Themes/Styles.xaml")
        });
        var tab = new AnimationTab();
        T Control<T>(string name) => (T)tab.FindName(name);
        var read = typeof(AnimationTab).GetMethod("ReadSettings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        AnimOptions? Read() => (AnimOptions?)read.Invoke(tab, [false]);
        var fps = Control<ComboBox>("FpsBox");
        fps.SelectedIndex = fps.Items.Count - 1;
        Control<TextBox>("FpsCustomBox").Text = "29.97";
        var width = Control<ComboBox>("WidthBox");
        width.SelectedIndex = width.Items.Count - 1;
        Control<TextBox>("WidthCustomBox").Text = "377";
        var o = Read()!;
        Check(o.Fps == 29.97 && o.Width == 377, "UI custom 29.97fps and 377px");
        Control<ComboBox>("ResizeModeBox").SelectedIndex = 1;
        Control<TextBox>("ScalePercentBox").Text = "37.5";
        o = Read()!;
        Check(o.ScalePercent == 37.5 && o.Width == 0, "UI percentage scaling");
        Control<CheckBox>("TargetSizeCheck").IsChecked = true;
        Control<TextBox>("TargetSizeBox").Text = "512";
        Control<ComboBox>("TargetUnitBox").SelectedIndex = 0;
        Check(Read()!.TargetBytes == 512000, "512 KB equals 512000 bytes");
        Control<TextBox>("TargetSizeBox").Text = "NaN";
        Check(Read() is null, "NaN target rejected");
        Control<TextBox>("TargetSizeBox").Text = "999999999999999999999999999999999";
        Check(Read() is null, "overflow target rejected");
        Control<TextBox>("TargetSizeBox").Text = "512";
        Control<TextBox>("FpsCustomBox").Text = "Infinity";
        Check(Read() is null, "infinite fps rejected");
        Control<TextBox>("FpsCustomBox").Text = "29.97";
        Control<TextBox>("ScalePercentBox").Text = "0";
        Check(Read() is null, "zero percentage rejected");
        Control<TextBox>("ScalePercentBox").Text = "50";
        var visibility = typeof(AnimationTab).GetMethod("UpdateVisibility", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var host = new Grid { Background = System.Windows.Media.Brushes.WhiteSmoke };
        host.Children.Add(tab);
        foreach (int mode in new[] { 0, 1 })
        {
            Control<ComboBox>("ResizeModeBox").SelectedIndex = mode;
            visibility.Invoke(tab, null);
            host.Measure(new Size(780, 620)); host.Arrange(new Rect(0, 0, 780, 620)); host.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(780, 620, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(host);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(dir, $"animation-ui-{mode}.png")); png.Save(file);
            var button = Control<Button>("ConvertBtn");
            var bottom = button.TranslatePoint(new Point(0, button.ActualHeight), host);
            Check(bottom.Y <= 620.5 && button.ActualWidth > 0, "780px UI mode " + mode + " keeps convert button visible");
        }
        Control<TextBox>("ScalePercentBox").Text = "invalid";
        Control<ComboBox>("ResizeModeBox").SelectedIndex = 0;
        visibility.Invoke(tab, null);
        Check(Control<TextBox>("WidthCustomBox").Visibility == Visibility.Visible &&
            Control<TextBox>("ScalePercentBox").Visibility == Visibility.Collapsed, "invalid entry does not block mode switching");
    }
    private static async Task RunAsync(string dir)
    {
        var ct = CancellationToken.None;
        string input = Path.Combine(dir, "source.mp4");
        await FFmpegService.RunAsync(
            $"-f lavfi -i \"testsrc2=size=320x240:rate=30:duration=8\" -f lavfi -i \"sine=frequency=440:duration=8\" " +
            $"-c:v libx264 -preset ultrafast -crf 8 -c:a aac -shortest {FFmpegService.Quote(input)}", 0, null, ct);
        var info = (await MediaProbe.ProbeAsync(input))!;
        foreach (string ext in new[] { ".gif", ".apng", ".webp", ".mp4", ".webm" })
        {
            string output = Path.Combine(dir, "target" + ext);
            var o = new AnimOptions(ext, 30, 320, 256, true, true, 75, 512000);
            await AnimationEncoder.ConvertAsync(input, output, o, info, null,
                s => Console.WriteLine(ext + ": " + s), ct);
            long size = new FileInfo(output).Length;
            Check(size > 0 && size < 512000, $"{ext} strict 512 KB target: {size} bytes");
            if (ext is ".mp4" or ".webm")
            {
                var converted = (await MediaProbe.ProbeAsync(output))!;
                Check(converted.HasAudio && Math.Abs(converted.Duration - info.Duration) < .6,
                    ext + " keeps audio and full timeline");
            }
        }
        string custom = Path.Combine(dir, "custom.apng");
        await AnimationEncoder.ConvertAsync(input, custom,
            new(".apng", 12.5, 377, 256, true, false, 75, null), info, null, null, ct);
        var customInfo = (await MediaProbe.ProbeAsync(custom))!;
        Check(customInfo.Width == 377 && Math.Abs(customInfo.Fps - 12.5) < .2, "custom width and fractional fps encoded");
        string percent = Path.Combine(dir, "percent.mp4");
        await AnimationEncoder.ConvertAsync(input, percent,
            new(".mp4", 15, 0, 256, true, false, 75, null, 50), info, null, null, ct);
        Check((await MediaProbe.ProbeAsync(percent))!.Width == 160, "50% scaling encodes per-source width");
        Check(AnimationEncoder.ResolveWidth(new(".gif", 0, 0, 256, true, true, 75, null, 50),
            info with { Width = 800 }) == 400, "batch percentage uses each source width");

        string impossible = Path.Combine(dir, "impossible.gif");
        try
        {
            await AnimationEncoder.ConvertAsync(input, impossible,
                new(".gif", 1, 16, 32, false, false, 75, 1), info, null, null, ct);
            throw new Exception("Impossible target accepted");
        }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("목표 용량"), "impossible target is an explicit error"); }
        Check(!File.Exists(impossible), "oversized output is not published");
        var item = new FileItem(input);
        var result = await ConversionRunner.RunAsync([item], 1, async (it, token) =>
        {
            it.OutputPath = impossible;
            await AnimationEncoder.ConvertAsync(input, impossible,
                new(".gif", 1, 16, 32, false, false, 75, 1), info, null, null, token);
        }, ct);
        Check(result.Ok == 0 && result.Fail == 1 && item.Status == "오류", "batch runner counts target failure as failure");
        string cancelled = Path.Combine(dir, "cancelled.gif");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try
        {
            await AnimationEncoder.ConvertAsync(input, cancelled,
                new(".gif", 30, 320, 256, true, true, 75, 512000), info, null, null, cancel.Token);
            throw new Exception("Cancellation ignored");
        }
        catch (OperationCanceledException) { Check(!File.Exists(cancelled), "cancellation leaves no output"); }
        Check(!Directory.EnumerateFiles(dir, ".octoanim_*").Any(), "temporary animation candidates cleaned");

        byte[] pixels = [0, 0, 255, 255, 255, 0, 0, 128, 0, 255, 0, 0];
        var pdf = PdfImage.WithAlpha(pixels, 3, 1, 90);
        using var compressed = new MemoryStream(pdf.SoftMask!);
        using var z = new ZLibStream(compressed, CompressionMode.Decompress);
        using var uncompressed = new MemoryStream();
        z.CopyTo(uncompressed);
        Check(uncompressed.ToArray().SequenceEqual(new byte[] { 255, 128, 0 }), "PDF soft mask preserves alpha");
        PdfWriter.WriteImagesPdf(Path.Combine(dir, "alpha.pdf"), [pdf], PdfPageMode.ImageSize);

        await TestDownloadsAsync(dir);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => send(r, ct);
    }
    private sealed class DownloadProgress : IProgress<(double Percent, string Message)>
    {
        public void Report((double Percent, string Message) value) { }
    }
    private static async Task TestDownloadsAsync(string dir)
    {
        var method = typeof(FFmpegService).GetMethod("DownloadFileAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        Task Download(HttpClient client, string file, CancellationToken ct) =>
            (Task)method.Invoke(null, [client, "https://test.invalid/package.zip", Path.Combine(dir, file), "test", new DownloadProgress(), ct])!;
        using var good = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent([1, 2, 3, 4]) })));
        await Download(good, "good.zip", CancellationToken.None);
        Check(new FileInfo(Path.Combine(dir, "good.zip")).Length == 4, "downloader writes complete content");
        using var missing = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        try { await Download(missing, "404.zip", CancellationToken.None); throw new Exception("404 accepted"); }
        catch (HttpRequestException) { Check(true, "HTTP failure triggers retry contract"); }
        var body = new ByteArrayContent([1, 2]);
        body.Headers.ContentLength = 8;
        using var shortFile = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body })));
        try { await Download(shortFile, "short.zip", CancellationToken.None); throw new Exception("Short download accepted"); }
        catch (IOException) { Check(true, "truncated download detected"); }
        using var slow = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }));
        using var cancel = new CancellationTokenSource(100);
        try { await Download(slow, "cancel.zip", cancel.Token); throw new Exception("Download cancellation ignored"); }
        catch (OperationCanceledException) { Check(true, "download header cancellation works"); }
        var install = typeof(FFmpegService).GetMethod("InstallPair", BindingFlags.Static | BindingFlags.NonPublic)!;
        string destination = Path.Combine(dir, "installed"), staging = Path.Combine(dir, "staged");
        Directory.CreateDirectory(destination); Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(destination, "ffmpeg.exe"), "original-ffmpeg");
        File.WriteAllText(Path.Combine(destination, "ffprobe.exe"), "original-ffprobe");
        File.WriteAllText(Path.Combine(staging, "ffmpeg.exe"), "replacement-ffmpeg");
        try { install.Invoke(null, [staging, destination, CancellationToken.None]); throw new Exception("Incomplete installation accepted"); }
        catch (TargetInvocationException ex) when (ex.InnerException is FileNotFoundException) { }
        Check(File.ReadAllText(Path.Combine(destination, "ffmpeg.exe")) == "original-ffmpeg" &&
            File.ReadAllText(Path.Combine(destination, "ffprobe.exe")) == "original-ffprobe", "failed installation restores both original files");
        File.WriteAllText(Path.Combine(staging, "ffmpeg.exe"), "replacement-ffmpeg");
        File.WriteAllText(Path.Combine(staging, "ffprobe.exe"), "replacement-ffprobe");
        install.Invoke(null, [staging, destination, CancellationToken.None]);
        Check(File.ReadAllText(Path.Combine(destination, "ffprobe.exe")) == "replacement-ffprobe", "validated pair installation succeeds");
    }
    private static async Task LiveDownloadAsync(string dir)
    {
        var type = typeof(FFmpegService);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OctoConverter-Regression");
        var resolve = type.GetMethod("ResolveMirrorsAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var mirrors = await (Task<List<(string Label, string Url)>>)resolve.Invoke(null, [http, CancellationToken.None])!;
        Check(mirrors.Count == 3 && mirrors[0].Url.EndsWith("-full_build.zip"), "live API resolves full static build and fallback mirrors");
        var download = type.GetMethod("DownloadFileAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        string zip = Path.Combine(dir, "full.zip"), extracted = Path.Combine(dir, "full");
        Directory.CreateDirectory(extracted);
        await (Task)download.Invoke(null, [http, mirrors[0].Url, zip, mirrors[0].Label, new DownloadProgress(), CancellationToken.None])!;
        type.GetMethod("ExtractPair", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [zip, extracted]);
        var validate = type.GetMethod("ValidateExecutableAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (string name in new[] { "ffmpeg.exe", "ffprobe.exe" })
        {
            await (Task)validate.Invoke(null, [Path.Combine(extracted, name), CancellationToken.None])!;
            Check(true, "downloaded full build " + name + " executes -version");
        }
        string destination = Path.Combine(dir, "live-installed");
        type.GetMethod("InstallPair", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [extracted, destination, CancellationToken.None]);
        Check(File.Exists(Path.Combine(destination, "ffmpeg.exe")) && File.Exists(Path.Combine(destination, "ffprobe.exe")),
            "real full build installed as a pair in isolated folder");
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(destination, "ffmpeg.exe"), "-encoders")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync(); var stderr = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(); string encoders = await stdout; await stderr;
        Check(new[] { "libsvtav1", "libwebp", "libopus", "libx264", "libvpx-vp9" }.All(encoders.Contains),
            "full build retains AVIF, WebP, Opus, H264 and VP9 encoders");
    }
}
