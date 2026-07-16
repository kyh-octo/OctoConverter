using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using OctoConverter.Services;

namespace OctoConverter.Models;

/// <summary>변환 목록의 파일 한 개. 상태/진행률은 바인딩으로 UI에 반영된다.</summary>
public class FileItem : INotifyPropertyChanged
{
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public long SizeBytes { get; }
    public string SizeText => Formatters.Bytes(SizeBytes);

    private string _info = "";
    public string Info { get => _info; set => Set(ref _info, value); }

    private string _status = "대기";
    public string Status { get => _status; set => Set(ref _status, value); }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    private string _resultText = "";
    public string ResultText { get => _resultText; set => Set(ref _resultText, value); }

    public string? OutputPath { get; set; }

    public FileItem(string path)
    {
        FilePath = path;
        try { SizeBytes = new FileInfo(path).Length; } catch { SizeBytes = 0; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
