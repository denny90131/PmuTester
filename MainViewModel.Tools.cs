using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using C37Comm.Client;
using C37Comm.Protocol;

namespace PmuApp;

/// <summary>設定檔 (CFG-2)、通訊統計、事件紀錄、CSV 錄製、設定頁。</summary>
public partial class MainViewModel
{
    // ═════════════════════════════ CFG-2 ═════════════════════════════

    public ObservableCollection<CfgStationItem> CfgStations { get; } = [];
    public ObservableCollection<CfgDiffItem> CfgDiffs { get; } = [];
    [ObservableProperty] private bool _hasCfg;
    [ObservableProperty] private string _cfgIdText = "--";
    [ObservableProperty] private string _cfgFingerprint = "--";
    [ObservableProperty] private string _cfgTriggerText = "--";
    [ObservableProperty] private string _cfgReceivedText = "--";
    [ObservableProperty] private string _protocolText = "--";
    [ObservableProperty] private string _dataRateText = "--";
    [ObservableProperty] private string _timeBaseText = "--";
    [ObservableProperty] private string _numPmuText = "--";
    [ObservableProperty] private string _frameSizeText = "--";
    [ObservableProperty] private int _cfgChangeCount;
    private ConfigurationChangedEventArgs? _lastCfgEvent;

    private void ClearCfgView()
    {
        HasCfg = false;
        CfgStations.Clear();
        CfgIdText = CfgFingerprint = CfgTriggerText = CfgReceivedText = ProtocolText = DataRateText = TimeBaseText = NumPmuText = FrameSizeText = "--";
        HasMultipleStations = false;
        SelectedStationIndex = -1;
    }

    /// <summary>收到 CFG-2（UI 執行緒）：更新站台清單、通道、設定檔頁。</summary>
    private void ApplyConfiguration(ConfigurationChangedEventArgs e)
    {
        var cfg = e.Current;
        _lastCfgEvent = e;
        CfgChangeCount++;

        // 站台清單：保留目前選取；無效時改用設定檔的預設站台
        int previous = SelectedStationIndex;
        StationList.Clear();
        StationSummaries.Clear();
        for (int i = 0; i < cfg.Stations.Count; i++)
        {
            var st = cfg.Stations[i];
            StationList.Add($"{StationName(st)}  (ID {st.IdCode})");
            StationSummaries.Add(new StationSummaryItem { Index = i, Name = StationName(st), IdCode = st.IdCode });
        }
        HasMultipleStations = cfg.Stations.Count > 1;

        int target = previous >= 0 && previous < StationList.Count ? previous : Settings.Ui.DefaultStationIndex;
        if (target < 0 || target >= StationList.Count) target = 0;
        if (SelectedStationIndex == target) RebuildChannelLists();   // 索引沒變也要以新 CFG 重建
        else SelectedStationIndex = StationList.Count > 0 ? target : -1;

        // 設定檔頁
        HasCfg = true;
        CfgIdText = e.CfgId.ToString();
        CfgFingerprint = e.Fingerprint;
        CfgTriggerText = e.TriggerSource switch
        {
            CfgTriggerSource.Initial => "第一次取得",
            CfgTriggerSource.StatBit => "PMU 通知設定變更（STAT bit 10）",
            CfgTriggerSource.SizeMismatch => "資料長度與設定檔不符",
            CfgTriggerSource.Reconnect => "重新連線後內容不同",
            _ => e.TriggerSource.ToString()
        };
        CfgReceivedText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        ProtocolText = e.ProtocolVersion == FrameHeader.Version2005 ? "IEEE C37.118-2005（版本 1）" : "IEEE C37.118.2-2011（版本 2）";
        DataRateText = cfg.DataRate > 0
            ? $"{cfg.DataRate} fps（每 {cfg.ExpectedInterval.TotalMilliseconds:F3} ms 一筆）"
            : $"每 {-cfg.DataRate} 秒 1 筆";
        TimeBaseText = $"{cfg.TimeBase:N0}" + (cfg.TimeBaseRaw != cfg.TimeBase ? $"（原始 0x{cfg.TimeBaseRaw:X8}）" : "");
        NumPmuText = cfg.NumPmu.ToString();
        FrameSizeText = $"{cfg.ExpectedDataFrameSize} bytes（CFG-2 本身 {cfg.Raw.Length} bytes）";

        CfgStations.Clear();
        foreach (var st in cfg.Stations) CfgStations.Add(BuildCfgStation(st));

        // 差異紀錄
        foreach (var d in e.Differences)
        {
            CfgDiffs.Insert(0, new CfgDiffItem
            {
                Time = DateTime.Now,
                Kind = d.Kind switch
                {
                    ConfigurationDifferenceKind.Added => "新增",
                    ConfigurationDifferenceKind.Removed => "移除",
                    _ => "修改"
                },
                Station = d.StationIdCode?.ToString() ?? "全部",
                Item = d.Item,
                OldValue = d.OldValue ?? "",
                NewValue = d.NewValue ?? ""
            });
        }

        var level = e.TriggerSource == CfgTriggerSource.Initial ? EventLevel.Info : EventLevel.Warning;
        AddEvent(level, "設定檔",
            $"取得 CFG-2（{CfgTriggerText}）：{cfg.Stations.Count} 個站台、{cfg.DataRate} fps、" +
            $"{(e.ProtocolVersion == 1 ? "2005" : "2011")} 版" +
            (e.Differences.Count > 0 ? $"，與前一版差異 {e.Differences.Count} 項（見「設定檔」頁）" : ""));

        StateDetail = "已取得設定檔，接收資料中";
    }

