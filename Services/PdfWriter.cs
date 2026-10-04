using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace OctoConverter.Services;

public enum PdfPageMode
{
    ImageSize,   // 페이지 크기 = 이미지 크기 (96dpi 기준)
    A4Portrait,
    A4Landscape,
}

/// <summary>
/// PDF에 넣을 이미지 한 장.
/// 불투명한 그림은 JPEG(DCTDecode)로, 투명도가 있는 그림은 무손실 압축(FlateDecode)에
/// 알파 채널을 소프트 마스크로 붙여 담는다.
/// </summary>
public sealed record PdfImage(byte[] Data, string Filter, int Width, int Height, byte[]? SoftMask)
{
    public static PdfImage Opaque(byte[] jpeg, int width, int height) =>
        new(jpeg, "DCTDecode", width, height, null);

    /// <summary>
    /// 투명도가 있는 이미지. 알파는 언제나 무손실 소프트 마스크로 붙이고,
    /// 색은 무손실 압축과 JPEG 중 작은 쪽을 고른다.
    /// 로고·도형은 무손실이 JPEG보다 몇 배 작고 가장자리도 깨끗하지만,
    /// 사진은 반대로 무손실이 몇 배 커진다.
    /// </summary>
    public static PdfImage WithAlpha(byte[] bgra, int width, int height, int jpegQuality)
    {
        var rgb = new byte[width * height * 3];
        var alpha = new byte[width * height];
        for (int i = 0, p = 0, a = 0; i < bgra.Length; i += 4, p += 3, a++)
        {
            byte opacity = bgra[i + 3];
            alpha[a] = opacity;
            if (opacity == 0)
            {
                // 완전히 투명한 픽셀의 색은 어차피 보이지 않는다.
                // 흰색으로 통일하면 압축률이 크게 오르고, 소프트 마스크를 무시하는
                // 옛 뷰어에서도 검게 뭉치지 않는다.
                rgb[p] = rgb[p + 1] = rgb[p + 2] = 255;
            }
            else
            {
                rgb[p] = bgra[i + 2];      // R
                rgb[p + 1] = bgra[i + 1];  // G
                rgb[p + 2] = bgra[i];      // B
            }
        }

        var rgbImage = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Rgb24, null, rgb, width * 3);
        rgbImage.Freeze();
        var jpeg = ImageCodec.Encode(rgbImage, ".jpg", jpegQuality);

        // 압축 결과를 직접 비교한다. 낮은 압축 수준으로 미리 넘겨짚으면
        // 넓은 단색 영역이 있는 그림에서 무손실 쪽 크기를 크게 과대평가한다.
        var lossless = Deflate(rgb, CompressionLevel.SmallestSize);
        bool useLossless = lossless.Length < jpeg.Length;

        return new PdfImage(
            useLossless ? lossless : jpeg,
            useLossless ? "FlateDecode" : "DCTDecode",
            width, height,
            Deflate(alpha, CompressionLevel.SmallestSize));
    }

    public static bool HasTransparency(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 255) return true;
        return false;
    }

    private static byte[] Deflate(byte[] data, CompressionLevel level)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, level, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }
}

/// <summary>
/// 외부 의존성 없는 최소 구현 PDF 작성기.
/// 페이지당 이미지 한 장을 담는다. 이미지 → PDF 용도로 충분한 부분집합만 구현.
/// </summary>
public static class PdfWriter
{
    public static void WriteImagesPdf(string path, IReadOnlyList<PdfImage> images, PdfPageMode mode)
    {
        using var fs = File.Create(path);
        Write(fs, images, mode);
    }

