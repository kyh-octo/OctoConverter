using System.IO;
using System.Text.Json;

namespace OctoConverter.Services;

/// <summary>앱 설정. %AppData%\OctoConverter\settings.json 에 저장된다.</summary>
public class AppSettings
{
    /// <summary>시작할 때 GitHub 최신 릴리스를 확인해 새 버전이 있으면 설치할지 묻는다.</summary>
    public bool CheckForUpdates { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OctoConverter");

    public static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // 손상된 설정 파일이면 기본값으로 진행
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // 저장 실패는 치명적이지 않음
        }
    }
}
