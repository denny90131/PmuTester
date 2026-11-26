using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace PmuApp;

/// <summary>ViewModel 需要的視窗功能（剪貼簿、存檔對話框、主題），由 MainWindow 實作。</summary>
public interface IUiServices
{
    Task CopyTextAsync(string text);
    /// <summary>顯示存檔對話框，回傳本機路徑；取消時回傳 null。</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string filterName);
    void ApplyTheme(AppTheme theme);
}

internal static class Shell
{
    /// <summary>用系統預設程式開啟檔案或資料夾。</summary>
    public static void Open(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>在檔案總管中選取檔案。</summary>
    public static void Reveal(string file)
    {
        if (File.Exists(file) && System.OperatingSystem.IsWindows())
            Process.Start("explorer.exe", $"/select,\"{file}\"");
        else
            Open(Path.GetDirectoryName(file) ?? file);
    }
}
