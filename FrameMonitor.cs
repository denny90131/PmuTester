using System;
using System.Collections.Generic;
using System.Threading;
using C37Comm.Protocol;

namespace PmuApp;

/// <summary>監視器產生的事件（交給 UI 寫進事件紀錄）。</summary>
public readonly record struct MonitorEvent(EventLevel Level, string Category, string Message);

/// <summary>
/// 在 C37Comm 的派送執行緒上逐筆檢查 Data Frame（必須夠快，不做 I/O）：
/// <list type="bullet">
/// <item>掉包 / 時間倒退（Client 手冊 6.3）</item>
/// <item>ROCOF 連續 N 點超過門檻（Client 手冊 6.1）</item>
/// <item>頻率超出上下限</item>
/// <item>實際接收 fps</item>
/// </list>
/// 事件依序觸發，內部狀態不需加鎖；計數器以 Interlocked / volatile 供 UI 執行緒讀取。
/// </summary>
public sealed class FrameMonitor
{
    private readonly Func<AlarmSettings> _alarm;

    private TimeSpan _interval;
    private DateTimeOffset? _last;
    private int[] _rocofCount = [];
    private bool[] _rocofActive = [];
    private bool[] _freqActive = [];

    // fps：以接收時間計算最近 1 秒
    private readonly Queue<DateTimeOffset> _recent = new();

    private long _gapEvents;
    private long _missingFrames;
    private long _backwardEvents;
    private long _rocofAlarms;
    private long _frequencyAlarms;
    private int _fpsX100;
    private int _activeAlarms;

    public FrameMonitor(Func<AlarmSettings> alarm) => _alarm = alarm;

    public long GapEvents => Interlocked.Read(ref _gapEvents);
    public long MissingFrames => Interlocked.Read(ref _missingFrames);
    public long BackwardEvents => Interlocked.Read(ref _backwardEvents);
    public long RocofAlarms => Interlocked.Read(ref _rocofAlarms);
    public long FrequencyAlarms => Interlocked.Read(ref _frequencyAlarms);
    /// <summary>最近 1 秒實際收到的 Frame 數。</summary>
    public double ActualFps => Volatile.Read(ref _fpsX100) / 100.0;
    /// <summary>目前仍在告警中的項目數（ROCOF + 頻率，所有站台）。</summary>
    public int ActiveAlarms => Volatile.Read(ref _activeAlarms);

    public void OnConfiguration(ConfigurationFrame cfg)
    {
        _interval = cfg.ExpectedInterval;
        int n = cfg.Stations.Count;
        _rocofCount = new int[n];
        _rocofActive = new bool[n];
        _freqActive = new bool[n];
        _last = null;
        Volatile.Write(ref _activeAlarms, 0);
    }

