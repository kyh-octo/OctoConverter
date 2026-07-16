using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OctoConverter.Services;

/// <summary>
/// Windows 내장 WIC 코덱 기반 이미지 로드/리사이즈/인코딩.
/// 모든 결과는 Freeze되어 백그라운드 스레드에서 안전하게 쓸 수 있다.
/// </summary>
public static class ImageCodec
{
    public static BitmapFrame Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>WIC가 못 읽는 형식(WebP 등)은 FFmpeg으로 PNG 변환 후 로드한다.</summary>
    public static async Task<BitmapSource> LoadAsync(string path, CancellationToken ct)
    {
        try
        {
            return Load(path);
        }
        catch (Exception) when (FFmpegService.IsAvailable)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "octoimg_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                await FFmpegService.RunAsync(
                    $"-i {FFmpegService.Quote(path)} -frames:v 1 {FFmpegService.Quote(tmp)}", 0, null, ct);
                return Load(tmp);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    public static (int Width, int Height) GetPixelSize(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>절반 이하로 줄일 때는 단계적으로 축소해 품질을 유지한다.</summary>
    public static BitmapSource Resize(BitmapSource src, int targetWidth, int targetHeight)
    {
        if (targetWidth <= 0 || targetHeight <= 0 ||
            (src.PixelWidth == targetWidth && src.PixelHeight == targetHeight))
            return src;

        var current = src;
        while (current.PixelWidth / 2 > targetWidth && current.PixelHeight / 2 > targetHeight)
            current = Scale(current, current.PixelWidth / 2, current.PixelHeight / 2);
        return Scale(current, targetWidth, targetHeight);
    }

    private static BitmapSource Scale(BitmapSource src, int w, int h)
    {
        var scaled = new TransformedBitmap(src,
            new ScaleTransform((double)w / src.PixelWidth, (double)h / src.PixelHeight));
        scaled.Freeze();
        return scaled;
    }

    public static byte[] GetBgra(BitmapSource src, out int w, out int h)
    {
        BitmapSource converted = src.Format == PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        w = converted.PixelWidth;
        h = converted.PixelHeight;
        var pixels = new byte[w * h * 4];
        converted.CopyPixels(pixels, w * 4, 0);
        return pixels;
    }

    public static BitmapSource FromBgra(byte[] pixels, int w, int h)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>투명 배경을 흰색으로 합성(JPEG/BMP처럼 알파가 없는 형식용).</summary>
    public static BitmapSource FlattenWhite(BitmapSource src)
    {
        var px = GetBgra(src, out var w, out var h);
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a == 255) continue;
            px[i] = (byte)((px[i] * a + 255 * (255 - a)) / 255);
            px[i + 1] = (byte)((px[i + 1] * a + 255 * (255 - a)) / 255);
            px[i + 2] = (byte)((px[i + 2] * a + 255 * (255 - a)) / 255);
            px[i + 3] = 255;
        }
        return FromBgra(px, w, h);
    }

    /// <summary>비율을 유지한 채 size×size 정사각형에 맞추고 남는 부분은 투명 처리(아이콘용).</summary>
    public static BitmapSource FitSquare(BitmapSource src, int size)
    {
        double scale = Math.Min((double)size / src.PixelWidth, (double)size / src.PixelHeight);
        int w = Math.Max(1, (int)Math.Round(src.PixelWidth * scale));
        int h = Math.Max(1, (int)Math.Round(src.PixelHeight * scale));
        var resized = Resize(src, w, h);
        var px = GetBgra(resized, out w, out h);

        var canvas = new byte[size * size * 4];
        int offX = (size - w) / 2, offY = (size - h) / 2;
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(px, y * w * 4, canvas, ((y + offY) * size + offX) * 4, w * 4);
        return FromBgra(canvas, size, size);
    }

    public static byte[] Encode(BitmapSource src, string ext, int quality)
    {
        if (ext is ".jpg" or ".jpeg" or ".bmp")
            src = FlattenWhite(src);

        BitmapEncoder encoder = ext switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) },
            ".png" => new PngBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            _ => throw new NotSupportedException("지원하지 않는 형식: " + ext)
        };
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>목표 용량 이하가 되도록 품질을 이진 탐색으로 자동 조절(JPEG용).</summary>
    public static byte[] EncodeToTarget(BitmapSource src, string ext, long targetBytes, out int usedQuality)
    {
        int lo = 5, hi = 100;
        var best = Encode(src, ext, lo);
        usedQuality = lo;
        if (best.Length > targetBytes) return best; // 최저 품질로도 초과하면 그대로 반환

        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var data = Encode(src, ext, mid);
            if (data.Length <= targetBytes)
            {
                best = data;
                usedQuality = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return best;
    }
}