    private static CfgStationItem BuildCfgStation(StationConfig st)
    {
        var f = st.Format;
        string phFmt = $"{(f.HasFlag(DataFormat.PhasorFloat) ? "浮點" : "整數")}、{(f.HasFlag(DataFormat.PhasorPolar) ? "極座標" : "直角座標")}";
        string summary =
            $"相量 {st.PhasorCount} 個（{phFmt}）· 類比 {st.AnalogCount} 個（{(f.HasFlag(DataFormat.AnalogFloat) ? "浮點" : "整數")}）· " +
            $"DI 狀態字 {st.DigitalWordCount} 個 · 頻率 {(f.HasFlag(DataFormat.FreqFloat) ? "浮點" : "整數")} · " +
            $"額定 {st.NominalFrequency:0} Hz · CFGCNT {st.CfgCnt} · 資料區塊 {st.DataBlockSize} bytes";

        var ch = new List<string>();
        for (int i = 0; i < st.PhasorNames.Count; i++)
        {
            var u = i < st.PhasorUnits.Count ? st.PhasorUnits[i] : default;
            ch.Add($"相量 {i + 1,2}   {st.PhasorNames[i].Trim(),-24} {(u.IsCurrent ? "電流 A" : "電壓 V")}   PHUNIT 比例 {u.Scale} × 1e-5");
        }
        for (int i = 0; i < st.AnalogNames.Count; i++)
        {
            var u = i < st.AnalogUnits.Count ? st.AnalogUnits[i] : default;
            ch.Add($"類比 {i + 1,2}   {st.AnalogNames[i].Trim(),-24} {AnalogUnitText(u)}");
        }
        for (int w = 0; w < st.DigitalWordCount; w++)
        {
            var u = w < st.DigitalUnits.Count ? st.DigitalUnits[w] : default;
            ch.Add($"DI 字 {w}   有效遮罩 0x{u.ValidMask:X4}   正常狀態 0x{u.NormalMask:X4}");
            for (int b = 0; b < DigitalDecoder.PointsPerWord; b++)
            {
                int idx = w * DigitalDecoder.PointsPerWord + b;
                bool valid = (u.ValidMask & (1 << b)) != 0;
                string name = idx < st.DigitalNames.Count ? st.DigitalNames[idx].Trim() : "";
                if (valid || name.Length > 0)
                    ch.Add($"   bit {b,2}   {name,-24} {(valid ? "啟用" : "未啟用")}");
            }
        }

        return new CfgStationItem
        {
            Header = $"{StationName(st)}   IDCODE {st.IdCode}",
            Summary = summary,
            Channels = ch
        };
    }

