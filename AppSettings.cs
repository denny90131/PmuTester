using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using C37Comm.Client;

namespace PmuApp;

/// <summary>
/// 應用程式設定（對應 appsettings.json）。
/// 程式內「設定」分頁修改後按「儲存」會寫回同一個檔案（註解由程式重新產生）；
/// 也可以直接編輯檔案，再按「重新載入」。
/// </summary>
public sealed class AppSettings
{
    public const string DefaultFileName = "appsettings.json";

    [Description("PMU 連線目標（上方連線列的內容，按「連線」時會自動儲存）")]
    public ConnectionSettings Connection { get; set; } = new();
    [Description("常用連線（成功連線後自動加入，最多 10 筆，可在上方「常用連線」下拉選單快速切換）")]
    public List<ConnectionProfile> SavedConnections { get; set; } = [];
    [Description("逾時設定")]
    public TimeoutSettings Timeouts { get; set; } = new();
    [Description("斷線自動重連（指數退避）")]
    public ReconnectSettings Reconnect { get; set; } = new();
    [Description("接收緩衝")]
    public BufferSettings Buffer { get; set; } = new();
    [Description("畫面顯示（修改後立即生效）")]
    public UiSettings Ui { get; set; } = new();
    [Description("告警判斷（修改後立即生效）")]
    public AlarmSettings Alarm { get; set; } = new();
    [Description("CSV 錄製")]
    public RecordingSettings Recording { get; set; } = new();
    [Description("診斷記錄（C37Comm 內部訊息寫入檔案，下次連線時生效）")]
    public LoggingSettings Logging { get; set; } = new();
    [Description("【預留】資料庫儲存（目前程式尚未實作寫入，欄位先行保留）")]
    public DatabaseSettings Database { get; set; } = new();

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,   // 允許設定檔內寫 // 註解
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文不轉成 \uXXXX
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>設定檔路徑：命令列 <c>--config &lt;路徑&gt;</c> 優先，否則為執行檔旁的 appsettings.json。</summary>
    public static string ResolvePath()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] is "--config" or "-c")
                return Path.GetFullPath(args[i + 1]);
        }
        return Path.Combine(AppContext.BaseDirectory, DefaultFileName);
    }

    /// <summary>讀取設定檔；檔案不存在時以預設值建立一份新的（含註解）。</summary>
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new AppSettings();
            defaults.Save(path);
            return defaults;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
               ?? throw new InvalidDataException("設定檔內容為空");
    }

    /// <summary>深層複製（透過 JSON）。用於「取消修改」與背景執行緒安全讀取。</summary>
    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    /// <summary>寫回設定檔，每個欄位附上中文註解（來自 <see cref="DescriptionAttribute"/>）。</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, CommentedJsonWriter.Write(this), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// 驗證設定：連線參數交給 C37Comm 的 <see cref="PmuClientOptions.Validate"/>，
    /// 其餘為應用程式層級的檢查。
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Connection.Host)) errors.Add("請輸入 PMU 的 IP 或主機名稱");
        if (Connection.CommandVersion is not (null or 1 or 2)) errors.Add("指令版本必須為 自動、2005 或 2011");
        try { errors.AddRange(ToPmuClientOptions(null).Validate()); }
        catch (Exception ex) { errors.Add(ex.Message); }

        if (Ui.RefreshIntervalMs is < 10 or > 5000) errors.Add("畫面更新週期必須介於 10–5000 ms");
        if (Ui.MagnitudeDecimals is < 0 or > 10) errors.Add("振幅小數位數必須介於 0–10");
        if (Ui.AngleDecimals is < 0 or > 10) errors.Add("相角小數位數必須介於 0–10");
        if (Ui.FrequencyDecimals is < 0 or > 10) errors.Add("頻率小數位數必須介於 0–10");
        if (Ui.TrendWindowSeconds is < 5 or > 600) errors.Add("趨勢圖時間長度必須介於 5–600 秒");
        try { _ = DateTime.Now.ToString(Ui.TimestampFormat); }
        catch (FormatException) { errors.Add($"時間戳格式錯誤：{Ui.TimestampFormat}"); }
        if (Alarm.RocofConsecutivePoints < 1) errors.Add("ROCOF 連續點數至少 1");
        if (Alarm.RocofThreshold <= 0) errors.Add("ROCOF 門檻必須大於 0");
        if (Alarm.FrequencyLowLimit >= Alarm.FrequencyHighLimit) errors.Add("頻率下限必須小於上限");
        if (Recording.EveryNthFrame < 1) errors.Add("錄製降頻 N 至少為 1");
        if (string.IsNullOrWhiteSpace(Recording.Directory)) errors.Add("錄製資料夾不可為空");
        return errors.Distinct().ToList();
    }

    /// <summary>轉換為 C37Comm 的 <see cref="PmuClientOptions"/>。</summary>
    public PmuClientOptions ToPmuClientOptions(Microsoft.Extensions.Logging.ILogger? logger) => new()
    {
        Host = Connection.Host.Trim(),
        Port = Connection.Port,
        IdCode = Connection.IdCode,
        CommandVersion = Connection.CommandVersion,
        ConnectTimeout = TimeSpan.FromMilliseconds(Timeouts.ConnectTimeoutMs),
        NoDataTimeout = TimeSpan.FromMilliseconds(Timeouts.NoDataTimeoutMs),
        CfgRequestTimeout = TimeSpan.FromMilliseconds(Timeouts.CfgRequestTimeoutMs),
        CfgRequestRetries = Timeouts.CfgRequestRetries,
        InitialReconnectInterval = TimeSpan.FromMilliseconds(Reconnect.InitialIntervalMs),
        ReconnectBackoffMultiplier = Reconnect.BackoffMultiplier,
        MaxReconnectInterval = TimeSpan.FromMilliseconds(Reconnect.MaxIntervalMs),
        MaxPendingFrames = Buffer.MaxPendingFrames,
        Logger = logger
    };

    /// <summary>把目前連線加入（或移到）常用連線最前面。</summary>
    public void RememberCurrentConnection(int max = 10)
    {
        var c = Connection;
        var profile = new ConnectionProfile
        {
            Name = c.Description,
            Host = c.Host.Trim(),
            Port = c.Port,
            IdCode = c.IdCode,
            CommandVersion = c.CommandVersion
        };
        SavedConnections.RemoveAll(p => p.SameTarget(profile));
        SavedConnections.Insert(0, profile);
        if (SavedConnections.Count > max) SavedConnections.RemoveRange(max, SavedConnections.Count - max);
    }
}

