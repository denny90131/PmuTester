using System;

namespace PmuApp;

/// <summary>趨勢圖的環狀緩衝（派送執行緒寫入、UI 執行緒讀取，以鎖保護）。</summary>
public sealed class TrendBuffer
{
    public const int Capacity = 120 * 600;   // 最高 120 fps × 600 秒

    private readonly object _lock = new();
    private readonly long[] _ticks = new long[Capacity];   // UTC ticks
    private readonly double[] _freq = new double[Capacity];
    private readonly double[] _rocof = new double[Capacity];
    private int _start;
    private int _count;

    public void Add(DateTimeOffset t, double? frequency, double? rocof)
    {
        lock (_lock)
        {
            int idx = (_start + _count) % Capacity;
            _ticks[idx] = t.UtcTicks;
            _freq[idx] = frequency ?? double.NaN;
            _rocof[idx] = rocof ?? double.NaN;
            if (_count < Capacity) _count++;
            else _start = (_start + 1) % Capacity;
        }
    }

    public void Clear()
    {
        lock (_lock) { _start = 0; _count = 0; }
    }

    /// <summary>
    /// 複製最近 <paramref name="window"/> 內的資料到呼叫端的陣列（自動擴充），回傳筆數。
    /// <paramref name="series"/>：0 = 頻率，1 = ROCOF。
    /// </summary>
    public int Snapshot(TimeSpan window, int series, ref long[] ticks, ref double[] values, out long latestTicks)
    {
        lock (_lock)
        {
            latestTicks = 0;
            if (_count == 0) return 0;
            int lastIdx = (_start + _count - 1) % Capacity;
            latestTicks = _ticks[lastIdx];
            long from = latestTicks - window.Ticks;

            // 從尾端往前找視窗起點
            int n = 0;
            while (n < _count && _ticks[(_start + _count - 1 - n) % Capacity] >= from) n++;

            if (ticks.Length < n) { ticks = new long[n]; values = new double[n]; }
            var src = series == 0 ? _freq : _rocof;
            for (int k = 0; k < n; k++)
            {
                int idx = (_start + _count - n + k) % Capacity;
                ticks[k] = _ticks[idx];
                values[k] = src[idx];
            }
            return n;
        }
    }
}
