using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OctoConverter.Services;

/// <summary>
/// Windows 내장 WIC 코덱 기반 이미지 로드/리사이즈/인코딩.
/// 읽어들인 이미지는 디코더에서 분리한 Bgra32 사본이라 어느 스레드에서나 안전하게 쓸 수 있다.
/// </summary>
public static class ImageCodec
{
    /// <summary>
    /// Windows의 WebP·HEIF 코덱은 알파 채널을 버린 채 디코드에 "성공"하는 경우가 있다.
    /// 이 형식들은 네이티브 픽셀 형식 그대로 읽고, 그래도 투명도가 없으면 FFmpeg으로 다시 읽는다.
    /// </summary>
    private static readonly string[] AlphaRiskExtensions = [".webp", ".avif", ".heic", ".heif"];

    /// <summary>
    /// 이미지를 읽어 Bgra32로 정규화한다.
    /// 디코더가 돌려주는 프레임은 Freeze해도 만든 스레드에 묶여 있어 다른 스레드에서 쓰면 예외가 나므로,
    /// 픽셀을 복사해 어느 스레드에서나 안전한 비트맵으로 만들어 돌려준다.
    /// </summary>
    public static BitmapSource Load(string path)
    {
        // WebP·HEIF는 네이티브 픽셀 형식을 유지해야 알파 채널이 살아남는다.
        // (기본 옵션으로 읽으면 Windows 코덱이 알파를 버린 Bgr32로 넘겨준다)
        var options = AlphaRiskExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            ? BitmapCreateOptions.PreservePixelFormat
            : BitmapCreateOptions.None;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(fs, options, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var pixels = GetBgra(frame, out int w, out int h);
        return FromBgra(pixels, w, h, frame.DpiX, frame.DpiY);
    }

    /// <summary>
    /// 알파 채널까지 보존해서 이미지를 읽는다.
    /// WIC가 아예 못 읽는 형식(TGA 등)이나, 읽어도 투명도가 남아 있지 않은 경우에는
    /// FFmpeg으로 다시 읽어 복구한다.
    /// </summary>
    public static async Task<BitmapSource> LoadAsync(string path, CancellationToken ct)
    {
        BitmapSource? decoded = null;
        try
        {
            decoded = Load(path);
            if (!AlphaLikelyDropped(path, decoded)) return decoded;
        }
        catch when (FFmpegService.IsAvailable)
        {
            // 아래 FFmpeg 경로로 진행
        }

        // FFmpeg이 없으면 투명도는 잃더라도 읽어낸 그림을 그대로 쓴다
        if (!FFmpegService.IsAvailable) return decoded!;

        var tmp = Path.Combine(Path.GetTempPath(), "octoimg_" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await FFmpegService.RunAsync(
                $"-i {FFmpegService.Quote(path)} -frames:v 1 -pix_fmt rgba {FFmpegService.Quote(tmp)}",
                0, null, ct);
            return Load(tmp);
        }
        catch (Exception ex) when (decoded is not null && ex is not OperationCanceledException)
        {
            return decoded; // FFmpeg마저 실패하면 WIC 결과라도 쓴다
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// 투명해야 할 파일인데 결과가 전부 불투명하면 코덱이 알파를 버린 것이다.
    /// 이때만 FFmpeg으로 다시 읽는다.
    /// </summary>
    private static bool AlphaLikelyDropped(string path, BitmapSource decoded)
    {
        var ext = Path.GetExtension(path);
        if (!AlphaRiskExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return false;

        // WebP는 헤더만 보고 알파 유무를 확정할 수 있어, 불필요한 검사를 피한다
        if (ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) && WebpHasAlpha(path) == false)
            return false;

        return IsFullyOpaque(decoded);
    }

    private static bool IsFullyOpaque(BitmapSource img)
    {
        var px = GetBgra(img, out _, out _);
        for (int i = 3; i < px.Length; i += 4)
            if (px[i] != 255) return false;
        return true;
    }

    /// <summary>WebP 헤더로 알파 채널 유무를 판정한다. 확신할 수 없으면 null.</summary>
    private static bool? WebpHasAlpha(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[25];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return null;
            if (!head[..4].SequenceEqual("RIFF"u8) || !head[8..12].SequenceEqual("WEBP"u8)) return null;

            var fourcc = head[12..16];
            if (fourcc.SequenceEqual("VP8X"u8))
                return (head[20] & 0x10) != 0;            // 확장 헤더의 ALPHA 플래그
            if (fourcc.SequenceEqual("VP8L"u8))
            {
                if (head[20] != 0x2F) return null;        // 무손실 시그니처
                uint bits = (uint)(head[21] | head[22] << 8 | head[23] << 16 | head[24] << 24);
                return ((bits >> 28) & 1) != 0;           // alpha_is_used 비트
            }
            if (fourcc.SequenceEqual("VP8 "u8))
                return false;                             // 단순 손실 WebP은 알파가 없다
            return null;
        }
        catch
        {
            return null;
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

    private static BitmapSource ConvertTo(BitmapSource src, PixelFormat format)
    {
        if (src.Format == format) return src;
        var converted = new FormatConvertedBitmap(src, format, null, 0);
        converted.Freeze();
        return converted;
    }

    public static byte[] GetBgra(BitmapSource src, out int w, out int h)
    {
        var converted = ConvertTo(src, PixelFormats.Bgra32);
        w = converted.PixelWidth;
        h = converted.PixelHeight;
        var pixels = new byte[w * h * 4];
        converted.CopyPixels(pixels, w * 4, 0);
        return pixels;
    }

    public static BitmapSource FromBgra(byte[] pixels, int w, int h, double dpiX = 96, double dpiY = 96)
    {
        var bmp = BitmapSource.Create(w, h, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, w * 4);
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