/// <summary>PMU 連線目標。</summary>
public sealed class ConnectionSettings
{
    [Description("PMU / PDC 的 IP 或主機名稱")]
    public string Host { get; set; } = "127.0.0.1";
    [Description("TCP 埠號（C37.118 慣用 4712）")]
    public int Port { get; set; } = 4712;
    [Description("Stream IDCODE（0–65535），須與 PMU CFG-2 一致")]
    public int IdCode { get; set; } = 235;
    [Description("指令版本：null = 自動（CFG-2 逾時會在 2011／2005 間切換）；1 = 固定 2005；2 = 固定 2011")]
    public int? CommandVersion { get; set; }
    [Description("true = 開啟程式後自動連線；false = 手動按「連線」")]
    public bool AutoConnect { get; set; } = false;
    [Description("連線名稱，會顯示在視窗標題與常用連線清單，例如 \"變電所A-主PMU\"")]
    public string Description { get; set; } = "";
}

/// <summary>常用連線的一筆紀錄。</summary>
public sealed class ConnectionProfile
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 4712;
    public int IdCode { get; set; }
    public int? CommandVersion { get; set; }

    public bool SameTarget(ConnectionProfile other) =>
        string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase) && Port == other.Port && IdCode == other.IdCode;

    [JsonIgnore]
    public string DisplayText
    {
        get
        {
            var target = $"{Host}:{Port}  #{IdCode}";
            var ver = CommandVersion switch { 1 => "  ·2005", 2 => "  ·2011", _ => "" };
            return string.IsNullOrWhiteSpace(Name) ? target + ver : $"{Name}  ({target}){ver}";
        }
    }

    public override string ToString() => DisplayText;
}

