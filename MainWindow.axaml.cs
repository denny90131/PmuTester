using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

namespace PmuApp;

public partial class MainWindow : Window, IUiServices
{
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel { Ui = this };
        DataContext = vm;

        // 設定檔 Connection.AutoConnect = true 時，視窗開啟後自動連線
        Opened += async (_, _) =>
        {
            HostBox.Focus();
            await vm.OnWindowOpenedAsync();
        };

        // 在 IP 輸入框按 Enter 直接連線
        HostBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && vm.ToggleConnectionCommand.CanExecute(null))
                vm.ToggleConnectionCommand.Execute(null);
        };
    }

    // 關閉視窗時先停止 PmuClient（送出「關閉資料流」給 PMU）、結束錄製，再真正關閉
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (!_disposed && DataContext is MainViewModel vm)
        {
            e.Cancel = true;
            _disposed = true;
            try { await vm.DisposeAsync(); }
            catch { /* 關閉中，忽略 */ }
            Close();
            return;
        }
        base.OnClosing(e);
    }

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is { } cb) await cb.SetTextAsync(text);
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string filterName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType(filterName) { Patterns = [$"*.{extension}"] },
                new FilePickerFileType("所有檔案") { Patterns = ["*"] }
            ]
        });
        return file?.TryGetLocalPath();
    }

    public void ApplyTheme(AppTheme theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
    }
}