    public static void Write(Stream stream, IReadOnlyList<PdfImage> images, PdfPageMode mode)
    {
        if (images.Count == 0)
            throw new ArgumentException("PDF에 담을 이미지가 없습니다.");

        var inv = CultureInfo.InvariantCulture;
        var offsets = new List<long>();
        void Text(string s) => stream.Write(Encoding.ASCII.GetBytes(s));
        string N(double v) => v.ToString("0.##", inv);

        int n = images.Count;

        // 객체 번호를 미리 배정한다. 1=Catalog, 2=Pages,
        // 이후 이미지마다 Page / Contents / XObject (+ 투명도가 있으면 SMask)
        var pageObj = new int[n];
        var contObj = new int[n];
        var imgObj = new int[n];
        var maskObj = new int[n];
        int next = 3;
        for (int i = 0; i < n; i++)
        {
            pageObj[i] = next++;
            contObj[i] = next++;
            imgObj[i] = next++;
            maskObj[i] = images[i].SoftMask is null ? 0 : next++;
        }
        int total = next - 1;

        // 소프트 마스크는 PDF 1.4부터
        Text("%PDF-1.4\n");

        offsets.Add(stream.Position);
        Text("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets.Add(stream.Position);
        var kids = string.Join(" ", pageObj.Select(o => $"{o} 0 R"));
        Text($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {n} >>\nendobj\n");

        for (int i = 0; i < n; i++)
        {
            var img = images[i];
            double imgPtW = img.Width * 72.0 / 96.0, imgPtH = img.Height * 72.0 / 96.0;
            double pageW, pageH, drawW, drawH, x, y;

            if (mode == PdfPageMode.ImageSize)
            {
                pageW = drawW = imgPtW;
                pageH = drawH = imgPtH;
                x = y = 0;
            }
            else
            {
                (pageW, pageH) = mode == PdfPageMode.A4Portrait
                    ? (595.28, 841.89)
                    : (841.89, 595.28);
                const double margin = 28.35; // 1cm
                double scale = Math.Min((pageW - margin * 2) / imgPtW, (pageH - margin * 2) / imgPtH);
                drawW = imgPtW * scale;
                drawH = imgPtH * scale;
                x = (pageW - drawW) / 2;
                y = (pageH - drawH) / 2;
            }

            offsets.Add(stream.Position);
            Text($"{pageObj[i]} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(pageW)} {N(pageH)}] " +
                 $"/Contents {contObj[i]} 0 R /Resources << /XObject << /Im{i} {imgObj[i]} 0 R >> >> >>\nendobj\n");

            var content = Encoding.ASCII.GetBytes(
                $"q\n{N(drawW)} 0 0 {N(drawH)} {N(x)} {N(y)} cm\n/Im{i} Do\nQ\n");
            offsets.Add(stream.Position);
            Text($"{contObj[i]} 0 obj\n<< /Length {content.Length} >>\nstream\n");
            stream.Write(content);
            Text("endstream\nendobj\n");

            offsets.Add(stream.Position);
            string smask = maskObj[i] == 0 ? "" : $"/SMask {maskObj[i]} 0 R ";
            Text($"{imgObj[i]} 0 obj\n<< /Type /XObject /Subtype /Image /Width {img.Width} /Height {img.Height} " +
                 $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /{img.Filter} {smask}" +
                 $"/Length {img.Data.Length} >>\nstream\n");
            stream.Write(img.Data);
            Text("\nendstream\nendobj\n");

            if (img.SoftMask is { } mask)
            {
                offsets.Add(stream.Position);
                Text($"{maskObj[i]} 0 obj\n<< /Type /XObject /Subtype /Image /Width {img.Width} /Height {img.Height} " +
                     $"/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode " +
                     $"/Length {mask.Length} >>\nstream\n");
                stream.Write(mask);
                Text("\nendstream\nendobj\n");
            }
        }

        long xrefPos = stream.Position;
        Text($"xref\n0 {total + 1}\n");
        Text("0000000000 65535 f \n");
        foreach (var off in offsets)
            Text(off.ToString("D10", inv) + " 00000 n \n");
        Text($"trailer\n<< /Size {total + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF");
    }
}
