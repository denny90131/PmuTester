using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using C37Comm.Client;
using C37Comm.Protocol;

namespace PmuApp;

/// <summary>即時量測：站台、頻率、ROCOF、相量、類比、DI、STAT。</summary>
public partial class MainViewModel
{
    public ObservableCollection<string> StationList { get; } = [];
    public ObservableCollection<PhasorItem> PhasorList { get; } = [];
    public ObservableCollection<AnalogItem> AnalogList { get; } = [];
    public ObservableCollection<DigitalItem> DigitalList { get; } = [];
    public ObservableCollection<StationSummaryItem> StationSummaries { get; } = [];
    public ObservableCollection<StatFlagItem> StatFlags { get; } =
    [
        new() { Name = "資料品質" }, new() { Name = "時間同步" }, new() { Name = "PMU 觸發" },
        new() { Name = "設定變更" }, new() { Name = "資料修改" }, new() { Name = "PMU 時間品質" },
        new() { Name = "失去同步時間" }, new() { Name = "資料排序" }, new() { Name = "訊息時間品質" },
        new() { Name = "閏秒" }
    ];

    [ObservableProperty] private int _selectedStationIndex = -1;
    [ObservableProperty] private bool _hasMultipleStations;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _hasAnalogs;
    [ObservableProperty] private bool _hasDigitals;
    [ObservableProperty] private string _stationTitle = "尚未取得設定檔";
    [ObservableProperty] private string _timestampText = "--:--:--.---";
    [ObservableProperty] private string _dateText = "";
    [ObservableProperty] private string _frequencyText = "--";
    [ObservableProperty] private string _frequencyDeviationText = "";
    [ObservableProperty] private string _rocofText = "--";
    [ObservableProperty] private bool _isValid;
    [ObservableProperty] private bool _syncLost;
    [ObservableProperty] private string _validText = "--";
    [ObservableProperty] private string _syncText = "--";
    [ObservableProperty] private string _statHex = "--";
    [ObservableProperty] private string _latencyText = "--";
    [ObservableProperty] private string _fpsText = "--";
    [ObservableProperty] private string _nominalText = "";
    [ObservableProperty] private bool _rocofAlarmActive;
    [ObservableProperty] private bool _frequencyAlarmActive;
    [ObservableProperty] private bool _digitalOnlyValid = true;
    [ObservableProperty] private int _trendVersion;
    [ObservableProperty] private int _diagramVersion;
    [ObservableProperty] private string _digitalSummary = "";

    public int[] TrendWindowChoices { get; } = [10, 30, 60, 120, 300, 600];

    partial void OnSelectedStationIndexChanged(int value)
    {
        _selectedStationForTrend = value;
        Trend.Clear();
        RebuildChannelLists();
        UpdateLive();
    }

    partial void OnDigitalOnlyValidChanged(bool value) => RebuildChannelLists();

    private void ClearLiveValues()
    {
        HasData = false;
        TimestampText = "--:--:--.---";
        DateText = "";
        FrequencyText = RocofText = LatencyText = FpsText = StatHex = ValidText = SyncText = "--";
        FrequencyDeviationText = "";
        IsValid = SyncLost = false;
        RocofAlarmActive = FrequencyAlarmActive = false;
        foreach (var f in StatFlags) { f.ValueText = "--"; f.IsBad = f.IsNotice = false; }
    }