    [RelayCommand]
    private async Task CopyCfgSummaryAsync()
    {
        if (Ui is null || _currentConfig is null) return;
        var sb = new StringBuilder();
        sb.AppendLine($"CFG ID\t{CfgIdText}");
        sb.AppendLine($"指紋\t{CfgFingerprint}");
        sb.AppendLine($"協定\t{ProtocolText}");
        sb.AppendLine($"DATA_RATE\t{DataRateText}");
        sb.AppendLine($"TIME_BASE\t{TimeBaseText}");
        sb.AppendLine($"站台數\t{NumPmuText}");
        sb.AppendLine($"Data Frame 長度\t{FrameSizeText}");
        foreach (var st in CfgStations)
        {
            sb.AppendLine().AppendLine(st.Header).AppendLine(st.Summary);
            foreach (var c in st.Channels) sb.AppendLine(c);
        }
        await Ui.CopyTextAsync(sb.ToString());
        AddEvent(EventLevel.Info, "設定檔", "已複製 CFG-2 摘要到剪貼簿");
    }

    [RelayCommand]
    private async Task ExportCfgRawAsync()
    {
        var cfg = _currentConfig;
        if (Ui is null || cfg is null) return;
        var path = await Ui.PickSaveFileAsync("匯出 CFG-2 原始封包", $"cfg2_{Settings.Connection.IdCode}_{DateTime.Now:yyyyMMdd_HHmmss}.bin", "bin", "二進位檔");
        if (path is null) return;
        try
        {
            await File.WriteAllBytesAsync(path, cfg.Raw);
            AddEvent(EventLevel.Info, "設定檔", $"已匯出 CFG-2（{cfg.Raw.Length} bytes）：{path}");
        }
        catch (Exception ex) { AddEvent(EventLevel.Error, "設定檔", $"匯出失敗：{ex.Message}"); }
    }

    [RelayCommand]
    private void ClearCfgDiffs() => CfgDiffs.Clear();

    // ═════════════════════════════ 通訊統計 ═════════════════════════════

    private sealed record StatDef(string Name, string Description, bool IsError, Func<ClientStatistics, long> Get);

    private static readonly StatDef[] StatDefs =
    [
        new("Data Frame", "收到的 Data Frame 數量", false, s => s.DataFramesReceived),
        new("CFG-2", "收到的 CFG-2 數量", false, s => s.Cfg2FramesReceived),
        new("重連次數", "斷線後自動重連的次數", true, s => s.ReconnectCount),
        new("處理太慢丟棄", "畫面處理不及而丟棄的資料（正常應為 0）", true, s => s.FramesDroppedSlowConsumer),
        new("CRC 錯誤", "封包 CRC 檢查失敗", true, s => s.CrcErrors),
        new("版本不符", "協定版本不是 2005 / 2011 的 Frame", true, s => s.VersionMismatches),
        new("同步跳過 bytes", "尋找 0xAA 同步字時略過的位元組", true, s => s.SyncSkipBytes),
        new("IDCODE 不符", "IDCODE 與設定不同而丟棄的 Data Frame", true, s => s.IdCodeMismatches),
        new("長度不符", "長度與設定檔不符而丟棄（PMU 變更設定的空窗期）", true, s => s.FrameSizeMismatches),
        new("切換設定檔丟棄", "重新取得 CFG-2 期間丟棄的 Frame", false, s => s.FramesDroppedCfgSwitch),
        new("無法解析", "格式錯誤而丟棄的 Frame", true, s => s.MalformedFrames),
        new("設定檔前的資料", "取得 CFG-2 之前收到、無法解析的 Data Frame（連線初期偶爾會有）", false, s => s.DataBeforeCfg),
        new("其他 Frame", "被略過的 Header / CFG-1 / CFG-3", false, s => s.OtherFramesReceived),
        new("Command Frame", "PMU 送來的 Command Frame", false, s => s.CommandFramesReceived),
        new("處理程式例外", "本程式事件處理程式丟出的例外", true, s => s.HandlerExceptions),
        new("停止時捨棄", "停止時未派送完而捨棄的事件", false, s => s.EventsDroppedOnStop),
    ];

