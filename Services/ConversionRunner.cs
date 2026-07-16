using System.IO;
using OctoConverter.Models;

namespace OctoConverter.Services;

/// <summary>여러 파일을 병렬로 변환하며 파일별 상태·결과를 갱신하는 공용 실행기.</summary>
public static class ConversionRunner
{
    public static async Task<(int Ok, int Fail, int Cancelled)> RunAsync(
        IReadOnlyList<FileItem> items, int parallelism,
        Func<FileItem, CancellationToken, Task> worker, CancellationToken ct)
    {
        foreach (var it in items)
        {
            it.Status = "대기";
            it.Progress = 0;
            it.ResultText = "";
            it.OutputPath = null;
        }

        int ok = 0, fail = 0, cancelled = 0;
        using var sem = new SemaphoreSlim(Math.Max(1, parallelism));

        var tasks = items.Select(async item =>
        {
            bool acquired = false;
            try
            {
                await sem.WaitAsync(ct);
                acquired = true;
                item.Status = "변환 중";
                await worker(item, ct);
                item.Progress = 100;
                item.Status = "완료";
                if (item.OutputPath is not null && File.Exists(item.OutputPath))
                {
                    long outSize = new FileInfo(item.OutputPath).Length;
                    string delta = item.SizeBytes > 0
                        ? $" ({(outSize - item.SizeBytes) * 100.0 / item.SizeBytes:+0;-0}%)"
                        : "";
                    item.ResultText = Formatters.Bytes(outSize) + delta;
                }
                Interlocked.Increment(ref ok);
            }
            catch (OperationCanceledException)
            {
                item.Status = "취소됨";
                Interlocked.Increment(ref cancelled);
                CleanupPartialOutput(item);
            }
            catch (Exception ex)
            {
                item.Status = "오류";
                item.ResultText = ex.Message;
                Interlocked.Increment(ref fail);
                CleanupPartialOutput(item);
            }
            finally
            {
                if (acquired) sem.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        return (ok, fail, cancelled);
    }

    private static void CleanupPartialOutput(FileItem item)
    {
        if (item.OutputPath is null) return;
        try { if (File.Exists(item.OutputPath)) File.Delete(item.OutputPath); } catch { }
    }

    /// <summary>출력 경로 결정. 원본과 겹치거나 이미 존재하면 번호를 붙인다.</summary>
    public static string GetOutputPath(string inputPath, string? outputFolder, string newExtWithDot)
    {
        var dir = outputFolder ?? Path.GetDirectoryName(inputPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var candidate = Path.Combine(dir, name + newExtWithDot);

        if (string.Equals(candidate, inputPath, StringComparison.OrdinalIgnoreCase))
            candidate = Path.Combine(dir, name + "_변환" + newExtWithDot);

        int n = 1;
        var baseName = Path.GetFileNameWithoutExtension(candidate);
        while (File.Exists(candidate))
            candidate = Path.Combine(dir, $"{baseName} ({n++}){newExtWithDot}");
        return candidate;
    }
}
