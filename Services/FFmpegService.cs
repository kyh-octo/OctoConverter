using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

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

    private const string GyanDownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-full.zip";
    private static readonly SemaphoreSlim DownloadLock = new(1, 1);

    public static void Locate()
    {
        foreach (var dir in CandidateDirs())
        {
            var f = Path.Combine(dir, "ffmpeg.exe");
            var p = Path.Combine(dir, "ffprobe.exe");
            if (File.Exists(f) && File.Exists(p))
            {
                FFmpegPath = f;
                FFprobePath = p;
                return;
            }
        }
        FFmpegPath = FFprobePath = null;
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

    /// <summary>Full FFmpeg 빌드를 내려받고 검증 후 원자적으로 설치한다.</summary>
    public static async Task DownloadAsync(IProgress<(double Percent, string Message)> progress, CancellationToken ct)
    {
        await DownloadLock.WaitAsync(ct);
        string staging = Path.Combine(Path.GetTempPath(), "OctoConverter-ffmpeg-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OctoConverter");
            progress.Report((0, "다운로드 서버를 찾는 중..."));
            var candidates = await ResolveMirrorsAsync(http, ct);
            Exception? lastError = null;
            foreach (var (label, url) in candidates)
            {
                ct.ThrowIfCancellationRequested();
                string zipPath = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".zip");
                try
                {
                    progress.Report((0, $"{label}에 연결하는 중..."));
                    await DownloadFileAsync(http, url, zipPath, label, progress, ct);
                    progress.Report((92, $"{label} 압축 검사 및 해제 중..."));
                    string extracted = Path.Combine(staging, "extract-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(extracted);
                    await Task.Run(() => ExtractPair(zipPath, extracted), ct);
                    await ValidateExecutableAsync(Path.Combine(extracted, "ffmpeg.exe"), ct);
                    await ValidateExecutableAsync(Path.Combine(extracted, "ffprobe.exe"), ct);
                    ct.ThrowIfCancellationRequested();
                    InstallPair(extracted, ToolDir, ct);
                    Locate();
                    if (!IsAvailable) throw new InvalidOperationException("설치한 FFmpeg 실행 파일 쌍을 찾지 못했습니다.");
                    progress.Report((100, "설치 완료"));
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lastError = ex;
                    progress.Report((0, $"{label} 실패 ({ex.Message}) → 다음 서버 시도"));
                }
                finally { try { File.Delete(zipPath); } catch { } }
            }
            throw new InvalidOperationException("모든 다운로드 서버에서 실패했습니다.\n" + (lastError?.Message ?? ""), lastError);
        }
        finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } DownloadLock.Release(); }
    }

    private static async Task<List<(string Label, string Url)>> ResolveMirrorsAsync(HttpClient http, CancellationToken ct)
    {
        var list = new List<(string, string)>();
        using var apiCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        apiCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await http.GetAsync("https://api.github.com/repos/GyanD/codexffmpeg/releases/latest", HttpCompletionOption.ResponseHeadersRead, apiCts.Token);
            if (response.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(apiCts.Token), cancellationToken: apiCts.Token);
                foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith("-full_build.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        string? url = asset.GetProperty("browser_download_url").GetString();
                        if (!string.IsNullOrWhiteSpace(url)) list.Add(("GitHub (gyan full 미러)", url));
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* API 실패/timeout이면 고정 후보로 진행 */ }
        list.Add(("GitHub (BtbN)", "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip"));
        list.Add(("gyan.dev full", GyanDownloadUrl));
        return list;
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string path, string label,
        IProgress<(double Percent, string Message)> progress, CancellationToken ct)
    {
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage response;
        try { response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headerCts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("30초 안에 다운로드 응답 헤더를 받지 못했습니다."); }
        using (response)
        {
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? -1;
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        streamCts.CancelAfter(TimeSpan.FromSeconds(30));
        Stream src;
        try { src = await response.Content.ReadAsStreamAsync(streamCts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("30초 안에 다운로드 스트림을 열지 못했습니다."); }
        await using (src)
        {
        await using var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true);
        var buffer = new byte[1 << 16]; long done = 0; var sw = Stopwatch.StartNew(); var lastReport = TimeSpan.Zero;
        while (true)
        {
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idleCts.CancelAfter(TimeSpan.FromSeconds(30));
            int n;
            try { n = await src.ReadAsync(buffer.AsMemory(), idleCts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("30초 동안 다운로드 응답이 없습니다."); }
            if (n == 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, n), ct); done += n;
            if (sw.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200))
            {
                lastReport = sw.Elapsed;
                double speed = done / Math.Max(.001, sw.Elapsed.TotalSeconds) / 1048576.0;
                string size = total > 0 ? $"{done / 1048576.0:0.0} / {total / 1048576.0:0.0} MB" : $"{done / 1048576.0:0.0} MB";
                progress.Report((total > 0 ? Math.Min(90, done * 90.0 / total) : 45, $"{label} 다운로드 중... {size} ({speed:0.0} MB/s)"));
            }
        }
        if (total >= 0 && done != total) throw new IOException($"다운로드 크기가 맞지 않습니다 (예상 {total}, 수신 {done} 바이트).");
        }
        }
    }

    private static void ExtractPair(string zipPath, string targetDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (string name in new[] { "ffmpeg.exe", "ffprobe.exe" })
        {
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) throw new InvalidDataException($"ZIP에서 {name}을(를) 찾지 못했습니다.");
            entry.ExtractToFile(Path.Combine(targetDir, name));
        }
    }

    private static async Task ValidateExecutableAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(path, "-version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"{Path.GetFileName(path)} 실행에 실패했습니다.");
        var stdout = proc.StandardOutput.ReadToEndAsync(); var stderr = proc.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch
        {
            try { proc.Kill(true); } catch { }
            try { await proc.WaitForExitAsync(CancellationToken.None); }
            finally { try { await Task.WhenAll(stdout, stderr); } catch { } }
            throw;
        }
        string output = await stdout + await stderr;
        string expected = Path.GetFileName(path).Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase) ? "ffprobe version" : "ffmpeg version";
        if (proc.ExitCode != 0 || !output.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Path.GetFileName(path)} -version 검증에 실패했습니다.");
    }

    private static void InstallPair(string sourceDir, string destinationDir, CancellationToken ct)
    {
        Directory.CreateDirectory(destinationDir);
        string backupDir = Path.Combine(Path.GetTempPath(), "OctoConverter-ffmpeg-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDir);
        var names = new[] { "ffmpeg.exe", "ffprobe.exe" };
        var backed = new List<string>(); var installed = new List<string>();
        bool committed = false;
        try
        {
            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                string dest = Path.Combine(destinationDir, name);
                if (File.Exists(dest)) { File.Move(dest, Path.Combine(backupDir, name)); backed.Add(name); }
            }
            foreach (var name in names) { ct.ThrowIfCancellationRequested(); File.Move(Path.Combine(sourceDir, name), Path.Combine(destinationDir, name)); installed.Add(name); }
            committed = true;
        }
        catch
        {
            foreach (var name in installed) try { File.Delete(Path.Combine(destinationDir, name)); } catch { }
            var restoreErrors = new List<Exception>();
            foreach (var name in backed)
            {
                try { File.Move(Path.Combine(backupDir, name), Path.Combine(destinationDir, name), true); }
                catch (Exception ex) { restoreErrors.Add(ex); }
            }
            if (restoreErrors.Count > 0)
                throw new AggregateException($"FFmpeg 설치에 실패했고 기존 파일 백업이 {backupDir}에 남아 있습니다.", restoreErrors);
            try { Directory.Delete(backupDir, true); } catch { }
            throw;
        }
        finally { if (committed) try { Directory.Delete(backupDir, true); } catch { } }
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
