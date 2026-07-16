using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OctoConverter.Models;
using OctoConverter.Services;

namespace OctoConverter.Controls;

/// <summary>드래그&드롭과 파일 대화상자를 지원하는 공용 변환 목록.</summary>
public partial class FileListControl : UserControl
{
    public ObservableCollection<FileItem> Files { get; } = new();
    public HashSet<string> AcceptedExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string DialogFilter { get; set; } = "모든 파일|*.*";

    /// <summary>새 파일이 추가되면 발생(탭에서 정보 표시·예상 용량 갱신에 사용).</summary>
    public event Action<IReadOnlyList<FileItem>>? FilesAdded;
    public event EventHandler? ListChanged;

    public FileListControl()
    {
        InitializeComponent();
        List.ItemsSource = Files;
        Files.CollectionChanged += (_, _) => Update();
        Update();
    }

    private void Update()
    {
        EmptyHint.Visibility = Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = Files.Count == 0
            ? "파일을 끌어다 놓거나 [파일 추가]를 누르세요"
            : $"{Files.Count}개 · 총 {Formatters.Bytes(Files.Sum(f => (double)f.SizeBytes))}";
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = DialogFilter };
        if (dlg.ShowDialog() == true) AddPaths(dlg.FileNames);
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in List.SelectedItems.Cast<FileItem>().ToList())
            Files.Remove(item);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Files.Clear();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        var added = new List<FileItem>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                // 폴더를 끌어오면 내부 파일 중 지원 형식만 추가
                AddCandidates(Directory.EnumerateFiles(path), added);
            }
            else
            {
                AddCandidates([path], added);
            }
        }
        if (added.Count > 0) FilesAdded?.Invoke(added);
    }

    private void AddCandidates(IEnumerable<string> paths, List<FileItem> added)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            var ext = Path.GetExtension(path);
            if (AcceptedExtensions.Count > 0 && !AcceptedExtensions.Contains(ext)) continue;
            if (Files.Any(f => string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase))) continue;

            var item = new FileItem(path);
            Files.Add(item);
            added.Add(item);
        }
    }
}
