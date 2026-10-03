using Avalonia.Controls;
using Avalonia.Logging;
using Avalonia.VisualTree;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 绑定卫生:把主要界面走一遍(集合三视图 + 检查器 + 内联编辑、筛选补全、查询、GridFS、对象列表),
/// 期间 Avalonia 报出的绑定错误一条都不许有。
/// <para>
/// 这些错误不让界面崩,只在宿主的调试输出里刷屏(<c>[Binding]An error occurred binding …</c>)——
/// 正因为不疼不痒,不拿测试守着就会一条条攒起来,真出问题时淹没在噪声里。
/// 常见的两种:路径中段为 null(<c>Editor.HasError</c> 在不编辑时),以及控件从外面继承了
/// 别人的 DataContext、编译绑定按 <c>x:DataType</c> 去转型失败(补全弹层继承了宿主视图的视图模型)。
/// </para>
/// </summary>
[TestClass]
public sealed class BindingHygieneTests
{
    private sealed class BindingErrorSink : ILogSink
    {
        public List<string> Errors { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Log(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area))
            {
                Errors.Add($"{messageTemplate} | {string.Join(" | ", propertyValues)} | {source?.GetType().Name}");
            }
        }
    }

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Screens.PumpAsync(5);
        }
    }

    [TestMethod]
    public void Walking_the_main_screens_raises_no_binding_errors() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        var sink = new BindingErrorSink();
        ILogSink? previous = Logger.Sink;
        Logger.Sink = sink;
        try
        {
            await using Workbench bench = await Screens.OpenWorkbenchAsync();

            // 集合:网格 + 检查器,内联编辑一格、检查器里编辑一个字段再点回网格。
            bench.Session.OpenCollection(Screens.Database, "orders");
            var tab = (CollectionTabViewModel)bench.ViewModel.ActiveTab!;
            await WaitAsync(() => !tab.IsLoading && tab.Rows.Count > 0);
            await Screens.PumpAsync(20);
            tab.SelectedRow = tab.Rows[0];
            await Screens.PumpAsync(10);
            CollectionCell cell = tab.Rows[0].CellOf(tab.Columns.Single(static c => c.Name == "status"))!;
            tab.BeginCellEdit(cell);
            await Screens.PumpAsync(10);
            CollectionTabViewModel.CancelCellEdit(cell);
            InspectorField status = tab.Inspector.Fields.First(static f => f.Path == "status");
            tab.Inspector.BeginEdit(status);
            await Screens.PumpAsync(10);
            DocInspectorViewModel.CancelEdit(status);
            tab.Inspector.Page = 1;
            await Screens.PumpAsync(10);
            tab.Inspector.Page = 0;

            // 筛选框的补全弹层开一次。
            CollectionTabView view = bench.Window.GetVisualDescendants().OfType<CollectionTabView>().First(static v => v.IsEffectivelyVisible);
            CodeEditor filter = view.FindControl<CodeEditor>("FilterEditor")!;
            filter.FocusEditor();
            filter.RequestCompletion();
            await Screens.PumpAsync(20);

            // 树视图、JSON 视图(大纲)。
            tab.ViewMode = CollectionViewMode.Tree;
            await Screens.PumpAsync(20);
            tab.ViewMode = CollectionViewMode.Json;
            await Screens.PumpAsync(20);
            tab.ViewMode = CollectionViewMode.Grid;
            await Screens.PumpAsync(10);

            // 查询、GridFS、对象列表。
            bench.Session.OpenQuery(Screens.Database, "db.orders.find({}).limit(5)", run: true);
            await Screens.PumpAsync(60);
            bench.Session.OpenGridFs(Screens.Database, "fs");
            await WaitAsync(() => bench.ViewModel.ActiveTab is GridFsTabViewModel { IsLoading: false });
            var gridFs = (GridFsTabViewModel)bench.ViewModel.ActiveTab!;
            // 选一个多版本文件:详情里的版本列表(勾选框、行上的删除、底下那两条)都要绑一遍。
            await gridFs.NavigateAsync("products/SKU-7710/");
            await WaitAsync(() => gridFs.Entries.Any(static e => e.Name == "main.jpg"));
            gridFs.SelectedEntry = gridFs.Entries.First(static e => e.Name == "main.jpg");
            await WaitAsync(() => gridFs.Details.Versions.Count > 0 && !gridFs.Details.IsPreviewLoading);
            gridFs.Details.Versions.LastOrDefault()?.IsChecked = true;
            await Screens.PumpAsync(20);
            bench.Session.OpenObjects(Screens.Database);
            await Screens.PumpAsync(40);
        }
        finally
        {
            Logger.Sink = previous;
        }

        Assert.IsEmpty(sink.Errors, "binding errors:\n" + string.Join("\n", sink.Errors.Distinct()));
    });

    [TestMethod]
    public void The_other_tabs_and_every_dialog_raise_no_binding_errors() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        var sink = new BindingErrorSink();
        ILogSink? previous = Logger.Sink;
        Logger.Sink = sink;
        try
        {
            await using Workbench bench = await Screens.OpenWorkbenchAsync();
            MongoSession session = bench.Session;

            session.OpenPipeline(Screens.Database, "orders");
            await Screens.PumpAsync(40);
            foreach (DesignPage page in Enum.GetValues<DesignPage>())
            {
                session.OpenDesign(Screens.Database, "orders", page);
                await Screens.PumpAsync(30);
            }
            session.OpenMonitor();
            await Screens.PumpAsync(40);
            session.OpenProfiler(Screens.Database);
            await Screens.PumpAsync(30);
            session.OpenUsers(Screens.Database);
            await Screens.PumpAsync(30);
            session.OpenUsers(Screens.Database, roles: true);
            await Screens.PumpAsync(30);

            session.OpenCollection(Screens.Database, "orders");
            var tab = (CollectionTabViewModel)bench.ViewModel.Tabs.Last(static t => t is CollectionTabViewModel);
            await WaitAsync(() => !tab.IsLoading && tab.Rows.Count > 0);

            async Task ShowAsync(Action open)
            {
                open();
                await Screens.PumpAsync(30);
                bench.ViewModel.Dialog?.Close();
                await Screens.PumpAsync(5);
            }

            await ShowAsync(() => tab.OpenInEditor(tab.Rows[0]));
            await ShowAsync(() => session.ShowDialog(new NewCollectionDialogViewModel(session, Screens.Database)));
            await ShowAsync(() => session.ShowDialog(new GridFsNewBucketDialogViewModel(session, Screens.Database)));
            await ShowAsync(() => session.ShowDialog(new ExportWizardViewModel(session, Screens.Database, "orders", null, null)));
            await ShowAsync(() => session.ShowDialog(new ImportWizardViewModel(session, Screens.Database, "orders")));
            await ShowAsync(() => session.ShowDialog(new TransferWizardViewModel(session, Screens.Database)));
            await ShowAsync(() => bench.ViewModel.NewConnectionCommand.Execute(null));
        }
        finally
        {
            Logger.Sink = previous;
        }

        Assert.IsEmpty(sink.Errors, "binding errors:\n" + string.Join("\n", sink.Errors.Distinct()));
    });
}
