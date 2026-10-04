using System.Globalization;
using System.IO;

namespace OctoConverter.Services;

public sealed record AnimOptions(
    string Ext, double Fps, int Width, int Colors, bool Dither, bool LoopForever,
    int WebpQuality, long? TargetBytes, double ScalePercent = 100);

/// <summary>최종 파일 크기를 검증한 뒤에만 결과 파일을 저장한다.</summary>
public static class AnimationEncoder
{
    public static async Task ConvertAsync(string inputPath, string outPath, AnimOptions o,
        MediaInfo? info, IProgress<double>? progress, Action<string>? note, CancellationToken ct)
    {
        if (o.TargetBytes is <= 0 || !double.IsFinite(o.Fps) || o.Fps < 0 || o.Fps > 240 ||
            o.Width < 0 || o.Width == 1 || o.Width > 16384 || o.WebpQuality is < 1 or > 100 ||
            o.Colors is < 2 or > 256 || !double.IsFinite(o.ScalePercent) || o.ScalePercent <= 0 || o.ScalePercent > 100)
            throw new ArgumentException("프레임, 크기 또는 목표 용량 설정이 올바르지 않습니다.");
        o = o with { Width = ResolveWidth(o, info), ScalePercent = 100 };
        string tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!,
            ".octoanim_" + Guid.NewGuid().ToString("N") + o.Ext);
        try
        {
            if (o.TargetBytes is long target)
            {
                switch (o.Ext)
                {
                    case ".mp4":
                    case ".webm":
                        await EncodeVideoTargetAsync(inputPath, tmp, o, info, target, progress, note, ct);
                        break;
                    case ".webp":
                        await EncodeWebpTargetAsync(inputPath, tmp, o, info, target, progress, note, ct);
                        break;
                    default:
                        await EncodeScaleTargetAsync(inputPath, tmp, o, info, target, progress, note, ct);
                        break;
                }
                long size = new FileInfo(tmp).Length;
                if (size >= target) throw TargetFailure(target, size);
                note?.Invoke($"목표 충족: {size:N0}바이트 / {target:N0}바이트 미만");
            }
            else
                await FFmpegService.RunAsync(BuildArgs(inputPath, tmp, o, o.Fps, o.Width, o.WebpQuality),
                    info?.Duration ?? 0, progress, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, outPath); // 기존 사용자 파일을 덮어쓰지 않는다.
            progress?.Report(100);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public static int ResolveWidth(AnimOptions o, MediaInfo? info)
    {
        if (o.Width > 0) return o.Width;
        if (o.ScalePercent == 100) return 0;
        if (info is not { Width: > 0 })
            throw new InvalidOperationException("원본 너비를 알 수 없어 비율 축소를 적용할 수 없습니다.");
        return Math.Max(2, (int)Math.Round(info.Width * o.ScalePercent / 100));
    }

    private static string Rate(double fps) => fps.ToString("0.########", CultureInfo.InvariantCulture);

    // 각 재시도 진행률은 보여 주되, 실제 크기를 검증하기 전에는 100%를 보고하지 않는다.
    private sealed class AttemptProgress(IProgress<double>? parent, double offset = 0, double scale = 1) : IProgress<double>
    {
        public void Report(double value) => parent?.Report(Math.Min(99, offset + value * scale));
    }

    private static List<string> Filters(double fps, int width)
    {
        var filters = new List<string>();
        if (fps > 0) filters.Add($"fps={Rate(fps)}");
        if (width > 0) filters.Add($"scale={width}:-2:flags=lanczos");
        return filters;
    }

    private static string BuildArgs(string input, string output, AnimOptions o,
        double fps, int width, int webpQuality)
    {
        var filters = Filters(fps, width);
        string inArg = $"-i {FFmpegService.Quote(input)}";
        string q = FFmpegService.Quote(output);
        string vf = filters.Count > 0 ? $"-vf \"{string.Join(",", filters)}\" " : "";
        switch (o.Ext)
        {
            case ".gif":
                string pre = filters.Count > 0 ? string.Join(",", filters) + "," : "";
                string dither = o.Dither ? "sierra2_4a" : "none";
                return $"{inArg} -filter_complex \"[0:v:0]{pre}split[a][b];" +
                    $"[a]palettegen=max_colors={o.Colors}[p];[b][p]paletteuse=dither={dither}\" " +
                    $"-loop {(o.LoopForever ? 0 : -1)} {q}";
            case ".apng":
                return $"{inArg} {vf}-an -c:v apng -f apng -plays {(o.LoopForever ? 0 : 1)} {q}";
            case ".webp":
                return $"{inArg} {vf}-c:v libwebp -quality {webpQuality} " +
                    $"-loop {(o.LoopForever ? 0 : 1)} -an {q}";
            default:
                filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
                string codec = o.Ext == ".webm"
                    ? "-c:v libvpx-vp9 -row-mt 1 -crf 32 -b:v 0 -c:a libopus -b:a 128k"
                    : "-c:v libx264 -preset veryfast -crf 20 -movflags +faststart -c:a aac -b:a 128k";
                return $"{inArg} -vf \"{string.Join(",", filters)}\" {codec} -pix_fmt yuv420p {q}";
        }
    }

    private static async Task EncodeVideoTargetAsync(string input, string output, AnimOptions o,
        MediaInfo? info, long target, IProgress<double>? progress, Action<string>? note, CancellationToken ct)
    {
        double duration = info?.Duration ?? 0;
        if (!double.IsFinite(duration) || duration <= 0)
            throw new InvalidOperationException("길이를 알 수 없어 목표 용량을 계산할 수 없습니다.");
        // 컨테이너 여유를 남기고 오디오도 목표 예산에 맞춰 배분한다.
        double totalKbps = target * 8.0 / 1000 / duration * 0.90;
        bool hasAudio = info?.HasAudio == true;
        int audioKbps = hasAudio ? Math.Clamp((int)(totalKbps * 0.25), 16, 128) : 0;
        int videoKbps = (int)(totalKbps - audioKbps);
        if (videoKbps < 8)
            throw new InvalidOperationException("재생 길이와 오디오를 유지하기에 목표 용량이 너무 작습니다. 용량을 늘려 주세요.");
        var filters = Filters(o.Fps, o.Width);
        filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        string vf = $"-vf \"{string.Join(",", filters)}\"";
        string inArg = $"-i {FFmpegService.Quote(input)} -map 0:v:0";
        string vcodec = o.Ext == ".webm" ? "-c:v libvpx-vp9 -row-mt 1" : "-c:v libx264 -preset veryfast";
        string container = o.Ext == ".mp4" ? "-movflags +faststart " : "";
        long lastSize = 0;
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var log = Path.Combine(Path.GetTempPath(), "octo2pass_" + Guid.NewGuid().ToString("N"));
            try
            {
                note?.Invoke($"{attempt}차 2-pass: 영상 {videoKbps}kbps · 오디오 {audioKbps}kbps");
                await FFmpegService.RunAsync(
                    $"{inArg} {vcodec} -b:v {videoKbps}k -pass 1 -passlogfile {FFmpegService.Quote(log)} " +
                    $"{vf} -pix_fmt yuv420p -an -f null NUL", duration, new AttemptProgress(progress, 0, .5), ct);
                string audio = !hasAudio ? "-an" : o.Ext == ".webm"
                    ? $"-map 0:a:0? -c:a libopus -b:a {audioKbps}k"
                    : $"-map 0:a:0? -c:a aac -b:a {audioKbps}k";
                await FFmpegService.RunAsync(
                    $"{inArg} {vcodec} -b:v {videoKbps}k -pass 2 -passlogfile {FFmpegService.Quote(log)} " +
                    $"{vf} -pix_fmt yuv420p {container}{audio} {FFmpegService.Quote(output)}", duration, new AttemptProgress(progress, 50, .5), ct);
            }
            finally { CleanupPassLogs(log); }
            lastSize = new FileInfo(output).Length;
            if (lastSize < target) return;
            double ratio = target * 0.90 / lastSize;
            int nextVideo = Math.Max(8, Math.Min(videoKbps - 1, (int)(videoKbps * ratio)));
            int nextAudio = hasAudio ? Math.Max(16, Math.Min(audioKbps, (int)(audioKbps * ratio))) : 0;
            note?.Invoke($"{Formatters.Bytes(lastSize)}로 목표 초과 → 비트레이트 재조정");
            if (nextVideo == videoKbps && nextAudio == audioKbps) break;
            videoKbps = nextVideo;
            audioKbps = nextAudio;
        }
        throw TargetFailure(target, lastSize);
    }

