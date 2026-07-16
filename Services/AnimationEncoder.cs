using System.IO;

namespace OctoConverter.Services;

public sealed record AnimOptions(
    string Ext, int Fps, int Width, int Colors, bool Dither, bool LoopForever,
    int WebpQuality, long? TargetBytes);

/// <summary>
/// 애니메이션 변환기. 목표 용량이 지정되면 형식별 전략으로 자동 조절한다:
///  - GIF/APNG: 크기(폭) → 프레임 순으로 반복 축소
///  - WebP: 품질 이진 탐색, 최저 품질로도 초과하면 크기 축소로 전환
///  - MP4/WebM: 비트레이트 계산 후 2-pass 인코딩
/// </summary>
public static class AnimationEncoder
{
    public static async Task ConvertAsync(string inputPath, string outPath, AnimOptions o,
        MediaInfo? info, IProgress<double>? progress, Action<string>? note, CancellationToken ct)
    {
        if (o.TargetBytes is long target)
        {
            switch (o.Ext)
            {
                case ".mp4":
                case ".webm":
                    await EncodeVideoTargetAsync(inputPath, outPath, o, info, target, progress, ct);
                    return;
                case ".webp":
                    await EncodeWebpTargetAsync(inputPath, outPath, o, info, target, progress, note, ct);
                    return;
                default:
                    await EncodeScaleTargetAsync(inputPath, outPath, o, info, target, progress, note, ct);
                    return;
            }
        }

        await FFmpegService.RunAsync(
            BuildArgs(inputPath, outPath, o, o.Fps, o.Width, o.WebpQuality),
            info?.Duration ?? 0, progress, ct);
    }

    private static string BuildArgs(string input, string output, AnimOptions o,
        int fps, int width, int webpQuality)
    {
        var filters = new List<string>();
        if (fps > 0) filters.Add($"fps={fps}");
        if (width > 0) filters.Add($"scale={width}:-2:flags=lanczos");
        string inArg = $"-i {FFmpegService.Quote(input)}";
        string q = FFmpegService.Quote(output);

        switch (o.Ext)
        {
            case ".gif":
            {
                // palettegen/paletteuse 2단계 필터로 화질 좋은 GIF 생성
                string pre = filters.Count > 0 ? string.Join(",", filters) + "," : "";
                string dither = o.Dither ? "sierra2_4a" : "none";
                return $"{inArg} -filter_complex \"[0:v]{pre}split[a][b];" +
                       $"[a]palettegen=max_colors={o.Colors}[p];[b][p]paletteuse=dither={dither}\" " +
                       $"-loop {(o.LoopForever ? 0 : -1)} {q}";
            }
            case ".apng":
                return $"{inArg} {Vf(filters)}-c:v apng -f apng -plays {(o.LoopForever ? 0 : 1)} {q}";
            case ".webp":
                return $"{inArg} {Vf(filters)}-c:v libwebp -quality {webpQuality} " +
                       $"-loop {(o.LoopForever ? 0 : 1)} -an {q}";
            case ".webm":
                filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
                return $"{inArg} -vf \"{string.Join(",", filters)}\" " +
                       $"-c:v libvpx-vp9 -row-mt 1 -crf 32 -b:v 0 -pix_fmt yuv420p " +
                       $"-c:a libopus -b:a 128k {q}";
            default: // ".mp4"
                filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2"); // H.264는 짝수 해상도 필요
                return $"{inArg} -vf \"{string.Join(",", filters)}\" " +
                       $"-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p " +
                       $"-movflags +faststart -c:a aac -b:a 128k {q}";
        }

        static string Vf(List<string> filters) =>
            filters.Count > 0 ? $"-vf \"{string.Join(",", filters)}\" " : "";
    }

    // ===== MP4/WebM: 비트레이트 계산 + 2-pass =====

