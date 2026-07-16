using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace OctoConverter.Controls;

public partial class OutputLocationControl : UserControl
{
    public OutputLocationControl()
    {
        InitializeComponent();
    }

    /// <summary>null이면 "원본과 같은 폴더"를 뜻한다. 잘못된 입력이면 예외.</summary>
    public string? GetOutputFolder()
    {
        if (SameFolder.IsChecked == true) return null;

        var dir = FolderBox.Text.Trim();
        if (dir.Length == 0)
            throw new InvalidOperationException("저장 폴더를 선택하세요.");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true)
        {
            FolderBox.Text = dlg.FolderName;
            CustomFolder.IsChecked = true;
        }
    }
}
