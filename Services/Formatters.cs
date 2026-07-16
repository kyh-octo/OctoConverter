using System.Globalization;

namespace OctoConverter.Services;

public static class Formatters
{
    public static string Bytes(double bytes)
    {
        if (bytes < 0 || double.IsNaN(bytes)) return "-";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int u = 0;
        while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
        return bytes.ToString(u == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[u];
    }

    public static string Duration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds)) return "-";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