    /// <summary>連線 / 斷線時呼叫：重新開始計算。</summary>
    public void Reset()
    {
        _last = null;
        Array.Clear(_rocofCount);
        Array.Clear(_rocofActive);
        Array.Clear(_freqActive);
        _recent.Clear();
        Volatile.Write(ref _fpsX100, 0);
        Volatile.Write(ref _activeAlarms, 0);
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _gapEvents, 0);
        Interlocked.Exchange(ref _missingFrames, 0);
        Interlocked.Exchange(ref _backwardEvents, 0);
        Interlocked.Exchange(ref _rocofAlarms, 0);
        Interlocked.Exchange(ref _frequencyAlarms, 0);
    }

    /// <summary>檢查一個 Frame；有事件時加入 <paramref name="events"/>。</summary>
    public void OnFrame(DataFrame frame, DateTimeOffset receivedAt, List<MonitorEvent> events)
    {
        UpdateFps(receivedAt);

        var alarm = _alarm();
        if (alarm.DetectGaps) CheckTiming(frame.Timestamp, events);
        else _last = frame.Timestamp;

        if (!alarm.Enabled)
        {
            if (ActiveAlarms != 0) { Array.Clear(_rocofActive); Array.Clear(_freqActive); Volatile.Write(ref _activeAlarms, 0); }
            return;
        }

        int n = Math.Min(frame.Stations.Count, _rocofCount.Length);
        int active = 0;
        for (int i = 0; i < n; i++)
        {
            var s = frame.Stations[i];
            CheckRocof(i, s, alarm, frame.Timestamp, events);
            CheckFrequency(i, s, alarm, events);
            if (_rocofActive[i]) active++;
            if (_freqActive[i]) active++;
        }
        Volatile.Write(ref _activeAlarms, active);
    }

    private void UpdateFps(DateTimeOffset receivedAt)
    {
        _recent.Enqueue(receivedAt);
        var cutoff = receivedAt - TimeSpan.FromSeconds(1);
        while (_recent.Count > 0 && _recent.Peek() <= cutoff) _recent.Dequeue();
        Volatile.Write(ref _fpsX100, _recent.Count * 100);
    }

    private void CheckTiming(DateTimeOffset t, List<MonitorEvent> events)
    {
        if (_last is { } prev && _interval > TimeSpan.Zero)
        {
            var delta = t - prev;
            if (delta <= TimeSpan.Zero)
            {
                Interlocked.Increment(ref _backwardEvents);
                events.Add(new(EventLevel.Warning, "時序", $"時間戳倒退或重複：{prev.ToLocalTime():HH:mm:ss.fff} → {t.ToLocalTime():HH:mm:ss.fff}"));
            }
            else if (delta > _interval * 1.5)
            {
                long missing = Math.Max(1, (long)Math.Round(delta / _interval) - 1);
                Interlocked.Increment(ref _gapEvents);
                Interlocked.Add(ref _missingFrames, missing);
                events.Add(new(EventLevel.Warning, "掉包",
                    $"{prev.ToLocalTime():HH:mm:ss.fff} → {t.ToLocalTime():HH:mm:ss.fff} 間隔 {delta.TotalMilliseconds:F0} ms，約缺 {missing} 筆"));
            }
        }
        if (_last is null || t > _last) _last = t;
    }

    private void CheckRocof(int i, StationSample s, AlarmSettings alarm, DateTimeOffset t, List<MonitorEvent> events)
    {
        if (!s.IsValid || s.Rocof is not { } rocof)
        {
            _rocofCount[i] = 0;   // 無效點不列入判斷，計數歸零
            return;
        }

        bool over = Math.Abs(rocof) >= alarm.RocofThreshold;
        _rocofCount[i] = over ? _rocofCount[i] + 1 : 0;

        if (!_rocofActive[i] && _rocofCount[i] >= alarm.RocofConsecutivePoints)
        {
            _rocofActive[i] = true;
            Interlocked.Increment(ref _rocofAlarms);
            events.Add(new(EventLevel.Alarm, "ROCOF",
                $"[{StationName(s)}] {t.ToLocalTime():HH:mm:ss.fff} ROCOF 連續 {alarm.RocofConsecutivePoints} 點 ≥ {alarm.RocofThreshold} Hz/s（{rocof:F4} Hz/s）"));
        }
        else if (_rocofActive[i] && !over)
        {
            _rocofActive[i] = false;
            events.Add(new(EventLevel.Info, "ROCOF", $"[{StationName(s)}] ROCOF 恢復正常（{rocof:F4} Hz/s）"));
        }
    }

    private void CheckFrequency(int i, StationSample s, AlarmSettings alarm, List<MonitorEvent> events)
    {
        if (!s.IsValid || s.Frequency is not { } f || f == 0) return;   // 0 Hz：PMU 未接入電壓訊號，不當成頻率告警

        bool outOfBand = f > alarm.FrequencyHighLimit || f < alarm.FrequencyLowLimit;
        if (outOfBand && !_freqActive[i])
        {
            _freqActive[i] = true;
            Interlocked.Increment(ref _frequencyAlarms);
            var which = f > alarm.FrequencyHighLimit ? $"高於上限 {alarm.FrequencyHighLimit} Hz" : $"低於下限 {alarm.FrequencyLowLimit} Hz";
            events.Add(new(EventLevel.Alarm, "頻率", $"[{StationName(s)}] 頻率 {f:F4} Hz {which}"));
        }
        else if (!outOfBand && _freqActive[i])
        {
            _freqActive[i] = false;
            events.Add(new(EventLevel.Info, "頻率", $"[{StationName(s)}] 頻率恢復正常（{f:F4} Hz）"));
        }
    }

    private static string StationName(StationSample s) =>
        string.IsNullOrWhiteSpace(s.Config.Name) ? $"ID {s.Config.IdCode}" : s.Config.Name.Trim();
}