    public ObservableCollection<StatItem> StatItems { get; } = [];
    public ObservableCollection<StatItem> MonitorItems { get; } = [];
    private ClientStatistics? _statsBaseline;
    private ClientStatistics? _lastStats;
    [ObservableProperty] private string _uptimeText = "--";
    [ObservableProperty] private string _lastFrameAgeText = "--";
    [ObservableProperty] private string _rawFrameHex = "";
    [ObservableProperty] private string _rawFrameInfo = "";
    [ObservableProperty] private string _statsSince = "";
    [ObservableProperty] private string _footerText = "";

    private void InitStatistics()
    {
        foreach (var d in StatDefs) StatItems.Add(new StatItem { Name = d.Name, Description = d.Description, IsErrorCounter = d.IsError });
        MonitorItems.Add(new StatItem { Name = "實際接收速率", Description = "最近 1 秒收到的 Frame 數" });
        MonitorItems.Add(new StatItem { Name = "掉包事件", Description = "時間戳間隔超過 1.5 倍 DATA_RATE 間隔的次數", IsErrorCounter = true });
        MonitorItems.Add(new StatItem { Name = "估計缺少筆數", Description = "依時間間隔推算缺少的 Frame 數", IsErrorCounter = true });
        MonitorItems.Add(new StatItem { Name = "時間倒退 / 重複", Description = "時間戳沒有前進的 Frame 數", IsErrorCounter = true });
        MonitorItems.Add(new StatItem { Name = "ROCOF 告警", Description = "ROCOF 連續 N 點超過門檻的次數", IsErrorCounter = true });
        MonitorItems.Add(new StatItem { Name = "頻率告警", Description = "頻率超出上下限的次數", IsErrorCounter = true });
        StatsSince = "";
    }

    [RelayCommand]
    private void ResetStatistics()
    {
        _statsBaseline = _client?.GetStatistics();
        _monitor.ResetCounters();
        StatsSince = $"自 {DateTime.Now:HH:mm:ss} 起計算";
        UpdateSlow();
    }

    /// <summary>每秒更新：統計、連線時間、重連倒數、錄製狀態。</summary>
    private void UpdateSlow()
    {
        var client = _client;
        if (client is not null)
        {
            var s = client.GetStatistics();
            _lastStats = s;
            for (int i = 0; i < StatDefs.Length; i++)
            {
                long v = StatDefs[i].Get(s) - (_statsBaseline is null ? 0 : StatDefs[i].Get(_statsBaseline));
                StatItems[i].ValueText = v.ToString("N0");
                StatItems[i].IsWarning = StatDefs[i].IsError && v > 0;
            }
        }

        double fps = _monitor.ActualFps;
        SetMonitor(0, client is null ? "--" : $"{fps:F0} fps", false);
        SetMonitor(1, _monitor.GapEvents.ToString("N0"), _monitor.GapEvents > 0);
        SetMonitor(2, _monitor.MissingFrames.ToString("N0"), _monitor.MissingFrames > 0);
        SetMonitor(3, _monitor.BackwardEvents.ToString("N0"), _monitor.BackwardEvents > 0);
        SetMonitor(4, _monitor.RocofAlarms.ToString("N0"), _monitor.RocofAlarms > 0);
        SetMonitor(5, _monitor.FrequencyAlarms.ToString("N0"), _monitor.FrequencyAlarms > 0);

        var cfg = _currentConfig;
        FpsText = client is null ? "--" : cfg is { DataRate: > 0 } ? $"{fps:F0} / {cfg.DataRate} fps" : $"{fps:F0} fps";

        if (_connectedSince is { } since)
        {
            var up = DateTimeOffset.Now - since;
            UptimeText = up.TotalHours >= 1 ? $"{(int)up.TotalHours} 小時 {up.Minutes} 分" : $"{up.Minutes} 分 {up.Seconds} 秒";
        }
        else UptimeText = "--";

        long recv = Interlocked.Read(ref _latestReceivedTicks);
        if (recv > 0 && client is not null)
        {
            var age = DateTime.UtcNow - new DateTime(recv, DateTimeKind.Utc);
            LastFrameAgeText = age.TotalSeconds < 1 ? "< 1 秒前" : $"{age.TotalSeconds:F0} 秒前";
        }
        else LastFrameAgeText = "--";

        if (_state == ConnectionState.Disconnected && _reconnectAt is { } at && IsRunning)
        {
            var left = at - DateTimeOffset.Now;
            StateDetail = left > TimeSpan.Zero
                ? $"{_lastDisconnectReason}　·　{left.TotalSeconds:F0} 秒後自動重連"
                : $"{_lastDisconnectReason}　·　正在重連…";
        }
        else if (_state == ConnectionState.Connected && client is not null && fps > 0)
        {
            StateDetail = $"{Settings.Connection.Host}:{Settings.Connection.Port}  ·  {ProtocolShort}  ·  {fps:F0} fps  ·  已連線 {UptimeText}";
        }

        if (SelectedTabIndex == TabStats && _latestFrame is { } f) UpdateRawFrame(f);

        UpdateRecordingStatus();
        var alarms = _monitor.ActiveAlarms;
        FooterText = $"設定檔：{SettingsPath}";
        ActiveAlarmText = alarms > 0 ? $"⚠ 告警中 {alarms} 項" : "";
    }