    /// <summary>依目前選取的站台，重建相量 / 類比 / DI 清單（名稱、單位來自 CFG-2）。</summary>
    private void RebuildChannelLists()
    {
        PhasorList.Clear();
        AnalogList.Clear();
        DigitalList.Clear();
        var cfg = _currentConfig;
        if (cfg is null || SelectedStationIndex < 0 || SelectedStationIndex >= cfg.Stations.Count)
        {
            HasAnalogs = HasDigitals = false;
            StationTitle = "尚未取得設定檔";
            NominalText = "";
            return;
        }

        var st = cfg.Stations[SelectedStationIndex];
        StationTitle = $"{StationName(st)}  ·  IDCODE {st.IdCode}";
        NominalText = $"額定 {st.NominalFrequency:0} Hz";

        for (int i = 0; i < st.PhasorNames.Count; i++)
        {
            bool isCurrent = i < st.PhasorUnits.Count && st.PhasorUnits[i].IsCurrent;
            var item = PhasorItem.Create(i, st.PhasorNames[i].Trim(), isCurrent);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PhasorItem.IsVisibleInDiagram)) DiagramVersion++;
            };
            PhasorList.Add(item);
        }

        for (int i = 0; i < st.AnalogNames.Count; i++)
        {
            string unit = i < st.AnalogUnits.Count ? AnalogUnitText(st.AnalogUnits[i]) : "";
            AnalogList.Add(new AnalogItem { Index = i, Name = st.AnalogNames[i].Trim(), UnitText = unit });
        }

        for (int w = 0; w < st.DigitalWordCount; w++)
        {
            ushort validMask = w < st.DigitalUnits.Count ? st.DigitalUnits[w].ValidMask : (ushort)0xFFFF;
            for (int bit = 0; bit < DigitalDecoder.PointsPerWord; bit++)
            {
                bool valid = (validMask & (1 << bit)) != 0;
                if (DigitalOnlyValid && !valid) continue;
                int idx = w * DigitalDecoder.PointsPerWord + bit;
                string name = idx < st.DigitalNames.Count ? st.DigitalNames[idx].Trim() : "";
                DigitalList.Add(new DigitalItem { Word = w, Bit = bit, Name = string.IsNullOrEmpty(name) ? $"(未命名 {bit})" : name, Valid = valid });
            }
        }

        HasAnalogs = AnalogList.Count > 0;
        HasDigitals = st.DigitalWordCount > 0;
        DiagramVersion++;
    }

    private static string AnalogUnitText(AnalogUnit u)
    {
        string type = u.Type switch { 0 => "單點取樣", 1 => "RMS", 2 => "峰值", _ => $"類型 {u.Type}" };
        return $"{type} · 比例 {u.Scale}";
    }

    internal static string StationName(StationConfig st) =>
        string.IsNullOrWhiteSpace(st.Name) ? $"Station {st.IdCode}" : st.Name.Trim();

    /// <summary>UI 計時器：把最新的 Frame 灌進畫面（只處理目前選取的站台與頁面）。</summary>
    private void UpdateLive()
    {
        var frame = _latestFrame;
        if (frame is null || _currentConfig is null) return;
        if (SelectedStationIndex < 0 || SelectedStationIndex >= frame.Stations.Count) return;

        var ui = Settings.Ui;
        var inv = CultureInfo.InvariantCulture;
        StationSample s = frame.Stations[SelectedStationIndex];
        HasData = true;

        // Data Frame 時間戳為 UTC，依設定顯示本機時間或 UTC
        var ts = ui.TimeZone == TimeDisplayZone.Utc ? frame.Timestamp : frame.Timestamp.ToLocalTime();
        try { TimestampText = ts.ToString(ui.TimestampFormat, inv); } catch (FormatException) { TimestampText = ts.ToString("HH:mm:ss.fff", inv); }
        DateText = ts.ToString("yyyy-MM-dd", inv) + (ui.TimeZone == TimeDisplayZone.Utc ? " UTC" : "");

        string fFmt = "F" + ui.FrequencyDecimals;
        FrequencyText = s.Frequency is { } f ? f.ToString(fFmt, inv) : "--";
        FrequencyDeviationText = s.Frequency switch
        {
            null => "",
            0 => "0 Hz：PMU 可能未接入電壓訊號",
            { } f2 when Math.Abs(f2 - s.Config.NominalFrequency) < 5 => $"Δ {(f2 - s.Config.NominalFrequency) * 1000:+0.0;-0.0;0.0} mHz（相對額定 {s.Config.NominalFrequency:0} Hz）",
            _ => $"與額定 {s.Config.NominalFrequency:0} Hz 相差過大"
        };
        RocofText = s.Rocof is { } r ? r.ToString(fFmt, inv) : "--";
        IsValid = s.IsValid;
        SyncLost = s.Stat.SyncLost;
        ValidText = s.IsValid ? "有效" : "無效";
        SyncText = s.Stat.SyncLost ? "未對時" : "已對時";
        StatHex = $"0x{s.Stat.Raw:X4}";

        long recvTicks = Interlocked.Read(ref _latestReceivedTicks);
        if (!s.Stat.SyncLost && recvTicks > 0)
        {
            double ms = (recvTicks - frame.Timestamp.UtcTicks) / (double)TimeSpan.TicksPerMillisecond;
            LatencyText = Math.Abs(ms) < 60_000 ? $"{ms:F0} ms" : "--";
        }
        else LatencyText = "--";

        bool alarmsOn = Settings.Alarm.Enabled;
        RocofAlarmActive = alarmsOn && s.IsValid && s.Rocof is { } rr && Math.Abs(rr) >= Settings.Alarm.RocofThreshold;
        FrequencyAlarmActive = alarmsOn && s.IsValid && s.Frequency is { } ff && ff != 0 &&
                               (ff > Settings.Alarm.FrequencyHighLimit || ff < Settings.Alarm.FrequencyLowLimit);

        UpdateStatFlags(s.Stat, frame);

        int tab = SelectedTabIndex;
        if (tab == TabLive) UpdatePhasors(s, ui);
        if (tab == TabLive && HasMultipleStations) UpdateStationSummaries(frame, fFmt);
        if (tab == TabIo) UpdateIo(s);
        if (tab == TabTrend && !TrendPaused) TrendVersion++;
    }

    private void UpdatePhasors(StationSample s, UiSettings ui)
    {
        var inv = CultureInfo.InvariantCulture;
        string magFmt = "F" + ui.MagnitudeDecimals;
        string angFmt = "F" + ui.AngleDecimals;
        bool deg = ui.AngleUnit == AngleUnit.Degree;

        int count = Math.Min(PhasorList.Count, s.Phasors.Count);
        for (int i = 0; i < count; i++)
        {
            var item = PhasorList[i];
            if (s.Phasors[i] is { } p)
            {
                double mag = p.ToMagnitude();
                double angDeg = p.ToAngleDegrees();
                item.HasValue = true;
                item.Magnitude = mag;
                item.AngleDegrees = angDeg;
                item.MagnitudeText = mag.ToString(magFmt, inv);
                item.AngleText = deg ? $"{angDeg.ToString(angFmt, inv)}°" : $"{p.ToAngle().ToString(angFmt, inv)} rad";
                item.RealText = p.ToReal().ToString(magFmt, inv);
                item.ImaginaryText = p.ToImaginary().ToString(magFmt, inv);
            }
            else item.Clear();
        }
        DiagramVersion++;
    }

    private void UpdateStationSummaries(DataFrame frame, string fFmt)
    {
        var inv = CultureInfo.InvariantCulture;
        int n = Math.Min(StationSummaries.Count, frame.Stations.Count);
        for (int i = 0; i < n; i++)
        {
            var s = frame.Stations[i];
            var item = StationSummaries[i];
            item.FrequencyText = s.Frequency is { } f ? $"{f.ToString(fFmt, inv)} Hz" : "--";
            item.RocofText = s.Rocof is { } r ? $"{r.ToString(fFmt, inv)} Hz/s" : "--";
            item.IsValid = s.IsValid;
            item.SyncLost = s.Stat.SyncLost;
            item.StatText = $"0x{s.Stat.Raw:X4}";
        }
    }

    private void UpdateIo(StationSample s)
    {
        var inv = CultureInfo.InvariantCulture;
        int n = Math.Min(AnalogList.Count, s.Analogs.Count);
        for (int i = 0; i < n; i++)
            AnalogList[i].ValueText = s.Analogs[i].Raw is { } v ? v.ToString("G7", inv) : "--";

        int on = 0, abnormal = 0;
        foreach (var d in DigitalList)
        {
            if (d.Word >= s.Digitals.Count) continue;
            var w = s.Digitals[d.Word];
            if (d.Bit >= w.Points.Count) continue;
            var p = w.Points[d.Bit];
            d.Value = p.Value;
            d.Valid = p.Valid;
            d.Abnormal = p.Abnormal;
            d.StateText = !p.Valid ? "未啟用" : p.Value ? "1  ON" : "0  OFF";
            if (p.Valid && p.Value) on++;
            if (p.Abnormal) abnormal++;
        }
        DigitalSummary = $"共 {DigitalList.Count} 點 · ON {on} · 異常 {abnormal}" +
                         (s.Digitals.Count > 0 ? "   狀態字 " + string.Join(" ", s.Digitals.Select(w => $"0x{w.Raw:X4}")) : "");
    }

    private void UpdateStatFlags(StatWord st, DataFrame frame)
    {
        bool v2005 = st.Version == FrameHeader.Version2005;
        Set(0, st.DataError switch
        {
            0 => "正常",
            1 => v2005 ? "PMU 錯誤" : "PMU 錯誤，資料無效",
            2 => v2005 ? "資料無效" : "測試模式",
            3 => v2005 ? "資料無效 + PMU 錯誤" : "資料不可用",
            _ => st.DataError.ToString()
        }, bad: st.DataError != 0);
        Set(1, st.SyncLost ? "失去同步（時間戳不可信）" : "同步正常", bad: st.SyncLost);
        Set(2, st.PmuTrigger ? $"觸發中：{TriggerReasonText(st.TriggerReason)}" : "無", notice: st.PmuTrigger);
        Set(3, st.ConfigChange ? "PMU 即將或剛剛變更設定" : "無", notice: st.ConfigChange);
        Set(4, st.DataModified is { } m ? (m ? "資料經過修改" : "否") : "（2005 版無此欄位）", bad: st.DataModified == true);
        Set(5, st.TimeQuality is { } q ? PmuTimeQualityText(q) : "（2005 版無此欄位）", notice: st.TimeQuality is >= 5);
        Set(6, st.UnlockedTime switch
        {
            0 => "同步中或失去 < 10 秒",
            1 => "10–100 秒",
            2 => "100–1000 秒",
            _ => "> 1000 秒"
        }, notice: st.UnlockedTime > 0);
        Set(7, st.SortedByArrival ? "依到達時間排序" : "依時間戳排序");

        byte tq = frame.Header.TimeQuality;
        int code = tq & 0x0F;
        Set(8, MessageTimeQualityText(code) + (frame.FractionOutOfRange ? "；FRACSEC 超出範圍" : ""),
            bad: code == 0x0F || frame.FractionOutOfRange, notice: code is > 0 and < 0x0F);
        bool pending = (tq & 0x40) != 0, occurred = (tq & 0x20) != 0, negative = (tq & 0x10) != 0;
        Set(9, pending ? $"即將{(negative ? "刪除" : "插入")}閏秒" : occurred ? "剛發生閏秒" : "無", notice: pending || occurred);

        void Set(int i, string text, bool bad = false, bool notice = false)
        {
            var f = StatFlags[i];
            f.ValueText = text;
            f.IsBad = bad;
            f.IsNotice = !bad && notice;
        }
    }

    private static string TriggerReasonText(int r) => r switch
    {
        0 => "手動",
        1 => "幅值過低",
        2 => "幅值過高",
        3 => "相角差",
        4 => "頻率過高或過低",
        5 => "df/dt 過高",
        7 => "數位輸入",
        >= 8 => $"使用者定義 {r}",
        _ => $"保留 {r}"
    };

    private static string PmuTimeQualityText(int q) => q switch
    {
        0 => "未使用",
        1 => "< 100 ns",
        2 => "< 1 µs",
        3 => "< 10 µs",
        4 => "< 100 µs",
        5 => "< 1 ms",
        6 => "< 10 ms",
        _ => "> 10 ms 或未知"
    };

    private static string MessageTimeQualityText(int code) => code switch
    {
        0 => "時鐘鎖定，正常",
        0x0F => "時鐘故障，時間不可信",
        0x0B => "誤差 < 10 s",
        0x0A => "誤差 < 1 s",
        0x09 => "誤差 < 100 ms",
        0x08 => "誤差 < 10 ms",
        0x07 => "誤差 < 1 ms",
        0x06 => "誤差 < 100 µs",
        0x05 => "誤差 < 10 µs",
        0x04 => "誤差 < 1 µs",
        0x03 => "誤差 < 100 ns",
        0x02 => "誤差 < 10 ns",
        0x01 => "誤差 < 1 ns",
        _ => $"代碼 0x{code:X}"
    };

    [RelayCommand]
    private void ToggleAllPhasors()
    {
        bool anyHidden = PhasorList.Any(p => !p.IsVisibleInDiagram);
        foreach (var p in PhasorList) p.IsVisibleInDiagram = anyHidden;
        DiagramVersion++;
    }

    [RelayCommand]
    private void ShowOnlyVoltages()
    {
        foreach (var p in PhasorList) p.IsVisibleInDiagram = !p.IsCurrent;
        DiagramVersion++;
    }

    [RelayCommand]
    private void ShowOnlyCurrents()
    {
        foreach (var p in PhasorList) p.IsVisibleInDiagram = p.IsCurrent;
        DiagramVersion++;
    }

    /// <summary>把目前站台的數值複製成文字表格（可直接貼到 Excel / 報告）。</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task CopySnapshotAsync()
    {
        if (Ui is null) return;
        var frame = _latestFrame;
        if (frame is null || SelectedStationIndex < 0 || SelectedStationIndex >= frame.Stations.Count)
        {
            AddEvent(EventLevel.Info, "複製", "目前沒有資料可複製");
            return;
        }
        var s = frame.Stations[SelectedStationIndex];
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"站台\t{StationName(s.Config)}\tIDCODE\t{s.Config.IdCode}");
        sb.AppendLine($"時間 (UTC)\t{frame.Timestamp.UtcDateTime:yyyy-MM-dd HH:mm:ss.ffffff}");
        sb.AppendLine($"頻率 (Hz)\t{s.Frequency?.ToString("R", inv)}");
        sb.AppendLine($"ROCOF (Hz/s)\t{s.Rocof?.ToString("R", inv)}");
        sb.AppendLine($"有效\t{(s.IsValid ? "是" : "否")}\tSTAT\t0x{s.Stat.Raw:X4}\t對時\t{(s.Stat.SyncLost ? "否" : "是")}");
        sb.AppendLine();
        sb.AppendLine("相量\t類型\t幅值\t相角 (°)\t實部\t虛部");
        for (int i = 0; i < s.Phasors.Count; i++)
        {
            string name = i < s.Config.PhasorNames.Count ? s.Config.PhasorNames[i].Trim() : $"#{i}";
            string unit = i < s.Config.PhasorUnits.Count && s.Config.PhasorUnits[i].IsCurrent ? "A" : "V";
            if (s.Phasors[i] is { } p)
                sb.AppendLine($"{name}\t{unit}\t{p.ToMagnitude().ToString("R", inv)}\t{p.ToAngleDegrees().ToString("R", inv)}\t{p.ToReal().ToString("R", inv)}\t{p.ToImaginary().ToString("R", inv)}");
            else sb.AppendLine($"{name}\t{unit}\t(缺值)");
        }
        if (s.Analogs.Count > 0)
        {
            sb.AppendLine().AppendLine("類比\t原始值");
            for (int i = 0; i < s.Analogs.Count; i++)
                sb.AppendLine($"{(i < s.Config.AnalogNames.Count ? s.Config.AnalogNames[i].Trim() : $"#{i}")}\t{s.Analogs[i].Raw?.ToString("R", inv)}");
        }
        var dps = s.Digitals.SelectMany(w => w.Points).Where(d => d.Valid).ToList();
        if (dps.Count > 0)
        {
            sb.AppendLine().AppendLine("DI\t值\t異常");
            foreach (var d in dps) sb.AppendLine($"{d.Name.Trim()}\t{(d.Value ? 1 : 0)}\t{(d.Abnormal ? "是" : "")}");
        }
        await Ui.CopyTextAsync(sb.ToString());
        AddEvent(EventLevel.Info, "複製", "已複製目前數值到剪貼簿");
    }
}
