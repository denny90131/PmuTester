using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using C37Comm.Protocol;

namespace PmuApp;

/// <summary>
/// 把 Data Frame 寫成 CSV（每個 Frame 一列，所有站台並排）。
/// 派送執行緒只負責 <see cref="TryEnqueue"/>，檔案 I/O 在背景工作中進行（Client 手冊第 5 節）。
/// CFG-2 改變時自動換新檔，欄位與新設定檔一致。
/// </summary>
public sealed class CsvRecorder : IAsyncDisposable
{
    private readonly Channel<DataFrame> _queue = Channel.CreateBounded<DataFrame>(new BoundedChannelOptions(50_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true
    });

    private readonly string _directory;
    private readonly string _filePrefix;
    private readonly RecordingSettings _settings;
    private readonly Task _worker;
    private long _framesSeen;
    private long _rowsWritten;
    private long _dropped;
    private string _currentFile = "";
    private string? _lastError;

    public CsvRecorder(string directory, string filePrefix, RecordingSettings settings)
    {
        _directory = directory;
        _filePrefix = filePrefix;
        _settings = settings;
        Directory.CreateDirectory(_directory);
        StartedAt = DateTime.Now;
        _worker = Task.Run(WriteLoopAsync);
    }

    public DateTime StartedAt { get; }
    public long RowsWritten => Interlocked.Read(ref _rowsWritten);
    public long Dropped => Interlocked.Read(ref _dropped);
    public string CurrentFile => Volatile.Read(ref _currentFile);
    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>由派送執行緒呼叫，只做降頻判斷與入列。</summary>
    public void TryEnqueue(DataFrame frame)
    {
        long n = Interlocked.Increment(ref _framesSeen);
        if ((n - 1) % _settings.EveryNthFrame != 0) return;
        if (_settings.OnlyValid && !frame.Stations.All(s => s.IsValid)) return;
        if (!_queue.Writer.TryWrite(frame)) Interlocked.Increment(ref _dropped);
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        ConfigurationFrame? cfg = null;
        var sb = new StringBuilder(4096);
        var lastFlush = DateTime.UtcNow;
        try
        {
            await foreach (var frame in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    if (writer is null || !ReferenceEquals(cfg, frame.Configuration))
                    {
                        if (writer is not null) await writer.DisposeAsync();
                        cfg = frame.Configuration;
                        writer = OpenNewFile(cfg);
                    }

                    sb.Clear();
                    AppendRow(sb, frame);
                    await writer.WriteLineAsync(sb.ToString());
                    Interlocked.Increment(ref _rowsWritten);

                    if (DateTime.UtcNow - lastFlush > TimeSpan.FromSeconds(1) || _queue.Reader.Count == 0)
                    {
                        await writer.FlushAsync();
                        lastFlush = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _lastError, ex.Message);
                    Interlocked.Increment(ref _dropped);
                }
            }
        }
        finally
        {
            if (writer is not null) await writer.DisposeAsync();
        }
    }

    private StreamWriter OpenNewFile(ConfigurationFrame cfg)
    {
        var name = $"{_filePrefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.csv";
        var path = Path.Combine(_directory, name);
        // UTF-8 BOM：Excel 直接開啟中文不亂碼
        var writer = new StreamWriter(path, append: false, new UTF8Encoding(true), 1 << 16);
        writer.WriteLine(BuildHeader(cfg));
        Volatile.Write(ref _currentFile, path);
        return writer;
    }

    private string BuildHeader(ConfigurationFrame cfg)
    {
        var cols = new System.Collections.Generic.List<string>
        {
            _settings.UseUtc ? "Time(UTC)" : "Time(Local)", "SOC", "FRACSEC"
        };
        bool multi = cfg.Stations.Count > 1;
        foreach (var st in cfg.Stations)
        {
            string p = multi ? st.Name.Trim() + "." : "";
            cols.AddRange([$"{p}FREQ(Hz)", $"{p}ROCOF(Hz/s)", $"{p}VALID", $"{p}STAT"]);
            for (int i = 0; i < st.PhasorNames.Count; i++)
            {
                string unit = i < st.PhasorUnits.Count && st.PhasorUnits[i].IsCurrent ? "A" : "V";
                string n = p + st.PhasorNames[i].Trim();
                cols.Add($"{n}.MAG({unit})");
                cols.Add($"{n}.ANG(deg)");
                if (_settings.IncludeRectangular) { cols.Add($"{n}.RE"); cols.Add($"{n}.IM"); }
            }
            foreach (var a in st.AnalogNames) cols.Add(p + a.Trim());
            for (int w = 0; w < st.DigitalWordCount; w++) cols.Add($"{p}DIGITAL{w}");
        }
        return string.Join(',', cols.Select(Csv));
    }

    private void AppendRow(StringBuilder sb, DataFrame f)
    {
        var inv = CultureInfo.InvariantCulture;
        var t = _settings.UseUtc ? f.Timestamp.UtcDateTime : f.Timestamp.LocalDateTime;
        sb.Append(t.ToString("yyyy-MM-dd HH:mm:ss.ffffff", inv))
          .Append(',').Append(f.Header.Soc.ToString(inv))
          .Append(',').Append(f.Header.FractionOfSecond.ToString(inv));

        foreach (var s in f.Stations)
        {
            sb.Append(',').Append(s.Frequency?.ToString("R", inv))
              .Append(',').Append(s.Rocof?.ToString("R", inv))
              .Append(',').Append(s.IsValid ? '1' : '0')
              .Append(",0x").Append(s.Stat.Raw.ToString("X4"));
            foreach (var p in s.Phasors)
            {
                if (p is { } v)
                {
                    sb.Append(',').Append(v.ToMagnitude().ToString("R", inv))
                      .Append(',').Append(v.ToAngleDegrees().ToString("R", inv));
                    if (_settings.IncludeRectangular)
                        sb.Append(',').Append(v.ToReal().ToString("R", inv))
                          .Append(',').Append(v.ToImaginary().ToString("R", inv));
                }
                else sb.Append(_settings.IncludeRectangular ? ",,,," : ",,");
            }
            foreach (var a in s.Analogs) sb.Append(',').Append(a.Raw?.ToString("R", inv));
            foreach (var w in s.Digitals) sb.Append(",0x").Append(w.Raw.ToString("X4"));
        }
    }

    private static string Csv(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { /* 關閉中，忽略 */ }
    }
}
