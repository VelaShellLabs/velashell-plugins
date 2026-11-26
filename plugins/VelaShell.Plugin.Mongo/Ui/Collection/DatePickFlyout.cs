using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
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
    private static readonly string[] TimeFormats = ["H:mm:ss.fff", "H:mm:ss.ff", "H:mm:ss.f", "H:mm:ss", "H:mm"];

    /// <summary>在 <paramref name="anchor" /> 下方弹出。</summary>
    /// <param name="anchor">锚点(编辑框右边的日历按钮)。</param>
    /// <param name="loc">文案。</param>
    /// <param name="text">编辑框里当前的文本;解析不了(空、写了一半)就从"现在"起步。</param>
    /// <param name="picked">选定之后:拿到要写回编辑框的文本(<see cref="BsonText.DateFormat" />,本地时间)。</param>
    /// <param name="applyTogether">文档表单里允许连续选择日期、时分秒,点应用后才回写。</param>
    /// <returns>弹层(测试要直接摆弄里面的月历)。</returns>
    public static Flyout Show(Control anchor, Loc loc, string text, Action<string> picked, bool applyTogether = false)
    {
        DateTime start = BsonEdit.TryParseDate(text, out DateTime utc) ? utc.ToLocalTime() : DateTime.Now;
        var calendar = new Calendar
        {
            SelectionMode = CalendarSelectionMode.SingleDate,
            SelectedDate = start.Date,
            DisplayDate = start.Date,
            IsTodayHighlighted = true
        };
        var now = new Button { Theme = ThemeOf(anchor, "MongoOutlineButton"), Content = loc["Cw_DateNow"], Padding = new Thickness(12, 0) };
        var apply = new Button { Theme = ThemeOf(anchor, "MongoPillButton"), Content = loc["Common_Apply"], Padding = new Thickness(12, 0) };
        var content = new StackPanel { Spacing = 8, Children = { calendar, new Border { Height = 1, Background = ThemeBrushes.Get("VelaBorderPrimary", Brushes.Gray) } } };
        var flyout = new Flyout { Content = content, Placement = PlacementMode.BottomEdgeAlignedRight };
        void Pick(DateTime value)
        {
            flyout.Hide();
            picked(value.ToString(BsonText.DateFormat, CultureInfo.InvariantCulture));
        }
        now.Click += (_, _) => Pick(DateTime.Now);
        // 时刻框与两颗按钮用本插件自己的主题(描边输入框、主操作药丸、次操作描边):
        // 不指定就落到宿主的 Fluent 默认上 —— 圆角大一号、「应用」是系统强调蓝,和上面的月历不是一套。
        var time = new TextBox
        {
            Theme = ThemeOf(anchor, "MongoTextBox"),
            Text = start.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            Height = 24,
            Padding = new Thickness(5, 0),
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        string hint = loc["Cw_TimeAdjustHint"];
        ToolTip.SetTip(time, hint);
        Avalonia.Automation.AutomationProperties.SetName(time, loc["Cw_DateTime"]);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(now);
        buttons.Children.Add(apply);
        // .ui = VelaUiFont 11 + VelaTextSecondary(MongoStyles.axaml)。
        var label = new TextBlock { Text = loc["Cw_DateTime"], Margin = new Thickness(2, 0, 8, 0), Classes = { "ui" } };
        DockPanel.SetDock(label, Dock.Left);
        content.Children.Add(new DockPanel { Children = { label, time } });
        content.Children.Add(buttons);

        void Commit(DateTime day)
        {
            if (!TryParseTime(time.Text, out TimeSpan of))
            {
                time.Classes.Set("invalid", true);
                ToolTip.SetTip(time, loc["Cw_DateBadTime"]);
                _ = time.Focus();
                return;
            }
            Pick(day.Date + of);
        }

        // 初值设完再挂事件:构造时那一次 SelectedDate 不算用户选的。
        if (!applyTogether)
        {
            calendar.SelectedDatesChanged += (_, _) =>
            {
                if (calendar.SelectedDate is { } day)
                {
                    Commit(day);
                }
            };
        }
        apply.Click += (_, _) => Commit(calendar.SelectedDate ?? start.Date);
        bool Step(int direction)
        {
            if (time.Text is not { } raw || !TryParseTime(raw, out TimeSpan current))
            {
                return false;
            }
            int caret = Math.Min(Math.Min(time.SelectionStart, time.SelectionEnd), raw.Length);
            var prefix = raw.AsSpan(0, caret);
            int part = prefix.Contains('.') ? 3 : prefix.Count(':');
            int[] parts = [current.Hours, current.Minutes, current.Seconds, current.Milliseconds];
            int limit = part switch { 0 => 24, 3 => 1000, _ => 60 };
            parts[part] = (parts[part] + limit + direction) % limit;
            time.Text = new TimeSpan(0, parts[0], parts[1], parts[2], parts[3]).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
            time.SelectionStart = part * 3;
            time.SelectionEnd = part * 3 + (part == 3 ? 3 : 2);
            return true;
        }
        time.PointerWheelChanged += (_, e) =>
        {
            if (time.IsKeyboardFocusWithin && e.Delta.Y != 0)
            {
                e.Handled = Step(Math.Sign(e.Delta.Y));
            }
        };
        time.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = Step(e.Key == Key.Up ? 1 : -1);
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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
            if (time.Classes.Contains("invalid"))
            {
                time.Classes.Set("invalid", false);
                ToolTip.SetTip(time, hint);
            }
        };
        flyout.ShowAt(anchor);
        return flyout;
    }

    /// <summary>本插件的具名控件主题(MongoStyles.axaml)。锚点在引了那份样式的视图里,从它往上找得到。</summary>
    private static ControlTheme? ThemeOf(Control anchor, string key) =>
        anchor.TryFindResource(key, out object? found) ? found as ControlTheme : null;

    /// <summary>时刻框:<c>9:30</c>、<c>09:30</c>、<c>09:30:15</c> 都认。</summary>
    internal static bool TryParseTime(string? text, out TimeSpan time)
    {
        bool valid = TimeOnly.TryParseExact(text?.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly parsed);
        time = parsed.ToTimeSpan();
        return valid;
    }
}
