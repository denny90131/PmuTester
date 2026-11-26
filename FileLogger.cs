using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace PmuApp;

/// <summary>簡易檔案記錄器：把 C37Comm 的診斷訊息依日期寫入檔案。</summary>
public sealed class FileLogger : ILogger
{
    private readonly object _lock = new();
    private readonly string _directory;
    private readonly string _pattern;
    private readonly LogLevel _minLevel;

    public FileLogger(LoggingSettings settings)
    {
        _directory = Path.IsPathRooted(settings.Directory)
            ? settings.Directory
            : Path.Combine(AppContext.BaseDirectory, settings.Directory);
        _pattern = settings.FileNamePattern;
        _minLevel = (LogLevel)(int)settings.MinimumLevel;
        Directory.CreateDirectory(_directory);
        CleanupOldFiles(settings.RetentionDays);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {formatter(state, exception)}";
        if (exception is not null) line += Environment.NewLine + exception;

        var path = Path.Combine(_directory, _pattern.Replace("{date}", now.ToString("yyyyMMdd")));
        lock (_lock)
        {
            try { File.AppendAllText(path, line + Environment.NewLine); }
            catch { /* 記錄失敗不可影響主程式 */ }
        }
    }

    private void CleanupOldFiles(int retentionDays)
    {
        if (retentionDays <= 0) return;
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        var searchPattern = _pattern.Replace("{date}", "*");
        foreach (var file in Directory.EnumerateFiles(_directory, searchPattern))
        {
            try { if (File.GetLastWriteTime(file) < cutoff) File.Delete(file); }
            catch { /* 忽略 */ }
        }
    }
}
