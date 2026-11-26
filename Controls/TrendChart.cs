using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PmuApp.Controls;

/// <summary>
/// 簡易即時趨勢圖：讀取 <see cref="TrendBuffer"/> 最近 N 秒的資料畫成折線。
/// 點數多於像素時，每個像素欄取最小 / 最大值，畫面不失真也不卡。
/// </summary>
public sealed class TrendChart : Control
{
    public static readonly StyledProperty<TrendBuffer?> BufferProperty =
        AvaloniaProperty.Register<TrendChart, TrendBuffer?>(nameof(Buffer));
    public static readonly StyledProperty<int> SeriesProperty =
        AvaloniaProperty.Register<TrendChart, int>(nameof(Series));
    public static readonly StyledProperty<int> WindowSecondsProperty =
        AvaloniaProperty.Register<TrendChart, int>(nameof(WindowSeconds), 30);
    public static readonly StyledProperty<int> VersionProperty =
        AvaloniaProperty.Register<TrendChart, int>(nameof(Version));
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<TrendChart, string>(nameof(Title), "");
    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<TrendChart, string>(nameof(Unit), "");
    public static readonly StyledProperty<int> DecimalsProperty =
        AvaloniaProperty.Register<TrendChart, int>(nameof(Decimals), 4);
    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<TrendChart, IBrush?>(nameof(LineBrush), Brushes.DodgerBlue);
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<TrendChart, IBrush?>(nameof(GridBrush), Brushes.LightGray);
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<TrendChart, IBrush?>(nameof(TextBrush), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> LimitBrushProperty =
        AvaloniaProperty.Register<TrendChart, IBrush?>(nameof(LimitBrush), Brushes.IndianRed);
    public static readonly StyledProperty<double> UpperLimitProperty =
        AvaloniaProperty.Register<TrendChart, double>(nameof(UpperLimit), double.NaN);
    public static readonly StyledProperty<double> LowerLimitProperty =
        AvaloniaProperty.Register<TrendChart, double>(nameof(LowerLimit), double.NaN);
    public static readonly StyledProperty<bool> ShowLimitsProperty =
        AvaloniaProperty.Register<TrendChart, bool>(nameof(ShowLimits), true);

    static TrendChart()
    {
        AffectsRender<TrendChart>(BufferProperty, SeriesProperty, WindowSecondsProperty, VersionProperty,
            LineBrushProperty, GridBrushProperty, TextBrushProperty, UpperLimitProperty, LowerLimitProperty, ShowLimitsProperty);
    }

    public TrendBuffer? Buffer { get => GetValue(BufferProperty); set => SetValue(BufferProperty, value); }
    /// <summary>0 = 頻率，1 = ROCOF。</summary>
    public int Series { get => GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public int WindowSeconds { get => GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }
    public int Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public int Decimals { get => GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? LimitBrush { get => GetValue(LimitBrushProperty); set => SetValue(LimitBrushProperty, value); }
    public double UpperLimit { get => GetValue(UpperLimitProperty); set => SetValue(UpperLimitProperty, value); }
    public double LowerLimit { get => GetValue(LowerLimitProperty); set => SetValue(LowerLimitProperty, value); }
    public bool ShowLimits { get => GetValue(ShowLimitsProperty); set => SetValue(ShowLimitsProperty, value); }

    private long[] _ticks = new long[1024];
    private double[] _values = new double[1024];
    private static readonly Typeface Face = new(FontFamily.Default);

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        const double left = 72, right = 14, top = 26, bottom = 22;
        var plot = new Rect(left, top, Math.Max(10, b.Width - left - right), Math.Max(10, b.Height - top - bottom));
        var text = TextBrush ?? Brushes.Gray;
        var gridPen = new Pen(GridBrush ?? Brushes.LightGray, 1);
        string fmt = "F" + Math.Clamp(Decimals, 0, 8);
        var inv = CultureInfo.InvariantCulture;

        ctx.DrawRectangle(null, gridPen, plot);
        ctx.DrawText(Txt(Title, 13, text, FontWeight.SemiBold), new Point(left, 4));

        var window = TimeSpan.FromSeconds(Math.Max(1, WindowSeconds));
        int n = Buffer?.Snapshot(window, Series, ref _ticks, ref _values, out long latest) ?? 0;
        latest = n > 0 ? _ticks[n - 1] : 0;

        // 數值範圍
        double min = double.PositiveInfinity, max = double.NegativeInfinity, last = double.NaN;
        for (int i = 0; i < n; i++)
        {
            double v = _values[i];
            if (double.IsNaN(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
            last = v;
        }

        var lastText = double.IsNaN(last) ? "--" : $"{last.ToString(fmt, inv)} {Unit}";
        var lt = Txt($"目前 {lastText}", 13, LineBrush ?? text, FontWeight.SemiBold);
        ctx.DrawText(lt, new Point(plot.Right - lt.Width, 4));

        if (double.IsInfinity(min))
        {
            var ft = Txt(n == 0 ? "等待資料…" : "目前皆為缺值", 13, text);
            ctx.DrawText(ft, new Point(plot.Center.X - ft.Width / 2, plot.Center.Y - ft.Height / 2));
            DrawTimeAxis(ctx, plot, text, window);
            return;
        }

        double span = max - min;
        double pad = span > 0 ? span * 0.1 : Math.Max(Math.Abs(max) * 1e-4, Math.Pow(10, -Math.Clamp(Decimals, 0, 8)) * 5);
        min -= pad; max += pad;

        // Y 軸格線
        for (int k = 0; k <= 4; k++)
        {
            double v = min + (max - min) * k / 4;
            double y = plot.Bottom - plot.Height * k / 4;
            if (k is > 0 and < 4) ctx.DrawLine(new Pen(GridBrush ?? Brushes.LightGray, 0.6, new DashStyle([2, 4], 0)), new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Txt(v.ToString(fmt, inv), 11, text);
            ctx.DrawText(ft, new Point(plot.Left - ft.Width - 6, y - ft.Height / 2));
        }
        DrawTimeAxis(ctx, plot, text, window);

        double Y(double v) => plot.Bottom - (v - min) / (max - min) * plot.Height;
        double X(long t) => plot.Right - (latest - t) / (double)window.Ticks * plot.Width;

        // 門檻線
        if (ShowLimits)
        {
            var limitPen = new Pen(LimitBrush ?? Brushes.IndianRed, 1.2, new DashStyle([6, 4], 0));
            foreach (double lim in new[] { UpperLimit, LowerLimit })
            {
                if (double.IsNaN(lim) || lim < min || lim > max) continue;
                double y = Y(lim);
                ctx.DrawLine(limitPen, new Point(plot.Left, y), new Point(plot.Right, y));
                var ft = Txt(lim.ToString(fmt, inv), 10, LimitBrush ?? text);
                ctx.DrawText(ft, new Point(plot.Left + 4, y - ft.Height - 1));
            }
        }

        // 折線（每像素欄取 min/max）
        var linePen = new Pen(LineBrush ?? Brushes.DodgerBlue, 1.6, null, PenLineCap.Round, PenLineJoin.Round);
        using (ctx.PushClip(plot))
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool open = false;
                int col = int.MinValue;
                double cMin = 0, cMax = 0, cFirst = 0, cLast = 0;
                bool hasCol = false;

                void FlushCol(StreamGeometryContext gc)
                {
                    if (!hasCol) return;
                    double x = plot.Left + col + 0.5;
                    var pFirst = new Point(x, Y(cFirst));
                    if (!open) { gc.BeginFigure(pFirst, false); open = true; }
                    else gc.LineTo(pFirst);
                    if (cMax != cMin) { gc.LineTo(new Point(x, Y(cMin))); gc.LineTo(new Point(x, Y(cMax))); }
                    gc.LineTo(new Point(x, Y(cLast)));
                    hasCol = false;
                }

                for (int i = 0; i < n; i++)
                {
                    double v = _values[i];
                    if (double.IsNaN(v))
                    {
                        FlushCol(g);
                        if (open) { g.EndFigure(false); open = false; }
                        continue;
                    }
                    int c = (int)Math.Floor(X(_ticks[i]) - plot.Left);
                    if (c != col)
                    {
                        FlushCol(g);
                        col = c; cMin = cMax = cFirst = v;
                    }
                    else
                    {
                        if (v < cMin) cMin = v;
                        if (v > cMax) cMax = v;
                    }
                    cLast = v;
                    hasCol = true;
                }
                FlushCol(g);
                if (open) g.EndFigure(false);
            }
            ctx.DrawGeometry(null, linePen, geo);
        }
    }

    private static void DrawTimeAxis(DrawingContext ctx, Rect plot, IBrush text, TimeSpan window)
    {
        for (int k = 0; k <= 4; k++)
        {
            double secs = window.TotalSeconds * (4 - k) / 4;
            string s = k == 4 ? "現在" : $"-{secs:0.#}s";
            var ft = Txt(s, 11, text);
            double x = plot.Left + plot.Width * k / 4 - ft.Width / 2;
            x = Math.Clamp(x, plot.Left - 4, plot.Right - ft.Width);
            ctx.DrawText(ft, new Point(x, plot.Bottom + 3));
        }
    }

    private static FormattedText Txt(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush);
}