    private static async Task EncodeVideoTargetAsync(string input, string output, AnimOptions o,
        MediaInfo? info, long target, IProgress<double>? progress, CancellationToken ct)
    {
        double duration = info?.Duration ?? 0;
        if (duration <= 0)
            throw new InvalidOperationException("길이를 알 수 없어 목표 용량을 계산할 수 없습니다.");

        bool hasAudio = info?.HasAudio == true;
        int audioKbps = hasAudio ? 128 : 0;
        int totalKbps = (int)(target * 8.0 / 1000 / duration * 0.97);
        int videoKbps = Math.Max(20, totalKbps - audioKbps);

        var filters = new List<string>();
        if (o.Fps > 0) filters.Add($"fps={o.Fps}");
        if (o.Width > 0) filters.Add($"scale={o.Width}:-2:flags=lanczos");
        filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        string vf = $"-vf \"{string.Join(",", filters)}\"";
        string inArg = $"-i {FFmpegService.Quote(input)}";
        string vcodec = o.Ext == ".webm"
            ? "-c:v libvpx-vp9 -row-mt 1"
            : "-c:v libx264 -preset veryfast";
        string audio = !hasAudio ? "-an"
            : o.Ext == ".webm" ? "-c:a libopus -b:a 128k" : "-c:a aac -b:a 128k";
        string container = o.Ext == ".mp4" ? "-movflags +faststart " : "";

        var log = Path.Combine(Path.GetTempPath(), "octo2pass_" + Guid.NewGuid().ToString("N"));
        try
        {
            var pass1 = progress is null ? null : new Progress<double>(p => progress.Report(p / 2));
            var pass2 = progress is null ? null : new Progress<double>(p => progress.Report(50 + p / 2));
            await FFmpegService.RunAsync(
                $"{inArg} {vcodec} -b:v {videoKbps}k -pass 1 -passlogfile {FFmpegService.Quote(log)} " +
                $"{vf} -an -f null NUL",
                duration, pass1, ct);
            await FFmpegService.RunAsync(
                $"{inArg} {vcodec} -b:v {videoKbps}k -pass 2 -passlogfile {FFmpegService.Quote(log)} " +
                $"{vf} -pix_fmt yuv420p {container}{audio} {FFmpegService.Quote(output)}",
                duration, pass2, ct);
        }
        finally
        {
            CleanupPassLogs(log);
        }
    }

    // ===== GIF/APNG: 폭 → 프레임 반복 축소 =====

    private static async Task EncodeScaleTargetAsync(string input, string output, AnimOptions o,
        MediaInfo? info, long target, IProgress<double>? progress, Action<string>? note, CancellationToken ct)
    {
        int width = o.Width > 0 ? o.Width : (info is { Width: > 0 } ? info.Width : 480);
        int fps = o.Fps;
        int[] fpsSteps = [15, 12, 10, 8, 6];
        const int maxAttempts = 6;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await FFmpegService.RunAsync(
                BuildArgs(input, output, o, fps, width, o.WebpQuality),
                info?.Duration ?? 0, null, ct);
            long size = new FileInfo(output).Length;
            progress?.Report(Math.Min(attempt * 100.0 / maxAttempts, 99));

            if (size <= target)
            {
                progress?.Report(100);
                return;
            }
            if (attempt == maxAttempts)
            {
                note?.Invoke($"목표 초과: 최소 설정에서 {Formatters.Bytes(size)}");
                progress?.Report(100);
                return;
            }

            // 용량은 대략 픽셀 수에 비례 → 폭을 sqrt(비율)만큼 축소
            double ratio = (double)target / size;
            int newWidth = Math.Max((int)(width * Math.Sqrt(ratio) * 0.97) & ~1, 64);

            if (newWidth >= width)
            {
                // 폭을 더 못 줄이면 프레임을 한 단계 낮춘다
                double curFps = fps > 0 ? fps : (info is { Fps: > 0 } ? info.Fps : 15);
                int next = fpsSteps.FirstOrDefault(f => f < curFps);
                if (next == 0)
                {
                    note?.Invoke($"목표 초과: 최소 설정에서 {Formatters.Bytes(size)}");
                    progress?.Report(100);
                    return;
                }
                fps = next;
            }
            else
            {
                width = newWidth;
            }
            note?.Invoke($"{attempt}차 {Formatters.Bytes(size)} → {width}px{(fps > 0 ? $"·{fps}fps" : "")} 재시도");
        }
    }

    // ===== WebP: 품질 이진 탐색 =====

    private static async Task EncodeWebpTargetAsync(string input, string output, AnimOptions o,
        MediaInfo? info, long target, IProgress<double>? progress, Action<string>? note, CancellationToken ct)
    {
        int lo = 5, hi = 100, best = -1, lastEncoded = -1;
        int step = 0;
        const int maxSteps = 7;

        while (lo <= hi)
        {
            ct.ThrowIfCancellationRequested();
            int mid = (lo + hi) / 2;
            await FFmpegService.RunAsync(
                BuildArgs(input, output, o, o.Fps, o.Width, mid),
                info?.Duration ?? 0, null, ct);
            lastEncoded = mid;
            long size = new FileInfo(output).Length;
            progress?.Report(Math.Min(++step * 100.0 / maxSteps, 99));
            note?.Invoke($"품질 {mid}: {Formatters.Bytes(size)}");

            if (size <= target) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        if (best < 0)
        {
            // 최저 품질로도 초과 → 저품질 고정 후 크기 축소로 전환
            await EncodeScaleTargetAsync(input, output, o with { WebpQuality = 30 },
                info, target, progress, note, ct);
            return;
        }
        if (best != lastEncoded)
        {
            await FFmpegService.RunAsync(
                BuildArgs(input, output, o, o.Fps, o.Width, best),
                info?.Duration ?? 0, null, ct);
        }
        progress?.Report(100);
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
}