    private static async Task EncodeScaleTargetAsync(string input, string output, AnimOptions o,
        MediaInfo? info, long target, IProgress<double>? progress, Action<string>? note, CancellationToken ct)
    {
        int width = o.Width > 0 ? o.Width : info is { Width: > 0 } ? info.Width : 480;
        int minWidth = Math.Min(width, 16);
        double fps = o.Fps;
        int colors = o.Colors;
        long lastSize = 0;
        for (int attempt = 1; attempt <= 32; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await FFmpegService.RunAsync(BuildArgs(input, output, o with { Colors = colors }, fps, width, o.WebpQuality),
                info?.Duration ?? 0, new AttemptProgress(progress), ct);
            lastSize = new FileInfo(output).Length;
            if (lastSize < target) return;
            if (width > minWidth)
            {
                double factor = Math.Min(0.85, Math.Sqrt(target * 0.92 / lastSize));
                width = Math.Max(minWidth, (int)(width * factor));
            }
            else
            {
                double currentFps = fps > 0 ? fps : info is { Fps: > 0 } ? info.Fps : 15;
                if (currentFps > 1) fps = Math.Max(1, Math.Floor(currentFps * 0.70));
                else if (o.Ext == ".gif" && colors > 32) colors = Math.Max(32, colors / 2);
                else break;
            }
            note?.Invoke($"{attempt}차 {Formatters.Bytes(lastSize)} → {width}px · " +
                $"{(fps > 0 ? Rate(fps) + "fps" : "원본 fps")} 재시도");
        }
        throw TargetFailure(target, lastSize);
    }