    private string _lastDisconnectReason = "";

    private string ProtocolShort => _lastCfgEvent?.ProtocolVersion switch { 1 => "C37.118-2005", 2 => "C37.118.2-2011", _ => "--" };

    [ObservableProperty] private string _activeAlarmText = "";

    private void SetMonitor(int i, string text, bool warn)
    {
        MonitorItems[i].ValueText = text;
        MonitorItems[i].IsWarning = warn;
    }

    private void UpdateRawFrame(DataFrame f)
    {
        var raw = f.Raw;
        var sb = new StringBuilder(raw.Length * 3 + raw.Length / 16 * 8);
        for (int i = 0; i < raw.Length; i += 16)
        {
            sb.Append(i.ToString("X4")).Append("  ");
            for (int j = i; j < Math.Min(i + 16, raw.Length); j++) sb.Append(raw[j].ToString("X2")).Append(' ');
            sb.AppendLine();
        }
        RawFrameHex = sb.ToString();
        RawFrameInfo = $"{raw.Length} bytes · SOC {f.Header.Soc} · FRACSEC 0x{f.Header.FracSec:X8} · IDCODE {f.Header.IdCode} · 版本 {f.Header.Version}";
    }

    [RelayCommand]
    private async Task CopyRawFrameAsync()
    {
        if (Ui is null || _latestFrame is not { } f) return;
        await Ui.CopyTextAsync(Convert.ToHexString(f.Raw));
        AddEvent(EventLevel.Info, "統計", "已複製最新 Data Frame 原始碼（十六進位）");
    }

    // ═════════════════════════════ 事件紀錄 ═════════════════════════════

    private const int MaxEvents = 5000;
    private readonly List<EventLogItem> _allEvents = [];
    public ObservableCollection<EventLogItem> VisibleEvents { get; } = [];
    /// <summary>0 = 全部，1 = 警告以上，2 = 只看告警。</summary>
    [ObservableProperty] private int _eventFilterIndex;
    [ObservableProperty] private string _eventSearch = "";
    [ObservableProperty] private int _alarmCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private string _eventsTabHeader = "事件紀錄";
    [ObservableProperty] private string _lastEventText = "";

