using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OctoConverter.Services;

public sealed record MediaInfo(
    double Duration,
    int Width,
    int Height,
    double Fps,
    long BitrateBps,
    long AudioBitrateBps,
    bool HasVideo,
    bool HasAudio,
    string? VideoCodec,
    string? AudioCodec,
    int SampleRate,
    int Channels);

/// <summary>ffprobe로 미디어 정보를 읽는다. 결과는 경로별로 캐시.</summary>
public static class MediaProbe
{
    private static readonly ConcurrentDictionary<string, MediaInfo?> Cache = new();

    public static async Task<MediaInfo?> ProbeAsync(string path)
    {
        if (FFmpegService.FFprobePath is null) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FFmpegService.FFprobePath,
                Arguments = "-v quiet -print_format json -show_format -show_streams " + FFmpegService.Quote(path),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var json = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double duration = 0;
            long bitrate = 0;
            if (root.TryGetProperty("format", out var fmt))
            {
                duration = GetDouble(fmt, "duration");
                bitrate = (long)GetDouble(fmt, "bit_rate");
            }

            int w = 0, h = 0, sampleRate = 0, channels = 0;
            double fps = 0;
            long audioBitrate = 0;
            bool hasVideo = false, hasAudio = false;
            string? vCodec = null, aCodec = null;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    if (type == "video" && !hasVideo)
                    {
                        // MP3 앨범아트 같은 첨부 이미지는 영상 스트림으로 치지 않는다
                        if (s.TryGetProperty("disposition", out var disp) &&
                            disp.TryGetProperty("attached_pic", out var ap) &&
                            ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1)
                            continue;
                        hasVideo = true;
                        w = s.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 0;
                        h = s.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 0;
                        vCodec = s.TryGetProperty("codec_name", out var vc) ? vc.GetString() : null;
                        fps = ParseRate(s, "avg_frame_rate");
                        if (fps <= 0) fps = ParseRate(s, "r_frame_rate");
                        if (duration <= 0) duration = GetDouble(s, "duration");
                    }
                    else if (type == "audio" && !hasAudio)
                    {
                        hasAudio = true;
                        aCodec = s.TryGetProperty("codec_name", out var ac) ? ac.GetString() : null;
                        audioBitrate = (long)GetDouble(s, "bit_rate");
                        sampleRate = (int)GetDouble(s, "sample_rate");
                        channels = s.TryGetProperty("channels", out var ch) &&
                                   ch.ValueKind == JsonValueKind.Number ? ch.GetInt32() : 0;
                        if (duration <= 0) duration = GetDouble(s, "duration");
                    }
                }
            }

            var info = new MediaInfo(duration, w, h, fps, bitrate, audioBitrate,
                hasVideo, hasAudio, vCodec, aCodec, sampleRate, channels);
            Cache[path] = info;
            return info;
        }
        catch
        {
            Cache[path] = null;
            return null;
        }
    }

    private static double GetDouble(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var d) => d,
            _ => 0
        };
    }

    private static double ParseRate(JsonElement stream, string prop)
    {
        if (!stream.TryGetProperty(prop, out var v)) return 0;
        var s = v.GetString();
        if (string.IsNullOrEmpty(s)) return 0;
        var parts = s.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var den) &&
            den != 0)
            return num / den;
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0;
    }

    public static string Summary(MediaInfo i)
    {
        var parts = new List<string>();
        if (i.HasVideo && i.Width > 0) parts.Add($"{i.Width}×{i.Height}");
        if (i.HasVideo && i.Fps > 0) parts.Add($"{i.Fps:0.##}fps");
        if (!i.HasVideo && i.HasAudio)
        {
            if (i.SampleRate > 0) parts.Add($"{i.SampleRate / 1000.0:0.#}kHz");
            if (i.AudioBitrateBps > 0) parts.Add($"{i.AudioBitrateBps / 1000}kbps");
        }
        if (i.Duration > 0) parts.Add(Formatters.Duration(i.Duration));
        return string.Join(" · ", parts);
    }
}
