using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace OctoConverter.Services;

public enum DocKind { Word, Excel, PowerPoint, Image, Unknown }

/// <summary>
/// 문서 → PDF 변환기.
/// Microsoft Office(COM 자동화)를 우선 사용하고, 없으면 LibreOffice(headless)로 폴백한다.
/// </summary>
public static class DocumentConverter
{
    public static readonly string[] WordExtensions = [".doc", ".docx", ".docm", ".rtf", ".odt", ".txt"];
    public static readonly string[] ExcelExtensions = [".xls", ".xlsx", ".xlsm", ".csv", ".ods"];
    public static readonly string[] PowerPointExtensions = [".ppt", ".pptx", ".pptm", ".odp"];
    public static readonly string[] ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".jfif",
        ".avif", ".heic", ".heif", ".tga", ".dds", ".jxr", ".wdp", ".jp2"
    ];

    public static DocKind KindOf(string ext)
    {
        if (WordExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return DocKind.Word;
        if (ExcelExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return DocKind.Excel;
        if (PowerPointExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return DocKind.PowerPoint;
        if (ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return DocKind.Image;
        return DocKind.Unknown;
    }

    public static string KindName(DocKind kind) => kind switch
    {
        DocKind.Word => "Word 문서",
        DocKind.Excel => "Excel 문서",
        DocKind.PowerPoint => "PowerPoint 문서",
        DocKind.Image => "이미지",
        _ => "알 수 없음"
    };

    // ===== 엔진 탐지 (ProgID 레지스트리 조회 결과는 캐시) =====

    private static bool? _word, _excel, _ppt;
    public static bool WordAvailable => _word ??= Type.GetTypeFromProgID("Word.Application") is not null;
    public static bool ExcelAvailable => _excel ??= Type.GetTypeFromProgID("Excel.Application") is not null;
    public static bool PowerPointAvailable => _ppt ??= Type.GetTypeFromProgID("PowerPoint.Application") is not null;

    private static string? _soffice;
    private static bool _sofficeSearched;

    public static string? LibreOfficePath
    {
        get
        {
            if (_sofficeSearched) return _soffice;
            _sofficeSearched = true;
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "LibreOffice", "program", "soffice.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "LibreOffice", "program", "soffice.exe"),
            };
            _soffice = candidates.FirstOrDefault(File.Exists);
            return _soffice;
        }
    }

    public static bool CanConvert(DocKind kind) => kind switch
    {
        DocKind.Image => true,
        DocKind.Word => WordAvailable || LibreOfficePath is not null,
        DocKind.Excel => ExcelAvailable || LibreOfficePath is not null,
        DocKind.PowerPoint => PowerPointAvailable || LibreOfficePath is not null,
        _ => false
    };

    /// <summary>사용 가능한 엔진 요약(상태 표시용).</summary>
    public static string EngineSummary()
    {
        var office = new List<string>();
        if (WordAvailable) office.Add("Word");
        if (ExcelAvailable) office.Add("Excel");
        if (PowerPointAvailable) office.Add("PowerPoint");

        if (office.Count > 0 && LibreOfficePath is not null)
            return $"변환 엔진: Microsoft Office ({string.Join("·", office)}) + LibreOffice";
        if (office.Count > 0)
            return $"변환 엔진: Microsoft Office ({string.Join("·", office)})";
        if (LibreOfficePath is not null)
            return "변환 엔진: LibreOffice";
        return "⚠ Microsoft Office 또는 LibreOffice가 없어 문서 변환은 사용할 수 없습니다. (이미지 → PDF는 가능)";
    }

    // ===== 변환 =====

    public static Task ConvertOfficeAsync(DocKind kind, string input, string output, CancellationToken ct)
    {
        bool officeAvailable = kind switch
        {
            DocKind.Word => WordAvailable,
            DocKind.Excel => ExcelAvailable,
            DocKind.PowerPoint => PowerPointAvailable,
            _ => false
        };
        if (officeAvailable)
            return Task.Run(() => ConvertWithOffice(kind, input, output), ct);
        if (LibreOfficePath is not null)
            return ConvertWithLibreOfficeAsync(input, output, ct);
        throw new InvalidOperationException(
            $"{KindName(kind)} 변환에는 Microsoft Office 또는 LibreOffice가 필요합니다.");
    }

    private static void ConvertWithOffice(DocKind kind, string input, string output)
    {
        switch (kind)
        {
            case DocKind.Word: WordToPdf(input, output); break;
            case DocKind.Excel: ExcelToPdf(input, output); break;
            case DocKind.PowerPoint: PowerPointToPdf(input, output); break;
            default: throw new NotSupportedException();
        }
    }

    private static void WordToPdf(string input, string output)
    {
        var type = Type.GetTypeFromProgID("Word.Application")!;
        dynamic? app = null, doc = null;
        try
        {
            app = Activator.CreateInstance(type)!;
            app!.Visible = false;
            app.DisplayAlerts = 0; // wdAlertsNone
            doc = app.Documents.Open(input, ReadOnly: true, AddToRecentFiles: false, Visible: false);
            doc.ExportAsFixedFormat(output, 17); // wdExportFormatPDF
        }
        finally
        {
            try { doc?.Close(false); } catch { }
            try { app?.Quit(); } catch { }
            Release(doc);
            Release(app);
        }
    }

    private static void ExcelToPdf(string input, string output)
    {
        var type = Type.GetTypeFromProgID("Excel.Application")!;
        dynamic? app = null, wb = null;
        try
        {
            app = Activator.CreateInstance(type)!;
            app!.Visible = false;
            app.DisplayAlerts = false;
            wb = app.Workbooks.Open(input, ReadOnly: true);
            wb.ExportAsFixedFormat(0, output); // xlTypePDF
        }
        finally
        {
            try { wb?.Close(false); } catch { }
            try { app?.Quit(); } catch { }
            Release(wb);
            Release(app);
        }
    }

    private static void PowerPointToPdf(string input, string output)
    {
        var type = Type.GetTypeFromProgID("PowerPoint.Application")!;
        dynamic? app = null, pres = null;
        try
        {
            app = Activator.CreateInstance(type)!;
            app!.DisplayAlerts = 1; // ppAlertsNone
            // PowerPoint는 창 없이 열려면 WithWindow=msoFalse(0), ReadOnly=msoTrue(-1)
            pres = app.Presentations.Open(input, -1, 0, 0);
            pres.SaveAs(output, 32); // ppSaveAsPDF
        }
        finally
        {
            try { pres?.Close(); } catch { }
            try { app?.Quit(); } catch { }
            Release(pres);
            Release(app);
        }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
            try { Marshal.FinalReleaseComObject(com); } catch { }
    }

    private static async Task ConvertWithLibreOfficeAsync(string input, string output, CancellationToken ct)
    {
        // LibreOffice는 출력 파일명을 지정할 수 없어 임시 폴더에 만든 뒤 옮긴다
        var tmpDir = Path.Combine(Path.GetTempPath(), "octodoc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = LibreOfficePath!,
                Arguments = "--headless --norestore --convert-to pdf " +
                            $"--outdir {FFmpegService.Quote(tmpDir)} {FFmpegService.Quote(input)}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("LibreOffice 실행에 실패했습니다.");
            using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
            await proc.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();

            var produced = Path.Combine(tmpDir, Path.GetFileNameWithoutExtension(input) + ".pdf");
            if (!File.Exists(produced))
                throw new InvalidOperationException("LibreOffice가 PDF를 생성하지 못했습니다.");
            File.Move(produced, output, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }
}