    partial void OnEventFilterIndexChanged(int value) => RefilterEvents();
    partial void OnEventSearchChanged(string value) => RefilterEvents();
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == TabEvents) { AlarmCount = 0; WarningCount = 0; UpdateEventsHeader(); }
        if (value == TabStats && _latestFrame is { } f) UpdateRawFrame(f);
        if (value == TabIo || value == TabLive) UpdateLive();
        if (value == TabTrend) TrendVersion++;
    }

    public void AddEvent(EventLevel level, string category, string message)
    {
        var item = new EventLogItem { Time = DateTime.Now, Level = level, Category = category, Message = message };
        _allEvents.Add(item);
        if (_allEvents.Count > MaxEvents) _allEvents.RemoveRange(0, _allEvents.Count - MaxEvents);

        if (Matches(item))
        {
            VisibleEvents.Insert(0, item);
            while (VisibleEvents.Count > MaxEvents) VisibleEvents.RemoveAt(VisibleEvents.Count - 1);
        }

        if (SelectedTabIndex != TabEvents)
        {
            if (level == EventLevel.Alarm) AlarmCount++;
            else if (level is EventLevel.Warning or EventLevel.Error) WarningCount++;
            UpdateEventsHeader();
        }
        if (level != EventLevel.Info) LastEventText = $"{item.TimeText}  {item.LevelText}：{message}";
    }

    private void UpdateEventsHeader()
    {
        int n = AlarmCount + WarningCount;
        EventsTabHeader = n > 0 ? $"事件紀錄 ({n})" : "事件紀錄";
    }

    private bool Matches(EventLogItem e)
    {
        bool levelOk = EventFilterIndex switch
        {
            1 => e.Level != EventLevel.Info,
            2 => e.Level == EventLevel.Alarm,
            _ => true
        };
        if (!levelOk) return false;
        var q = EventSearch?.Trim();
        return string.IsNullOrEmpty(q) ||
               e.Message.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               e.Category.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void RefilterEvents()
    {
        VisibleEvents.Clear();
        for (int i = _allEvents.Count - 1; i >= 0; i--)
            if (Matches(_allEvents[i])) VisibleEvents.Add(_allEvents[i]);
    }

    [RelayCommand]
    private void ClearEvents()
    {
        _allEvents.Clear();
        VisibleEvents.Clear();
        AlarmCount = WarningCount = 0;
        LastEventText = "";
        UpdateEventsHeader();
    }

    [RelayCommand]
    private async Task ExportEventsAsync()
    {
        if (Ui is null) return;
        var path = await Ui.PickSaveFileAsync("匯出事件紀錄", $"pmu_events_{DateTime.Now:yyyyMMdd_HHmmss}.txt", "txt", "文字檔");
        if (path is null) return;
        try
        {
            var lines = new List<string> { "時間\t等級\t類別\t內容" };
            lines.AddRange(_allEvents.Select(e => e.ToString()));
            await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(true));
            AddEvent(EventLevel.Info, "事件", $"已匯出 {_allEvents.Count} 筆事件：{path}");
        }
        catch (Exception ex) { AddEvent(EventLevel.Error, "事件", $"匯出失敗：{ex.Message}"); }
    }

    // ═════════════════════════════ CSV 錄製 ═════════════════════════════

    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _recordingText = "";
    public string RecordButtonText => IsRecording ? "■  停止錄製" : "●  錄製 CSV";
    partial void OnIsRecordingChanged(bool value) => OnPropertyChanged(nameof(RecordButtonText));

    public string RecordingDirectory => ResolveDir(Settings.Recording.Directory);
    public string LogDirectory => ResolveDir(Settings.Logging.Directory);

    private static string ResolveDir(string dir) =>
        Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (IsRecording) { await StopRecordingAsync(); return; }
        if (!IsRunning)
        {
            AddEvent(EventLevel.Info, "錄製", "請先連線再開始錄製");
            return;
        }
        try
        {
            var host = string.Concat(Settings.Connection.Host.Split(Path.GetInvalidFileNameChars()));
            var prefix = $"pmu_{host}_{Settings.Connection.IdCode}";
            _recorder = new CsvRecorder(RecordingDirectory, prefix, Settings.Clone().Recording);
            IsRecording = true;
            AddEvent(EventLevel.Info, "錄製", $"開始錄製到 {RecordingDirectory}" +
                     (Settings.Recording.EveryNthFrame > 1 ? $"（每 {Settings.Recording.EveryNthFrame} 筆存 1 筆）" : ""));
            UpdateRecordingStatus();
        }
        catch (Exception ex)
        {
            AddEvent(EventLevel.Error, "錄製", $"無法開始錄製：{ex.Message}");
        }
    }

    private async Task StopRecordingAsync()
    {
        var rec = _recorder;
        if (rec is null) return;
        _recorder = null;
        await rec.DisposeAsync();
        IsRecording = false;
        _lastRecordingFile = rec.CurrentFile;
        AddEvent(EventLevel.Info, "錄製", $"停止錄製，共 {rec.RowsWritten:N0} 筆" +
                 (rec.Dropped > 0 ? $"，丟棄 {rec.Dropped:N0} 筆" : "") +
                 (string.IsNullOrEmpty(rec.CurrentFile) ? "" : $"：{rec.CurrentFile}"));
        RecordingText = string.IsNullOrEmpty(rec.CurrentFile) ? "" : $"上次錄製：{Path.GetFileName(rec.CurrentFile)}（{rec.RowsWritten:N0} 筆）";
    }

    private string _lastRecordingFile = "";

    private void UpdateRecordingStatus()
    {
        if (_recorder is not { } rec) return;
        var dur = DateTime.Now - rec.StartedAt;
        RecordingText = $"● 錄製中 {(int)dur.TotalMinutes:00}:{dur.Seconds:00}　{rec.RowsWritten:N0} 筆" +
                        (rec.Dropped > 0 ? $"（丟棄 {rec.Dropped:N0}）" : "") +
                        (rec.LastError is { } err ? $"　⚠ {err}" : "") +
                        (string.IsNullOrEmpty(rec.CurrentFile) ? "　等待資料…" : $"　{Path.GetFileName(rec.CurrentFile)}");
    }

    [RelayCommand]
    private void OpenRecordingFolder()
    {
        var file = _recorder?.CurrentFile is { Length: > 0 } cur ? cur : _lastRecordingFile;
        if (!string.IsNullOrEmpty(file) && File.Exists(file)) { Shell.Reveal(file); return; }
        Directory.CreateDirectory(RecordingDirectory);
        Shell.Open(RecordingDirectory);
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(LogDirectory);
        Shell.Open(LogDirectory);
    }

    // ═════════════════════════════ 設定頁 ═════════════════════════════

    [ObservableProperty] private string _settingsStatus = "";
    [ObservableProperty] private bool _trendPaused;

    public string[] AngleUnitChoices { get; } = ["度 (°)", "弧度 (rad)"];
    public string[] TimeZoneChoices { get; } = ["本機時間", "UTC"];
    public string[] ThemeChoices { get; } = ["跟隨系統", "淺色", "深色"];
    public string[] LogLevelChoices { get; } = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];
    public string[] CommandVersionChoices { get; } = ["自動", "2005 (v1)", "2011 (v2)"];
    public string[] EventFilterChoices { get; } = ["全部", "警告以上", "只看告警"];

    public int AngleUnitIndex
    {
        get => (int)Settings.Ui.AngleUnit;
        set { Settings.Ui.AngleUnit = (AngleUnit)Math.Clamp(value, 0, 1); OnPropertyChanged(); UpdateLive(); }
    }
    public int TimeZoneIndex
    {
        get => (int)Settings.Ui.TimeZone;
        set { Settings.Ui.TimeZone = (TimeDisplayZone)Math.Clamp(value, 0, 1); OnPropertyChanged(); UpdateLive(); }
    }
    public int ThemeIndex
    {
        get => (int)Settings.Ui.Theme;
        set { Settings.Ui.Theme = (AppTheme)Math.Clamp(value, 0, 2); OnPropertyChanged(); Ui?.ApplyTheme(Settings.Ui.Theme); }
    }
    public int LogLevelIndex
    {
        get => Math.Min((int)Settings.Logging.MinimumLevel, 5);
        set { Settings.Logging.MinimumLevel = (LogLevelSetting)Math.Clamp(value, 0, 5); OnPropertyChanged(); }
    }
    public int TrendWindowSeconds
    {
        get => Settings.Ui.TrendWindowSeconds;
        set { Settings.Ui.TrendWindowSeconds = Math.Clamp(value, 5, 600); OnPropertyChanged(); TrendVersion++; }
    }
    public double RocofThreshold
    {
        get => Settings.Alarm.RocofThreshold;
        set { if (value > 0) Settings.Alarm.RocofThreshold = value; OnPropertyChanged(); OnPropertyChanged(nameof(RocofLowerLimit)); }
    }
    public double RocofLowerLimit => -Settings.Alarm.RocofThreshold;

    /// <summary>NumericUpDown 使用 decimal；這裡做 int / double 的轉接，讓設定頁可以直接繫結。</summary>
    public decimal? RefreshIntervalMs
    {
        get => Settings.Ui.RefreshIntervalMs;
        set
        {
            Settings.Ui.RefreshIntervalMs = (int)Math.Clamp(value ?? 50, 10, 5000);
            _uiTimer.Interval = TimeSpan.FromMilliseconds(Settings.Ui.RefreshIntervalMs);
            OnPropertyChanged();
        }
    }

    partial void OnSettingsChanged(AppSettings value)
    {
        // 設定物件被整個換掉（重新載入 / 還原預設）：所有繫結重新取值
        OnPropertyChanged(string.Empty);
    }

    private void LoadSettingsFromFile(bool initial)
    {
        try
        {
            var loaded = AppSettings.Load(SettingsPath);
            Settings = loaded;
            LoadTargetFromSettings();
            var errors = loaded.Validate();
            SettingsStatus = errors.Count == 0
                ? (initial ? $"已載入 {SettingsPath}" : $"已重新載入（{DateTime.Now:HH:mm:ss}）")
                : "⚠ 設定檔有問題：" + string.Join("；", errors);
            if (!initial) Ui?.ApplyTheme(Settings.Ui.Theme);
        }
        catch (Exception ex)
        {
            SettingsStatus = $"⚠ 設定檔讀取失敗，改用預設值：{ex.Message}";
            AddEvent(EventLevel.Error, "設定", SettingsStatus);
            Settings = new AppSettings();
            LoadTargetFromSettings();
        }
        _uiTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Settings.Ui.RefreshIntervalMs, 10, 5000));
    }

    private void LoadProfilesOnly()
    {
        SavedConnections.Clear();
        foreach (var p in Settings.SavedConnections) SavedConnections.Add(p);
    }

    private bool TrySaveSettings(bool quiet)
    {
        try
        {
            Settings.Save(SettingsPath);
            if (!quiet) SettingsStatus = $"✓ 已儲存（{DateTime.Now:HH:mm:ss}）  {SettingsPath}";
            return true;
        }
        catch (Exception ex)
        {
            SettingsStatus = $"⚠ 儲存失敗：{ex.Message}";
            AddEvent(EventLevel.Error, "設定", SettingsStatus);
            return false;
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        if (!IsRunning) ApplyTargetToSettings();
        var errors = Settings.Validate();
        if (errors.Count > 0)
        {
            SettingsStatus = "⚠ 無法儲存：" + string.Join("；", errors);
            return;
        }
        if (TrySaveSettings(quiet: false) && IsRunning)
            SettingsStatus += "　（連線、逾時、重連、緩衝、記錄檔設定於下次連線時生效）";
    }

    [RelayCommand]
    private void ReloadSettings()
    {
        if (IsRunning)
        {
            SettingsStatus = "⚠ 連線中無法重新載入，請先中斷連線";
            return;
        }
        LoadSettingsFromFile(initial: false);
    }

    [RelayCommand]
    private void ResetSettingsToDefaults()
    {
        if (IsRunning)
        {
            SettingsStatus = "⚠ 連線中無法還原預設，請先中斷連線";
            return;
        }
        var keep = Settings.SavedConnections;
        var conn = Settings.Connection;
        Settings = new AppSettings { SavedConnections = keep, Connection = conn };
        LoadTargetFromSettings();
        Ui?.ApplyTheme(Settings.Ui.Theme);
        SettingsStatus = "已還原為預設值（保留連線目標與常用連線），按「儲存」寫入設定檔";
    }

    [RelayCommand]
    private void OpenSettingsFile()
    {
        if (!File.Exists(SettingsPath)) TrySaveSettings(quiet: true);
        Shell.Open(SettingsPath);
    }

    [RelayCommand]
    private void OpenAppFolder() => Shell.Open(AppContext.BaseDirectory);

    [RelayCommand]
    private void ClearTrend()
    {
        Trend.Clear();
        TrendVersion++;
    }
}
