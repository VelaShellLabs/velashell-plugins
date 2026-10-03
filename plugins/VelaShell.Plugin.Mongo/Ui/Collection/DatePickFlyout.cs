using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VelaShell.Plugin.Mongo.Bson;
using Calendar = Avalonia.Controls.Calendar;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 日期值的选择弹层:月历 + 时刻框 +「现在」/「应用」。
/// <para>
/// 网格、检查器、树视图里内联编辑日期字段时,编辑框右边的日历按钮弹它。以前网格那支下拉箭头里只有一条
/// "现在"的文字,换一天还得手敲整串 <c>yyyy-MM-dd HH:mm:ss</c>。
/// 点一天 = 换成那一天(时刻取时刻框里的)并写入;只改时刻就在框里回车或点「应用」。全程本地时间,
/// 与编辑框的写法一致(存进去时由 <see cref="BsonEdit.TryParseDate" /> 换成 UTC)。
/// </para>
/// </summary>
internal static class DatePickFlyout
{
    private static readonly string[] TimeFormats = [@"h\:mm\:ss", @"hh\:mm\:ss", @"h\:mm", @"hh\:mm"];

    /// <summary>在 <paramref name="anchor" /> 下方弹出。</summary>
    /// <param name="anchor">锚点(编辑框右边的日历按钮)。</param>
    /// <param name="loc">文案。</param>
    /// <param name="text">编辑框里当前的文本;解析不了(空、写了一半)就从"现在"起步。</param>
    /// <param name="picked">选定之后:拿到要写回编辑框的文本(<see cref="BsonText.DateFormat" />,本地时间)。</param>
    /// <returns>弹层(测试要直接摆弄里面的月历)。</returns>
    public static Flyout Show(Control anchor, Loc loc, string text, Action<string> picked)
    {
        DateTime start = BsonEdit.TryParseDate(text, out DateTime utc) ? utc.ToLocalTime() : DateTime.Now;
        var calendar = new Calendar
        {
            SelectionMode = CalendarSelectionMode.SingleDate,
            SelectedDate = start.Date,
            DisplayDate = start.Date,
            IsTodayHighlighted = true
        };
        var time = new TextBox
        {
            Text = start.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            Width = 96,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (anchor.TryFindResource("VelaUiMonoFont", out object? mono) && mono is FontFamily family)
        {
            time.FontFamily = family;
        }
        ToolTip.SetTip(time, loc["Doc_LocalTime"]);
        var now = new Button { Content = loc["Cw_DateNow"], VerticalAlignment = VerticalAlignment.Center };
        var apply = new Button { Content = loc["Common_Apply"], VerticalAlignment = VerticalAlignment.Center };
        apply.Classes.Add("accent");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(now);
        buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Right);
        var label = new TextBlock
        {
            Text = loc["Cw_DateTime"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = ThemeBrushes.Get("VelaTextSecondary", Brushes.Gray)
        };
        DockPanel.SetDock(label, Dock.Left);
        var bar = new DockPanel { LastChildFill = false };
        bar.Children.Add(buttons);
        bar.Children.Add(label);
        bar.Children.Add(time);

        var flyout = new Flyout
        {
            Content = new StackPanel { Spacing = 8, Children = { calendar, bar } },
            Placement = PlacementMode.BottomEdgeAlignedRight
        };
        IBrush? normalBorder = null;

        void Commit(DateTime day)
        {
            if (!TryParseTime(time.Text, out TimeSpan of))
            {
                normalBorder ??= time.BorderBrush;
                time.BorderBrush = ThemeBrushes.Get("VelaError", Brushes.Red);
                ToolTip.SetTip(time, loc["Cw_DateBadTime"]);
                time.Focus();
                return;
            }
            flyout.Hide();
            picked((day.Date + of).ToString(BsonText.DateFormat, CultureInfo.InvariantCulture));
        }

        // 初值设完再挂事件:构造时那一次 SelectedDate 不算用户选的。
        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (calendar.SelectedDate is { } day)
            {
                Commit(day);
            }
        };
        apply.Click += (_, _) => Commit(calendar.SelectedDate ?? start.Date);
        now.Click += (_, _) =>
        {
            flyout.Hide();
            picked(DateTime.Now.ToString(BsonText.DateFormat, CultureInfo.InvariantCulture));
        };
        time.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit(calendar.SelectedDate ?? start.Date);
            }
        };
        time.TextChanged += (_, _) =>
        {
            if (normalBorder is not null)
            {
                time.BorderBrush = normalBorder;
                ToolTip.SetTip(time, loc["Doc_LocalTime"]);
            }
        };
        flyout.ShowAt(anchor);
        return flyout;
    }

    /// <summary>时刻框:<c>9:30</c>、<c>09:30</c>、<c>09:30:15</c> 都认。</summary>
    internal static bool TryParseTime(string? text, out TimeSpan time) =>
        TimeSpan.TryParseExact((text ?? "").Trim(), TimeFormats, CultureInfo.InvariantCulture, out time)
        && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
}
