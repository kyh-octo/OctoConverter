using System.IO;
using System.Windows.Media.Imaging;

namespace OctoConverter.Services;

/// <summary>
/// 다중 크기 ICO 파일 작성기.
/// 128px 이상은 PNG 압축 엔트리, 그 미만은 호환성이 좋은 32bpp BMP(DIB) 엔트리로 저장한다.
/// </summary>
public static class IcoWriter
{
    public static void Write(string path, IReadOnlyList<(int Size, System.Windows.Media.Imaging.BitmapSource Image)> images)
    {
        var blobs = new List<byte[]>(images.Count);
        foreach (var (size, img) in images)
            blobs.Add(size >= 128 ? EncodePng(img) : EncodeDib(img));

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        // ICONDIR
        bw.Write((ushort)0);              // reserved
        bw.Write((ushort)1);              // type: icon
        bw.Write((ushort)images.Count);

        int offset = 6 + 16 * images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            int size = images[i].Size;
            bw.Write((byte)(size >= 256 ? 0 : size)); // 0 = 256px
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)0);            // palette colors
            bw.Write((byte)0);            // reserved
            bw.Write((ushort)1);          // planes
            bw.Write((ushort)32);         // bpp
            bw.Write(blobs[i].Length);
            bw.Write(offset);
            offset += blobs[i].Length;
        }
        foreach (var blob in blobs) bw.Write(blob);
    }

    private static byte[] EncodePng(System.Windows.Media.Imaging.BitmapSource img)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] EncodeDib(System.Windows.Media.Imaging.BitmapSource img)
    {
        var px = ImageCodec.GetBgra(img, out int w, out int h);
        int xorSize = w * h * 4;
        int andStride = ((w + 31) / 32) * 4;
        int andSize = andStride * h;

        using var ms = new MemoryStream(40 + xorSize + andSize);
        using var bw = new BinaryWriter(ms);

        // BITMAPINFOHEADER (높이는 XOR+AND 두 배로 기록하는 것이 ICO 규격)
        bw.Write(40);
        bw.Write(w);
        bw.Write(h * 2);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(0);          // BI_RGB
        bw.Write(xorSize);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

        // XOR(색상) 비트맵: 아래에서 위로
        for (int y = h - 1; y >= 0; y--)
            bw.Write(px, y * w * 4, w * 4);

        // AND(투명) 마스크: 알파 채널을 쓰므로 전부 0
        bw.Write(new byte[andSize]);
        return ms.ToArray();
    }
}