/// <summary>逾時與 CFG 請求設定（毫秒）。</summary>
public sealed class TimeoutSettings
{
    [Description("TCP 連線逾時")]
    public int ConnectTimeoutMs { get; set; } = 3000;
    [Description("開啟資料流後超過此時間沒收到 Data Frame 視為斷線")]
    public int NoDataTimeoutMs { get; set; } = 5000;
    [Description("請求 CFG-2 的逾時")]
    public int CfgRequestTimeoutMs { get; set; } = 3000;
    [Description("CFG-2 逾時後重送次數，全部失敗即斷線")]
    public int CfgRequestRetries { get; set; } = 3;
}

/// <summary>斷線自動重連（指數退避）。</summary>
public sealed class ReconnectSettings
{
    [Description("第一次重連前等待")]
    public int InitialIntervalMs { get; set; } = 1000;
    [Description("每次失敗後等待時間 × 倍率")]
    public double BackoffMultiplier { get; set; } = 1.5;
    [Description("等待時間上限")]
    public int MaxIntervalMs { get; set; } = 5000;
}

/// <summary>接收緩衝。</summary>
public sealed class BufferSettings
{
    [Description("未派送 Data Frame 暫存上限，滿了丟棄最舊（60 fps 時 1024 ≈ 17 秒）")]
    public int MaxPendingFrames { get; set; } = 1024;
}

public enum TimeDisplayZone { Local, Utc }
public enum AngleUnit { Degree, Radian }
public enum AppTheme { System, Light, Dark }

/// <summary>畫面顯示。</summary>
public sealed class UiSettings
{
    [Description("視窗標題")]
    public string WindowTitle { get; set; } = "PMU Tester";
    [Description("佈景主題：System = 跟隨 Windows；Light = 淺色；Dark = 深色")]
    public AppTheme Theme { get; set; } = AppTheme.System;
    [Description("畫面更新週期（毫秒，不影響接收速率）")]
    public int RefreshIntervalMs { get; set; } = 50;
    [Description("連線後預設選取第幾個站台（從 0 開始）")]
    public int DefaultStationIndex { get; set; } = 0;
    [Description("時間戳顯示時區：Local = 本機時間；Utc = UTC")]
    public TimeDisplayZone TimeZone { get; set; } = TimeDisplayZone.Local;
    [Description("時間戳格式（.NET 自訂日期格式），例：\"yyyy-MM-dd HH:mm:ss.ffffff\"")]
    public string TimestampFormat { get; set; } = "HH:mm:ss.fff";
    [Description("相角單位：Degree = 度；Radian = 弧度")]
    public AngleUnit AngleUnit { get; set; } = AngleUnit.Degree;
    [Description("振幅小數位數")]
    public int MagnitudeDecimals { get; set; } = 2;
    [Description("相角小數位數")]
    public int AngleDecimals { get; set; } = 2;
    [Description("頻率 / ROCOF 小數位數")]
    public int FrequencyDecimals { get; set; } = 4;
    [Description("趨勢圖顯示最近幾秒（5–600）")]
    public int TrendWindowSeconds { get; set; } = 30;
}

/// <summary>告警判斷（全部站台都會判斷，結果寫入事件紀錄）。</summary>
public sealed class AlarmSettings
{
    [Description("是否啟用告警判斷")]
    public bool Enabled { get; set; } = true;
    [Description("ROCOF 門檻（Hz/s，取絕對值比較）")]
    public double RocofThreshold { get; set; } = 0.5;
    [Description("連續幾個有效點超過門檻才觸發")]
    public int RocofConsecutivePoints { get; set; } = 3;
    [Description("頻率上限（Hz），超過即告警")]
    public double FrequencyHighLimit { get; set; } = 60.5;
    [Description("頻率下限（Hz），低於即告警")]
    public double FrequencyLowLimit { get; set; } = 59.5;
    [Description("偵測掉包與時間倒退（依 CFG-2 的 DATA_RATE 判斷）")]
    public bool DetectGaps { get; set; } = true;
}

