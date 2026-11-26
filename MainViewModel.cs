using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using C37Comm.Client;
using C37Comm.Protocol;

namespace PmuApp;

/// <summary>主畫面：連線管理與 C37Comm 事件處理。即時數值見 MainViewModel.Live.cs，其餘工具見 MainViewModel.Tools.cs。</summary>
public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    public const int TabLive = 0, TabTrend = 1, TabIo = 2, TabCfg = 3, TabStats = 4, TabEvents = 5, TabSettings = 6;

    private PmuClient? _client;
    private int _session;                       // 每次連線遞增；舊連線的事件一律忽略
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _slowTimer;
    private readonly FrameMonitor _monitor;
    private CsvRecorder? _recorder;

    // 派送執行緒寫入、UI 執行緒讀取
    private volatile DataFrame? _latestFrame;
    private long _latestReceivedTicks;
    private volatile ConfigurationFrame? _currentConfig;
    private volatile int _selectedStationForTrend;

    private DateTimeOffset? _connectedSince;
    private DateTimeOffset? _reconnectAt;
    private ConnectionState _state = ConnectionState.Idle;

    public IUiServices? Ui { get; set; }

    /// <summary>目前使用的設定檔完整路徑。</summary>
    public string SettingsPath { get; } = AppSettings.ResolvePath();

    [ObservableProperty] private AppSettings _settings = new();

    public TrendBuffer Trend { get; } = new();

    public MainViewModel()
    {
        _monitor = new FrameMonitor(() => Settings.Alarm);
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _uiTimer.Tick += (_, _) => UpdateLive();
        _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _slowTimer.Tick += (_, _) => UpdateSlow();
        _slowTimer.Start();

        InitStatistics();
        LoadSettingsFromFile(initial: true);
    }

    // ───────────────────────────── 連線列 ─────────────────────────────

    public ObservableCollection<ConnectionProfile> SavedConnections { get; } = [];
    [ObservableProperty] private ConnectionProfile? _selectedProfile;
    [ObservableProperty] private string _hostText = "";
    [ObservableProperty] private decimal? _portValue = 4712;
    [ObservableProperty] private decimal? _idCodeValue = 1;
    /// <summary>0 = 自動，1 = 2005，2 = 2011。</summary>
    [ObservableProperty] private int _commandVersionIndex;
    [ObservableProperty] private string _descriptionText = "";
    [ObservableProperty] private string _inputError = "";

    [ObservableProperty] private string _windowTitle = "PMU Tester";
    [ObservableProperty] private bool _isRunning;            // 已建立 PmuClient（連線中 / 已連線 / 重連中）
    [ObservableProperty] private bool _isBusy;               // 正在啟動或停止
    [ObservableProperty] private string _stateText = "未連線";
    [ObservableProperty] private string _stateDetail = "輸入 PMU 的 IP 與 IDCODE 後按「連線」";
    [ObservableProperty] private bool _isStateConnected;
    [ObservableProperty] private bool _isStateConnecting;
    [ObservableProperty] private bool _isStateDisconnected;
    [ObservableProperty] private bool _isStateIdle = true;
    [ObservableProperty] private int _selectedTabIndex;

    public string ConnectButtonText => IsRunning ? "■  中斷連線" : "▶  連線";
    public bool CanEditTarget => !IsRunning && !IsBusy;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(CanEditTarget));
    }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanEditTarget));

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        if (value is null || IsRunning) return;
        HostText = value.Host;
        PortValue = value.Port;
        IdCodeValue = value.IdCode;
        CommandVersionIndex = value.CommandVersion ?? 0;
        DescriptionText = value.Name;
    }

    partial void OnHostTextChanged(string value) => InputError = "";
    partial void OnPortValueChanged(decimal? value) => InputError = "";
    partial void OnIdCodeValueChanged(decimal? value) => InputError = "";

    [RelayCommand]
    private void RemoveProfile(ConnectionProfile? profile)
    {
        profile ??= SelectedProfile;
        if (profile is null) return;
        Settings.SavedConnections.RemoveAll(p => p.SameTarget(profile));
        SavedConnections.Remove(profile);
        TrySaveSettings(quiet: true);
    }

    /// <summary>由視窗開啟後呼叫：設定檔 AutoConnect = true 時自動連線。</summary>
    public async Task OnWindowOpenedAsync()
    {
        Ui?.ApplyTheme(Settings.Ui.Theme);
        if (Settings.Connection.AutoConnect)
            await ConnectAsync();
    }

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        if (IsBusy) return;
        if (IsRunning) await DisconnectAsync();
        else await ConnectAsync();
    }

    /// <summary>把連線列的輸入寫回設定物件。</summary>
    private void ApplyTargetToSettings()
    {
        var c = Settings.Connection;
        c.Host = (HostText ?? "").Trim();
        c.Port = (int)(PortValue ?? 0);
        c.IdCode = (int)(IdCodeValue ?? -1);
        c.CommandVersion = CommandVersionIndex switch { 1 => 1, 2 => 2, _ => null };
        c.Description = (DescriptionText ?? "").Trim();
    }

    private void LoadTargetFromSettings()
    {
        var c = Settings.Connection;
        HostText = c.Host;
        PortValue = c.Port;
        IdCodeValue = c.IdCode;
        CommandVersionIndex = c.CommandVersion ?? 0;
        DescriptionText = c.Description;
        SavedConnections.Clear();
        foreach (var p in Settings.SavedConnections) SavedConnections.Add(p);
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        var c = Settings.Connection;
        string target = string.IsNullOrWhiteSpace(c.Description) ? $"{c.Host}:{c.Port}" : c.Description;
        WindowTitle = IsRunning ? $"{Settings.Ui.WindowTitle} - {target}" : Settings.Ui.WindowTitle;
    }

    public async Task ConnectAsync()
    {
        if (IsBusy || IsRunning) return;

        ApplyTargetToSettings();
        var errors = Settings.Validate();
        if (errors.Count > 0)
        {
            InputError = string.Join("；", errors);
            SettingsStatus = "⚠ " + InputError;
            return;
        }
        InputError = "";

        PmuClient client;
        try
        {
            var logger = Settings.Logging.Enabled ? new FileLogger(Settings.Logging) : null;
            client = new PmuClient(Settings.ToPmuClientOptions(logger));   // 建構時由 C37Comm 驗證參數
        }
        catch (Exception ex)
        {
            InputError = ex.Message;
            return;
        }

        // 記住這次的連線目標（下次開啟自動帶入，並加入常用連線）
        Settings.RememberCurrentConnection();
        LoadProfilesOnly();
        TrySaveSettings(quiet: true);

        IsBusy = true;
        int session = Interlocked.Increment(ref _session);
        _client = client;
        IsRunning = true;
        ResetSessionState();
        UpdateWindowTitle();
        SetState(ConnectionState.Connecting, $"正在連線 {Settings.Connection.Host}:{Settings.Connection.Port} …");
        AddEvent(EventLevel.Info, "連線", $"開始連線 {Settings.Connection.Host}:{Settings.Connection.Port}，IDCODE {Settings.Connection.IdCode}，" +
                                         $"指令版本 {CommandVersionText(Settings.Connection.CommandVersion)}");

        client.ConnectionChanged += (_, e) => OnConnectionChanged(session, e);
        client.ConfigurationChanged += (_, e) => OnConfigurationChanged(session, e);
        client.FrameReceived += (_, e) => OnFrameReceived(session, e);

        _uiTimer.Interval = TimeSpan.FromMilliseconds(Settings.Ui.RefreshIntervalMs);
        _uiTimer.Start();
        try
        {
            await client.StartAsync();
        }
        catch (Exception ex)
        {
            AddEvent(EventLevel.Error, "連線", $"啟動失敗：{ex.Message}");
            IsBusy = false;
            await DisconnectAsync();
            return;
        }
        IsBusy = false;
    }

    public async Task DisconnectAsync()
    {
        var client = _client;
        if (client is null) { IsRunning = false; return; }

        IsBusy = true;
        StateDetail = "正在送出「關閉資料流」並中斷連線…";
        await StopRecordingAsync();
        try
        {
            await client.StopAsync();
            await client.DisposeAsync();
        }
        catch (Exception ex)
        {
            AddEvent(EventLevel.Warning, "連線", $"停止時發生例外：{ex.Message}");
        }
        _client = null;
        Interlocked.Increment(ref _session);
        _uiTimer.Stop();
        IsRunning = false;
        IsBusy = false;
        _connectedSince = null;
        _reconnectAt = null;
        SetState(ConnectionState.Stopped, "已中斷連線。畫面保留最後一筆資料。");
        UpdateWindowTitle();
    }

    private void ResetSessionState()
    {
        _latestFrame = null;
        _currentConfig = null;
        _connectedSince = null;
        _reconnectAt = null;
        _statsBaseline = null;
        _monitor.ResetCounters();
        Trend.Clear();
        StationList.Clear();
        PhasorList.Clear();
        AnalogList.Clear();
        DigitalList.Clear();
        StationSummaries.Clear();
        ClearLiveValues();
        ClearCfgView();
    }

    // ───────────────────────────── C37Comm 事件（派送執行緒） ─────────────────────────────

    private void OnConnectionChanged(int session, ConnectionChangedEventArgs e)
    {
        if (session != _session) return;
        if (e.State is ConnectionState.Connected or ConnectionState.Disconnected) _monitor.Reset();

        Dispatcher.UIThread.Post(() =>
        {
            if (session != _session) return;
            switch (e.State)
            {
                case ConnectionState.Connecting:
                    _reconnectAt = null;
                    SetState(e.State, $"正在連線 {Settings.Connection.Host}:{Settings.Connection.Port} …");
                    break;
                case ConnectionState.Connected:
                    _connectedSince = e.Timestamp;
                    _reconnectAt = null;
                    SetState(e.State, "TCP 已連線，正在取得設定檔 (CFG-2) 並開啟資料流…");
                    AddEvent(EventLevel.Info, "連線", "已連線");
                    break;
                case ConnectionState.Disconnected:
                    _connectedSince = null;
                    _reconnectAt = e.NextReconnectDelay is { } d ? DateTimeOffset.Now + d : null;
                    _lastDisconnectReason = string.IsNullOrWhiteSpace(e.Reason) ? "已斷線" : e.Reason;
                    SetState(e.State, $"{e.Reason}");
                    AddEvent(EventLevel.Warning, "連線",
                        $"斷線：{e.Reason}" + (e.NextReconnectDelay is { } nd ? $"，{nd.TotalSeconds:F1} 秒後自動重連" : ""));
                    break;
                case ConnectionState.Stopped:
                    AddEvent(EventLevel.Info, "連線", "已停止");
                    break;
            }
        });
    }

    private void OnConfigurationChanged(int session, ConfigurationChangedEventArgs e)
    {
        if (session != _session) return;
        _currentConfig = e.Current;
        _monitor.OnConfiguration(e.Current);
        Trend.Clear();

        Dispatcher.UIThread.Post(() =>
        {
            if (session != _session) return;
            ApplyConfiguration(e);
        });
    }

    private readonly List<MonitorEvent> _pendingMonitorEvents = new();

    private void OnFrameReceived(int session, FrameReceivedEventArgs e)
    {
        if (session != _session) return;
        var frame = e.Frame;
        _latestFrame = frame;
        Interlocked.Exchange(ref _latestReceivedTicks, e.ReceivedAt.UtcTicks);

        int st = _selectedStationForTrend;
        if (st >= 0 && st < frame.Stations.Count)
            Trend.Add(frame.Timestamp, frame.Stations[st].Frequency, frame.Stations[st].Rocof);

        _recorder?.TryEnqueue(frame);

        _pendingMonitorEvents.Clear();
        _monitor.OnFrame(frame, e.ReceivedAt, _pendingMonitorEvents);
        if (_pendingMonitorEvents.Count > 0)
        {
            var batch = _pendingMonitorEvents.ToArray();
            Dispatcher.UIThread.Post(() =>
            {
                if (session != _session) return;
                foreach (var ev in batch) AddEvent(ev.Level, ev.Category, ev.Message);
            });
        }
    }

    // ───────────────────────────── 狀態顯示 ─────────────────────────────

    private void SetState(ConnectionState state, string detail)
    {
        _state = state;
        StateText = state switch
        {
            ConnectionState.Connecting => "連線中",
            ConnectionState.Connected => "已連線",
            ConnectionState.Disconnected => "已斷線",
            ConnectionState.Stopped => "未連線",
            _ => "未連線"
        };
        StateDetail = detail;
        IsStateConnected = state == ConnectionState.Connected;
        IsStateConnecting = state == ConnectionState.Connecting;
        IsStateDisconnected = state == ConnectionState.Disconnected;
        IsStateIdle = state is ConnectionState.Idle or ConnectionState.Stopped;
    }

    private static string CommandVersionText(int? v) => v switch { 1 => "2005（固定）", 2 => "2011（固定）", _ => "自動" };

    public async ValueTask DisposeAsync()
    {
        _uiTimer.Stop();
        _slowTimer.Stop();
        await StopRecordingAsync();
        if (_client != null)
        {
            Interlocked.Increment(ref _session);
            await _client.DisposeAsync();
            _client = null;
        }
    }
}
