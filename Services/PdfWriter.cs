using System.Globalization;
using System.IO;
using System.Text;

namespace OctoConverter.Services;

public enum PdfPageMode
{
    ImageSize,   // 페이지 크기 = 이미지 크기 (96dpi 기준)
    A4Portrait,
    A4Landscape,
}

/// <summary>
/// 외부 의존성 없는 최소 구현 PDF 작성기.
/// JPEG(DCTDecode)을 페이지당 한 장씩 담는다. 이미지 → PDF 용도로 충분한 부분집합만 구현.
/// </summary>
public static class PdfWriter
{
    public static void WriteImagesPdf(string path,
        IReadOnlyList<(byte[] Jpeg, int Width, int Height)> images, PdfPageMode mode)
    {
        using var fs = File.Create(path);
        Write(fs, images, mode);
    }

    public static void Write(Stream stream,
        IReadOnlyList<(byte[] Jpeg, int Width, int Height)> images, PdfPageMode mode)
    {
        if (images.Count == 0)
            throw new ArgumentException("PDF에 담을 이미지가 없습니다.");

        var inv = CultureInfo.InvariantCulture;
        var offsets = new List<long>();
        void Text(string s) => stream.Write(Encoding.ASCII.GetBytes(s));
        string N(double v) => v.ToString("0.##", inv);

        int n = images.Count;
        // 객체 번호: 1=Catalog, 2=Pages, 이후 이미지 i마다 Page=3+3i, Contents=4+3i, XObject=5+3i
        Text("%PDF-1.4\n");

        offsets.Add(stream.Position);
        Text("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets.Add(stream.Position);
        var kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{3 + 3 * i} 0 R"));
        Text($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {n} >>\nendobj\n");

        for (int i = 0; i < n; i++)
        {
            var (jpeg, iw, ih) = images[i];
            double imgPtW = iw * 72.0 / 96.0, imgPtH = ih * 72.0 / 96.0;
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

            int pageObj = 3 + 3 * i, contObj = 4 + 3 * i, imgObj = 5 + 3 * i;

            offsets.Add(stream.Position);
            Text($"{pageObj} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(pageW)} {N(pageH)}] " +
                 $"/Contents {contObj} 0 R /Resources << /XObject << /Im{i} {imgObj} 0 R >> >> >>\nendobj\n");

            var content = Encoding.ASCII.GetBytes(
                $"q\n{N(drawW)} 0 0 {N(drawH)} {N(x)} {N(y)} cm\n/Im{i} Do\nQ\n");
            offsets.Add(stream.Position);
            Text($"{contObj} 0 obj\n<< /Length {content.Length} >>\nstream\n");
            stream.Write(content);
            Text("endstream\nendobj\n");

            offsets.Add(stream.Position);
            Text($"{imgObj} 0 obj\n<< /Type /XObject /Subtype /Image /Width {iw} /Height {ih} " +
                 $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            stream.Write(jpeg);
            Text("\nendstream\nendobj\n");
        }

        long xrefPos = stream.Position;
        int total = 2 + 3 * n;
        Text($"xref\n0 {total + 1}\n");
        Text("0000000000 65535 f \n");
        foreach (var off in offsets)
            Text(off.ToString("D10", inv) + " 00000 n \n");
        Text($"trailer\n<< /Size {total + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF");
    }
}