    private static async Task EncodeWebpTargetAsync(string input, string output, AnimOptions o,
        MediaInfo? info, long target, IProgress<double>? progress, Action<string>? note, CancellationToken ct)
    {
        int lo = 1, hi = o.WebpQuality;
        string bestFile = output + ".best";
        try
        {
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                await FFmpegService.RunAsync(BuildArgs(input, output, o, o.Fps, o.Width, mid),
                    info?.Duration ?? 0, new AttemptProgress(progress), ct);
                long size = new FileInfo(output).Length;
                note?.Invoke($"품질 {mid}: {Formatters.Bytes(size)}");
                if (size < target)
                {
                    File.Copy(output, bestFile, overwrite: true);
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            if (File.Exists(bestFile)) File.Move(bestFile, output, overwrite: true);
            else await EncodeScaleTargetAsync(input, output, o with { WebpQuality = 1 }, info, target, progress, note, ct);
        }
        finally { try { File.Delete(bestFile); } catch { } }
    }

    private static InvalidOperationException TargetFailure(long target, long size) => new(
        $"목표 용량을 달성하지 못했습니다 ({size:N0}바이트 / 목표 {target:N0}바이트 미만). " +
        "초과 파일은 저장하지 않았습니다. 목표 용량을 늘려 주세요.");

    private static void CleanupPassLogs(string logBase)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(logBase)!, Path.GetFileName(logBase) + "*"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }
}
