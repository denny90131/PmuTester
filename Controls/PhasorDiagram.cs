using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PmuApp.Controls;

/// <summary>
/// 相量圖：0° 在右方、逆時針為正。電壓（實線）與電流（虛線）各自以最大幅值為滿刻度。
/// </summary>
public sealed class PhasorDiagram : Control
{
    public static readonly StyledProperty<IReadOnlyList<PhasorItem>?> ItemsProperty =
        AvaloniaProperty.Register<PhasorDiagram, IReadOnlyList<PhasorItem>?>(nameof(Items));
    public static readonly StyledProperty<int> VersionProperty =
        AvaloniaProperty.Register<PhasorDiagram, int>(nameof(Version));
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<PhasorDiagram, IBrush?>(nameof(GridBrush), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<PhasorDiagram, IBrush?>(nameof(TextBrush), Brushes.Gray);
    public static readonly StyledProperty<int> MagnitudeDecimalsProperty =
        AvaloniaProperty.Register<PhasorDiagram, int>(nameof(MagnitudeDecimals), 1);

    static PhasorDiagram()
    {
        AffectsRender<PhasorDiagram>(ItemsProperty, VersionProperty, GridBrushProperty, TextBrushProperty);
    }

    public IReadOnlyList<PhasorItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public int Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public int MagnitudeDecimals { get => GetValue(MagnitudeDecimalsProperty); set => SetValue(MagnitudeDecimalsProperty, value); }

    private static readonly Typeface Face = new(FontFamily.Default);

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        double size = Math.Min(b.Width, b.Height);
        if (size < 60) return;

        var center = new Point(b.Width / 2, b.Height / 2);
        double r = size / 2 - 30;
        var grid = GridBrush ?? Brushes.Gray;
        var text = TextBrush ?? Brushes.Gray;
        var gridPen = new Pen(grid, 1);
        var gridDash = new Pen(grid, 0.7, new DashStyle([3, 4], 0));

        // 同心圓與軸線
        for (int k = 1; k <= 4; k++)
            ctx.DrawEllipse(null, k == 4 ? gridPen : gridDash, center, r * k / 4, r * k / 4);
        for (int deg = 0; deg < 360; deg += 30)
        {
            double a = deg * Math.PI / 180;
            var p = new Point(center.X + r * Math.Cos(a), center.Y - r * Math.Sin(a));
            ctx.DrawLine(deg % 90 == 0 ? gridPen : gridDash, center, p);
            if (deg % 90 == 0)
            {
                string label = deg switch { 0 => "0°", 90 => "90°", 180 => "180°", _ => "-90°" };
                var ft = Text(label, 11, text);
                var lp = new Point(center.X + (r + 14) * Math.Cos(a) - ft.Width / 2,
                                   center.Y - (r + 14) * Math.Sin(a) - ft.Height / 2);
                ctx.DrawText(ft, lp);
            }
        }

        var items = Items?.Where(i => i.IsVisibleInDiagram && i.HasValue).ToList() ?? [];
        if (items.Count == 0)
        {
            var ft = Text("尚無相量資料", 13, text);
            ctx.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
            return;
        }

        double maxV = items.Where(i => !i.IsCurrent).Select(i => i.Magnitude).DefaultIfEmpty(0).Max();
        double maxI = items.Where(i => i.IsCurrent).Select(i => i.Magnitude).DefaultIfEmpty(0).Max();
        if (maxV <= 0 && maxI <= 0)
        {
            var l1 = Text("幅值皆為 0", 13, text);
            var l2 = Text("PMU 可能未接入電壓 / 電流訊號", 11, text);
            ctx.DrawText(l1, new Point(center.X - l1.Width / 2, center.Y - l1.Height - 2));
            ctx.DrawText(l2, new Point(center.X - l2.Width / 2, center.Y + 2));
            return;
        }
        items = items.Where(i => i.Magnitude > 0).ToList();

        foreach (var item in items)
        {
            double max = item.IsCurrent ? maxI : maxV;
            double len = max > 0 ? item.Magnitude / max * r : 0;
            double a = item.AngleDegrees * Math.PI / 180;
            var tip = new Point(center.X + len * Math.Cos(a), center.Y - len * Math.Sin(a));
            var pen = item.IsCurrent
                ? new Pen(item.Brush, 2, new DashStyle([5, 3], 0), PenLineCap.Round)
                : new Pen(item.Brush, 2.6, null, PenLineCap.Round);
            ctx.DrawLine(pen, center, tip);

            if (len > 8)
            {
                var head = new Pen(item.Brush, 2.4, null, PenLineCap.Round);
                foreach (double off in new[] { 2.7, -2.7 })   // ±155°：箭頭兩翼
                {
                    var wing = new Point(tip.X + 10 * Math.Cos(a + off), tip.Y - 10 * Math.Sin(a + off));
                    ctx.DrawLine(head, tip, wing);
                }
            }

            var name = Text(item.Name, 11, item.Brush);
            double lx = tip.X + (Math.Cos(a) >= 0 ? 6 : -name.Width - 6);
            double ly = tip.Y + (Math.Sin(a) >= 0 ? -name.Height - 2 : 2);
            ctx.DrawText(name, new Point(lx, ly));
        }

        // 滿刻度說明
        string fmt = "F" + Math.Clamp(MagnitudeDecimals, 0, 6);
        var lines = new List<string>();
        if (maxV > 0) lines.Add($"── 電壓 滿刻度 {maxV.ToString(fmt, CultureInfo.InvariantCulture)} V");
        if (maxI > 0) lines.Add($"- - 電流 滿刻度 {maxI.ToString(fmt, CultureInfo.InvariantCulture)} A");
        double y = 2;
        foreach (var l in lines)
        {
            var ft = Text(l, 11, text);
            ctx.DrawText(ft, new Point(4, y));
            y += ft.Height + 1;
        }
    }

    private static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush);
}