/// <summary>CSV 錄製。</summary>
public sealed class RecordingSettings
{
    [Description("錄製檔資料夾（相對路徑以執行檔資料夾為準）")]
    public string Directory { get; set; } = "records";
    [Description("降頻：每 N 個 Frame 存一筆（1 = 全存；60 fps 設 60 ≈ 每秒一筆）")]
    public int EveryNthFrame { get; set; } = 1;
    [Description("只錄 IsValid = true 的資料")]
    public bool OnlyValid { get; set; } = false;
    [Description("相量欄位同時輸出實部 / 虛部")]
    public bool IncludeRectangular { get; set; } = false;
    [Description("時間欄位使用 UTC（false = 本機時間）")]
    public bool UseUtc { get; set; } = true;
}

public enum LogLevelSetting { Trace, Debug, Information, Warning, Error, Critical, None }

/// <summary>診斷記錄（C37Comm 內部訊息寫入檔案）。</summary>
public sealed class LoggingSettings
{
    [Description("是否寫入記錄檔")]
    public bool Enabled { get; set; } = false;
    [Description("最低記錄等級：Trace / Debug / Information / Warning / Error / Critical")]
    public LogLevelSetting MinimumLevel { get; set; } = LogLevelSetting.Information;
    [Description("記錄資料夾（相對路徑以執行檔資料夾為準）")]
    public string Directory { get; set; } = "logs";
    [Description("檔名樣板，{date} → yyyyMMdd")]
    public string FileNamePattern { get; set; } = "pmu_{date}.log";
    [Description("超過天數的記錄檔於連線時刪除；0 = 永久保留")]
    public int RetentionDays { get; set; } = 30;
}

public enum DatabaseProvider { None, SqlServer, PostgreSql, TimescaleDb, MySql, Sqlite, InfluxDb }

/// <summary>【預留】資料庫儲存設定。目前程式尚未實作寫入，欄位先行保留供日後使用。</summary>
public sealed class DatabaseSettings
{
    [Description("是否啟用資料庫儲存")]
    public bool Enabled { get; set; } = false;
    [Description("None / SqlServer / PostgreSql / TimescaleDb / MySql / Sqlite / InfluxDb")]
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.None;
    [Description("完整連線字串；有填則優先使用，忽略下方個別欄位")]
    public string ConnectionString { get; set; } = "";
    public string Server { get; set; } = "localhost";
    [Description("0 = 該資料庫預設埠")]
    public int Port { get; set; } = 0;
    [Description("Sqlite 時為檔案路徑")]
    public string DatabaseName { get; set; } = "pmu";
    public string Username { get; set; } = "";
    [Description("建議改用 PasswordEnvironmentVariable")]
    public string Password { get; set; } = "";
    [Description("有填則改從此環境變數讀密碼，例如 \"PMU_DB_PASSWORD\"")]
    public string PasswordEnvironmentVariable { get; set; } = "";
    [Description("SQL Server Windows 驗證")]
    public bool IntegratedSecurity { get; set; } = false;
    public bool UseSsl { get; set; } = false;
    public bool TrustServerCertificate { get; set; } = false;
    public int CommandTimeoutSec { get; set; } = 30;
    public int MaxPoolSize { get; set; } = 20;
    [Description("PostgreSQL 一般為 \"public\"")]
    public string Schema { get; set; } = "dbo";
    public string TablePrefix { get; set; } = "pmu_";
    public bool AutoCreateTables { get; set; } = true;
    public string InfluxOrganization { get; set; } = "";
    public string InfluxBucket { get; set; } = "pmu";
    public string InfluxToken { get; set; } = "";
    [Description("寫入策略")]
    public DatabaseWriteSettings Write { get; set; } = new();
    [Description("資料保留")]
    public DatabaseRetentionSettings Retention { get; set; } = new();
}

