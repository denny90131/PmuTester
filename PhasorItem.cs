using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PmuApp;

/// <summary>單一相量的顯示資料（名稱 / 單位來自 CFG-2，數值來自 Data Frame）。</summary>
public partial class PhasorItem : ObservableObject
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    /// <summary>"V" 或 "A"（CFG-2 PHUNIT 類型）。</summary>
    public string Unit { get; init; } = "";
    public bool IsCurrent { get; init; }
    public string KindText => IsCurrent ? "電流" : "電壓";
    /// <summary>相量圖與表格共用的顏色。</summary>
    public IBrush Brush { get; init; } = Brushes.Gray;
    public Color Color { get; init; } = Colors.Gray;

    [ObservableProperty] private bool _hasValue;
    [ObservableProperty] private double _magnitude;
    [ObservableProperty] private double _angleDegrees;
    [ObservableProperty] private string _magnitudeText = "--";
    [ObservableProperty] private string _angleText = "--";
    [ObservableProperty] private string _realText = "--";
    [ObservableProperty] private string _imaginaryText = "--";
    [ObservableProperty] private bool _isVisibleInDiagram = true;

    // 常見的三相配色：A 紅、B 黃、C 藍；其他通道依序取色盤
    private static readonly Color[] Palette =
    [
        Color.Parse("#2A78D6"), Color.Parse("#E07B39"), Color.Parse("#2E9E6A"), Color.Parse("#9A5BD1"),
        Color.Parse("#C9478F"), Color.Parse("#3BA7B5"), Color.Parse("#8C8C2B"), Color.Parse("#6F6F6F")
    ];
    private static readonly Color PhaseA = Color.Parse("#D64545");
    private static readonly Color PhaseB = Color.Parse("#D9A400");
    private static readonly Color PhaseC = Color.Parse("#2A78D6");

    public static Color PickColor(string name, int index)
    {
        return PhaseLetter(name) switch
        {
            'A' or 'R' => PhaseA,
            'B' or 'S' => PhaseB,
            'C' or 'T' => PhaseC,
            _ => Palette[index % Palette.Length]
        };
    }

    /// <summary>從名稱結尾判斷相別："…_VA"、"IA"、"Bus A"、"V-R" → 'A'/'R'；判斷不出回傳 '\0'。</summary>
    private static char PhaseLetter(string name)
    {
        var n = name.Trim().ToUpperInvariant();
        if (n.Length == 0) return '\0';
        char last = n[^1];
        if ("ABCRST".IndexOf(last) < 0) return '\0';
        if (n.Length == 1) return last;
        char prev = n[^2];
        if (!char.IsLetter(prev)) return last;                          // "Bus_A"、"V-R"
        if (prev is 'V' or 'I' or 'U' && (n.Length == 2 || !char.IsLetter(n[^3])))
            return last;                                                 // "VA"、"_IB"、"03 UC"
        return '\0';
    }

    public static PhasorItem Create(int index, string name, bool isCurrent)
    {
        var color = PickColor(name, index);
        return new PhasorItem
        {
            Index = index,
            Name = name,
            IsCurrent = isCurrent,
            Unit = isCurrent ? "A" : "V",
            Color = color,
            Brush = new SolidColorBrush(color)
        };
    }

    public void Clear()
    {
        HasValue = false;
        MagnitudeText = AngleText = RealText = ImaginaryText = "--";
    }
}

/// <summary>類比通道（原始值，不換算）。</summary>
public partial class AnalogItem : ObservableObject
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string UnitText { get; init; } = "";
    [ObservableProperty] private string _valueText = "--";
}

/// <summary>DI 點。</summary>
public partial class DigitalItem : ObservableObject
{
    public int Word { get; init; }
    public int Bit { get; init; }
    public string Name { get; init; } = "";
    public string Position => $"W{Word}.{Bit:00}";
    [ObservableProperty] private bool _value;
    [ObservableProperty] private bool _valid;
    [ObservableProperty] private bool _abnormal;
    [ObservableProperty] private string _stateText = "--";
}

/// <summary>多站台（PDC）時的各站摘要。</summary>
public partial class StationSummaryItem : ObservableObject
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public ushort IdCode { get; init; }
    [ObservableProperty] private string _frequencyText = "--";
    [ObservableProperty] private string _rocofText = "--";
    [ObservableProperty] private bool _isValid;
    [ObservableProperty] private bool _syncLost;
    [ObservableProperty] private string _statText = "--";
}

public enum EventLevel { Info, Warning, Error, Alarm }

/// <summary>事件紀錄的一筆。</summary>
public sealed class EventLogItem
{
    public DateTime Time { get; init; } = DateTime.Now;
    public EventLevel Level { get; init; }
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public string TimeText => Time.ToString("HH:mm:ss.fff");
    public string LevelText => Level switch
    {
        EventLevel.Info => "資訊",
        EventLevel.Warning => "警告",
        EventLevel.Error => "錯誤",
        EventLevel.Alarm => "告警",
        _ => Level.ToString()
    };
    public bool IsInfo => Level == EventLevel.Info;
    public bool IsWarning => Level == EventLevel.Warning;
    public bool IsError => Level == EventLevel.Error;
    public bool IsAlarm => Level == EventLevel.Alarm;
    public bool IsBadLevel => Level is EventLevel.Error or EventLevel.Alarm;
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff}\t{LevelText}\t{Category}\t{Message}";
}

/// <summary>統計表的一列。</summary>
public partial class StatItem : ObservableObject
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>此計數非 0 代表有問題（顯示為警示色）。</summary>
    public bool IsErrorCounter { get; init; }
    [ObservableProperty] private string _valueText = "0";
    [ObservableProperty] private bool _isWarning;
}

/// <summary>CFG-2 差異的一筆。</summary>
public sealed class CfgDiffItem
{
    public DateTime Time { get; init; }
    public string Kind { get; init; } = "";
    public string Station { get; init; } = "";
    public string Item { get; init; } = "";
    public string OldValue { get; init; } = "";
    public string NewValue { get; init; } = "";
}

/// <summary>CFG-2 頁的站台說明。</summary>
public sealed class CfgStationItem
{
    public string Header { get; init; } = "";
    public string Summary { get; init; } = "";
    public System.Collections.Generic.IReadOnlyList<string> Channels { get; init; } = [];
}

/// <summary>STAT 拆解的一項。</summary>
public partial class StatFlagItem : ObservableObject
{
    public string Name { get; init; } = "";
    [ObservableProperty] private string _valueText = "--";
    [ObservableProperty] private bool _isBad;
    [ObservableProperty] private bool _isNotice;
}
