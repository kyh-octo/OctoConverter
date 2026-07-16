using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;

namespace OctoConverter.Services;

/// <summary>
/// FFmpeg 실행 파일 탐색·자동 설치·실행(진행률 파싱)을 담당한다.
/// 탐색 순서: 프로그램 폴더 → 전용 설치 폴더(%LocalAppData%) → PATH
/// </summary>
public static class FFmpegService
{
    public static string? FFmpegPath { get; private set; }
    public static string? FFprobePath { get; private set; }
    public static bool IsAvailable => FFmpegPath is not null && FFprobePath is not null;

    public static string ToolDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OctoConverter", "ffmpeg");

    private const string DownloadUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";

    public static void Locate()
    {
        string? ffmpeg = null, ffprobe = null;
        foreach (var dir in CandidateDirs())
        {
            var f = Path.Combine(dir, "ffmpeg.exe");
            var p = Path.Combine(dir, "ffprobe.exe");
            if (ffmpeg is null && File.Exists(f)) ffmpeg = f;
            if (ffprobe is null && File.Exists(p)) ffprobe = p;
            if (ffmpeg is not null && ffprobe is not null) break;
        }
        FFmpegPath = ffmpeg;
        FFprobePath = ffprobe;
    }

    private static IEnumerable<string> CandidateDirs()
    {
        yield return AppContext.BaseDirectory;
        yield return ToolDir;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string d;
            try { d = Environment.ExpandEnvironmentVariables(dir); } catch { continue; }
            bool exists;
            try { exists = Directory.Exists(d); } catch { continue; }
            if (exists) yield return d;
        }
    }

    /// <summary>BtbN 공식 빌드를 내려받아 ffmpeg.exe / ffprobe.exe만 추출한다.</summary>
    public static async Task DownloadAsync(IProgress<(double Percent, string Message)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolDir);
        var zipPath = Path.Combine(ToolDir, "_download.zip");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            {
                progress.Report((0, "다운로드 준비 중..."));
                using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1;

                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress.Report(total > 0
                        ? (done * 90.0 / total, $"다운로드 중... {Formatters.Bytes(done)} / {Formatters.Bytes(total)}")
                        : (45, $"다운로드 중... {Formatters.Bytes(done)}"));
                }
            }

            progress.Report((92, "압축 해제 중..."));
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    var entry = zip.Entries.FirstOrDefault(e =>
                        e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (entry is null)
                        throw new InvalidOperationException($"압축 파일에서 {name}을(를) 찾지 못했습니다.");
                    entry.ExtractToFile(Path.Combine(ToolDir, name), overwrite: true);
                }
            }
            progress.Report((100, "설치 완료"));
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }
        Locate();
    }

    /// <summary>
    /// FFmpeg 실행. durationSeconds를 알면 out_time 기반으로 진행률(0~100)을 보고한다.
    /// 실패 시 stderr 마지막 줄을 담은 예외를 던진다.
    /// </summary>
    public static async Task RunAsync(string arguments, double durationSeconds,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (FFmpegPath is null)
            throw new InvalidOperationException("FFmpeg이 설치되어 있지 않습니다.");

        var psi = new ProcessStartInfo
        {
            FileName = FFmpegPath,
            Arguments = "-hide_banner -y -nostdin -progress pipe:1 -nostats " + arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = new Process { StartInfo = psi };
        var errTail = new Queue<string>();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null || durationSeconds <= 0) return;
            if (e.Data.StartsWith("out_time_us=") &&
                long.TryParse(e.Data.AsSpan("out_time_us=".Length), out var us))
            {
                progress?.Report(Math.Clamp(us / 1_000_000.0 / durationSeconds * 100.0, 0, 99.5));
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (errTail)
            {
                errTail.Enqueue(e.Data);
                while (errTail.Count > 30) errTail.Dequeue();
            }
        };

        if (!proc.Start())
            throw new InvalidOperationException("FFmpeg 실행에 실패했습니다.");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        await proc.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        if (proc.ExitCode != 0)
        {
            string last;
            lock (errTail)
                last = errTail.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "알 수 없는 오류";
            throw new InvalidOperationException("FFmpeg 오류: " + last);
        }
        progress?.Report(100);
    }

    public static string Quote(string path) => "\"" + path + "\"";
}