public sealed class DatabaseWriteSettings
{
    public int BatchSize { get; set; } = 500;
    public int FlushIntervalMs { get; set; } = 1000;
    public int StoreEveryNthFrame { get; set; } = 1;
    public int MaxQueueSize { get; set; } = 100_000;
    public int RetryCount { get; set; } = 3;
    public int RetryIntervalMs { get; set; } = 2000;
    public bool StoreOnlyValid { get; set; } = false;
    public List<int> StationIdCodes { get; set; } = [];
    public bool StoreFrequency { get; set; } = true;
    public bool StoreRocof { get; set; } = true;
    public bool StorePhasors { get; set; } = true;
    public bool StoreAnalogs { get; set; } = true;
    public bool StoreDigitals { get; set; } = true;
    public bool StoreStatusWord { get; set; } = true;
    public bool StoreConfigurationFrames { get; set; } = true;
    public bool StoreConnectionEvents { get; set; } = true;
    public bool UseUtcTimestamp { get; set; } = true;
}

public sealed class DatabaseRetentionSettings
{
    public int RawDataDays { get; set; } = 90;
    public int EventDays { get; set; } = 365;
    public int CleanupIntervalHours { get; set; } = 24;
}

/// <summary>把設定物件寫成「每個欄位帶 // 註解」的 JSON。</summary>
internal static class CommentedJsonWriter
{
    public static string Write(AppSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// ==========================================================================");
        sb.AppendLine("//  PMU Tester 設定檔");
        sb.AppendLine("//  - 放在執行檔旁（與 PmuApp.exe 同資料夾）；也可用命令列指定：PmuApp.exe --config D:\\path\\my.json");
        sb.AppendLine("//  - 程式內「設定」分頁按「儲存」會覆寫本檔（註解自動重新產生）");
        sb.AppendLine("//  - 手動修改後，在程式內按「重新載入設定檔」即可套用");
        sb.AppendLine("//  - 時間單位為毫秒 (Ms) 或秒 (Sec)，以欄位名稱結尾為準");
        sb.AppendLine("// ==========================================================================");
        WriteObject(sb, settings, 0);
        sb.AppendLine();
        return sb.ToString();
    }

    private static void WriteObject(StringBuilder sb, object obj, int indent)
    {
        var pad = new string(' ', indent * 2);
        var inner = new string(' ', (indent + 1) * 2);
        sb.AppendLine("{");
        var props = obj.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .ToList();

        for (int i = 0; i < props.Count; i++)
        {
            var p = props[i];
            var value = p.GetValue(obj);
            var comment = p.GetCustomAttribute<DescriptionAttribute>()?.Description;
            var comma = i < props.Count - 1 ? "," : "";

            if (value is not null && IsSection(p.PropertyType))
            {
                if (indent == 0) sb.AppendLine();
                if (comment is not null) sb.AppendLine($"{inner}// ---- {comment} ----");
                sb.Append($"{inner}\"{p.Name}\": ");
                WriteObject(sb, value, indent + 1);
                sb.Length -= Environment.NewLine.Length;   // 去掉 '}' 後的換行，補上逗號
                sb.AppendLine(comma);
            }
            else
            {
                var json = JsonSerializer.Serialize(value, p.PropertyType, AppSettings.JsonOptions);
                if (value is IList)
                    json = json.Replace("\n", "\n" + inner);
                if (value is IList && indent == 0) sb.AppendLine().AppendLine(comment is null ? "" : $"{inner}// ---- {comment} ----");
                var line = $"{inner}\"{p.Name}\": {json}{comma}";
                if (comment is not null && !(value is IList && indent == 0))
                    line = line.PadRight(Math.Max(line.Length + 2, 44)) + "// " + comment;
                sb.AppendLine(line);
            }
        }
        sb.AppendLine($"{pad}}}");
    }

    private static bool IsSection(Type t) =>
        t.IsClass && t != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(t);
}
