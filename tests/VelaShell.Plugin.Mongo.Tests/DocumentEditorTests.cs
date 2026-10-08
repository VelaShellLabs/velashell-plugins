using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Ui;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Mongo.Tests;

// 测试方法的名字就是它的说明(一句完整的句子),不再逐个写 XML 注释。

/// <summary>
/// 文档编辑器(设计稿 05):行状态、差异与更新计划、JSON / 对比 / 预览、粘贴与按 Schema 补全,
/// 以及打真实 MongoDB 的保存路径(只提交变更字段、乐观并发冲突、另存为新文档、只读拦截、写前确认)。
/// </summary>
[TestClass]
public sealed class DocumentEditorTests
{
    private static readonly Loc Zh = new("zh-CN");

    private const string OrderText = """
        { _id: ObjectId("66f5c2a1d38b5e1a0c7fe3b7"), orderNo: "SO2609-10403",
          customer: { id: ObjectId("66e01d9f7a3c5b2e8f0d41c2"), name: "张伟", level: "SVIP" },
          items: [ { sku: "SKU-7710", qty: 2, price: NumberDecimal("210.00") }, { sku: "SKU-1182", qty: 1, price: NumberDecimal("99.00") } ],
          total: NumberDecimal("8740.00"), status: "paid", paid: true,
          createdAt: ISODate("2026-09-26T12:51:27Z"), tags: ["企业", "开票"], note: null }
        """;

    private const string OrderSchema = """
        { $jsonSchema: { bsonType: "object", required: ["orderNo", "total"],
            properties: {
              discount: { bsonType: "double", minimum: 0 },
              customer: { properties: { level: { enum: ["普通", "VIP", "SVIP"] } } } } } }
        """;

    private static BsonDocument Order() => ShellJson.ParseDocument(OrderText);

    private static CollectionInfo Orders(string? validator = null, string action = "error", CollectionKind kind = CollectionKind.Collection)
    {
        var options = new BsonDocument();
        if (validator is not null)
        {
            options["validator"] = ShellJson.ParseDocument(validator);
            options["validationAction"] = action;
        }
        return new("shop", "orders", kind, options);
    }

    private static DocumentEditorDialogViewModel Open(DocEditorWorkspace workspace, CollectionInfo? info = null, BsonDocument? document = null,
        Func<BsonDocument?, Task>? saved = null)
    {
        var vm = new DocumentEditorDialogViewModel(workspace, info ?? Orders(), document, saved);
        vm.RecomputeNow();
        return vm;
    }

    private static DocumentEditorRow Row(DocumentEditorDialogViewModel vm, string path) =>
        vm.FindRow(path) ?? throw new AssertFailedException($"row {path} not found");

    // ── 打开 ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void A_document_without_id_is_a_template_for_an_insert() => Screens.OnUi(() =>
    {
        // 网格的「克隆」传进来的是去掉 _id 的文档:它必须按新文档(insert)处理,
        // 而不是当成已存在的文档去 updateOne —— 筛选里没有 _id,那样什么都改不到。
        BsonDocument clone = Order();
        clone.Remove("_id");
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: clone);

        Assert.IsFalse(vm.IsExisting);
        Assert.IsTrue(vm.HasChanges, "新文档总是可保存的");
        BsonDocument built = vm.BuildDocument();
        Assert.AreEqual(BsonType.ObjectId, built["_id"].BsonType, "给新文档配一个新的 _id");
        Assert.AreEqual(clone["orderNo"], built["orderNo"], "其余字段照模板带过来");
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Opening_an_existing_document_shows_its_fields_without_changes() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: Order());
        CollectionAssert.AreEqual(
            new[] { "_id", "orderNo", "customer", "items", "total", "status", "paid", "createdAt", "tags", "note" },
            vm.Roots.Select(static r => r.Name).ToArray());
        Assert.AreEqual("编辑文档", vm.Title);
        Assert.AreEqual("shop.orders", vm.Subtitle);
        Assert.AreEqual("Mongo.file-pen-line", vm.IconKey);
        Assert.AreEqual(1100, vm.Width);
        Assert.AreEqual(708, vm.Height);
        Assert.AreEqual("ObjectId(\"66f5c2a1d38b5e1a0c7fe3b7\")", vm.IdText);
        // customer 默认展开,数组收起;末尾是「添加字段」那一格。
        Assert.IsTrue(Row(vm, "customer").IsExpanded);
        Assert.IsFalse(Row(vm, "items").IsExpanded);
        Assert.IsTrue(vm.FormItems.Contains(Row(vm, "customer.level")));
        Assert.IsFalse(vm.FormItems.Contains(Row(vm, "items").Children[0]));
        _ = Assert.IsInstanceOfType<DocumentEditorTail>(vm.FormItems[^1]);
        Assert.IsFalse(vm.HasChanges);
        Assert.IsFalse(vm.CanSave);
        Assert.AreEqual("没有修改", vm.FooterHint);
        Assert.AreEqual(Zh["Doc_NoCommand"], vm.CommandText);
        Assert.IsTrue(vm.Roots.All(static r => r.State == DocumentEditorRowState.None));
        Assert.IsTrue(Row(vm, "_id").IsIdLocked);
        Assert.IsFalse(Row(vm, "_id").CanRemove);
        Assert.IsTrue(vm.CanCloseWithEscape);
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Value_editors_follow_the_kind() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: Order());
        Assert.IsTrue(Row(vm, "orderNo").ShowText);
        Assert.IsTrue(Row(vm, "total").ShowNumber);
        Assert.IsTrue(Row(vm, "paid").ShowBool);
        Assert.IsTrue(Row(vm, "paid").BoolValue);
        Assert.IsTrue(Row(vm, "createdAt").ShowDate);
        Assert.IsTrue(Row(vm, "_id").ShowObjectId);
        Assert.IsTrue(Row(vm, "note").ShowNull);
        Assert.IsTrue(Row(vm, "customer").ShowObjectSummary);
        Assert.AreEqual("{3}", Row(vm, "customer").ContainerCount);
        Assert.AreEqual("id, name, level", Row(vm, "customer").ContainerSummary);
        Assert.IsTrue(Row(vm, "tags").ShowChips);
        Assert.AreEqual("String", Row(vm, "tags").NewItemLabel);
        Assert.IsTrue(Row(vm, "items").ShowArraySummary);
        Assert.AreEqual("[2]", Row(vm, "items").ContainerCount);
        Assert.AreEqual("SKU-7710 ×2 · SKU-1182 ×1", Row(vm, "items").ContainerSummary);
        Assert.AreEqual("8740.00", Row(vm, "total").Text);
        Assert.AreEqual("[0]", Row(vm, "items").Children[0].DisplayName);
        Assert.AreEqual("Decimal128", BsonKinds.Name(Row(vm, "total").Kind));
        return Task.CompletedTask;
    });

    // ── 行状态与更新计划 ───────────────────────────────────────────────────

    [TestMethod]
    public void Edits_mark_rows_and_build_a_minimal_update() => Screens.OnUi(() =>
    {
        var workspace = new DocEditorWorkspace();
        DocumentEditorDialogViewModel vm = Open(workspace, document: Order());

        Row(vm, "status").Text = "shipped";
        Row(vm, "customer.level").Text = "VIP";
        DocumentEditorRow shipped = vm.AddAfter(Row(vm, "paid"))!;
        shipped.Name = "shippedAt";
        shipped.SetValue(new BsonDateTime(new DateTime(2026, 9, 27, 2, 30, 0, DateTimeKind.Utc)));
        vm.Remove(Row(vm, "note"));
        vm.RecomputeNow();

        DocumentEditorRow status = Row(vm, "status");
        Assert.AreEqual(DocumentEditorRowState.Modified, status.State);
        Assert.AreEqual("原值 \"paid\"", status.OriginalText);
        Assert.IsTrue(status.ShowRevert);
        Assert.AreEqual(DocumentEditorRowState.Added, shipped.State);
        Assert.IsTrue(shipped.ShowDateExtras);
        StringAssert.Matches(shipped.TimeZoneText, new System.Text.RegularExpressions.Regex(@"^UTC[+-]\d\d:\d\d$"));
        StringAssert.Contains(shipped.IsoEcho, "02:30Z");
        Assert.AreEqual(DocumentEditorRowState.Modified, Row(vm, "customer.level").State);
        Assert.AreEqual(DocumentEditorRowState.None, Row(vm, "customer").State, "an expanded parent leaves the change to its child");

        Assert.AreEqual("2 处修改", vm.ModifiedText);
        Assert.AreEqual("1 个新字段", vm.AddedText);
        Assert.AreEqual("1 处删除", vm.RemovedText);
        Assert.IsFalse(vm.HasErrors);
        Assert.IsTrue(vm.CanSave);
        Assert.IsFalse(vm.CanCloseWithEscape, "Esc must not throw away unsaved edits");
        Assert.AreEqual("updateOne", vm.CommandName);
        StringAssert.StartsWith(vm.CommandText, "db.orders.updateOne(\n  { _id: ObjectId(\"66f5c2a1d38b5e1a0c7fe3b7\") },\n  { $set: {");
        StringAssert.Contains(vm.CommandText, "\"customer.level\": \"VIP\"");
        StringAssert.Contains(vm.CommandText, "shippedAt: ISODate(\"2026-09-27T02:30:00Z\")");
        StringAssert.Contains(vm.CommandText, "$unset: {\n      note: \"\"");
        Assert.AreEqual(Zh["Doc_OnlyChangedNoVersion"], vm.CommandNote);
        Assert.IsFalse(vm.CommandText.Contains("orderNo", StringComparison.Ordinal), "only changed fields are sent");
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Revert_restores_the_original_value_and_type() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: Order());
        DocumentEditorRow total = Row(vm, "total");
        total.ChangeKind(BsonKind.Double);
        vm.RecomputeNow();
        Assert.AreEqual(BsonKind.Double, total.Kind);
        Assert.AreEqual(DocumentEditorRowState.Modified, total.State);
        total.RevertCommand.Execute(null);
        vm.RecomputeNow();
        DocumentEditorRow reverted = Row(vm, "total");
        Assert.AreEqual(BsonKind.Decimal128, reverted.Kind);
        Assert.AreEqual("8740.00", reverted.Text);
        Assert.AreEqual(DocumentEditorRowState.None, reverted.State);
        Assert.IsFalse(vm.HasChanges);
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Changing_the_kind_converts_or_resets_with_a_notice() => Screens.OnUi(() =>
    {
        var workspace = new DocEditorWorkspace();
        DocumentEditorDialogViewModel vm = Open(workspace, document: Order());
        DocumentEditorRow total = Row(vm, "total");
        total.SelectedKind = BsonKind.Int64;
        Assert.AreEqual("8740", total.Text);
        Assert.HasCount(0, workspace.Toasts);

        DocumentEditorRow orderNo = Row(vm, "orderNo");
        orderNo.SelectedKind = BsonKind.Int32;
        Assert.AreEqual(BsonKind.Int32, orderNo.Kind);
        Assert.AreEqual("0", orderNo.Text);
        Assert.HasCount(1, workspace.Toasts);
        Assert.AreEqual("orderNo:值无法换算为 Int32,已重置", workspace.Toasts[0].Title);

        DocumentEditorRow note = Row(vm, "note");
        note.SetStringCommand.Execute(null);
        Assert.AreEqual(BsonKind.String, note.Kind);
        Assert.AreEqual("", note.Text);

        DocumentEditorRow tags = Row(vm, "tags");
        tags.SelectedKind = BsonKind.Object;
        vm.RecomputeNow();
        Assert.IsTrue(tags.IsExpanded, "a fresh container opens so it can be filled");
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Name_and_parse_problems_are_errors_that_block_saving() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: Order());
        DocumentEditorRow fresh = vm.AddFieldAtEnd()!;
        Assert.AreEqual("newField", fresh.Name);
        Assert.AreEqual("newField2", vm.AddFieldAtEnd()!.Name);
        vm.Remove(vm.Roots[^1]);

        (string Name, string Message)[] cases =
        [
            ("", "字段名不能为空"),
            ("status", "字段名 status 重复"),
            ("$bad", "字段名不能以 $ 开头"),
            ("a.b", "字段名不能含点号")
        ];
        foreach ((string name, string message) in cases)
        {
            fresh.Name = name;
            vm.RecomputeNow();
            Assert.AreEqual(DocumentEditorRowState.Error, fresh.State, name);
            Assert.AreEqual(message, fresh.Message, name);
            Assert.IsFalse(fresh.IsValueError, "a name problem only marks the name");
            Assert.IsFalse(vm.CanSave);
        }
        fresh.Name = "ok";
        DocumentEditorRow total = Row(vm, "total");
        total.Text = "12,34x";
        vm.RecomputeNow();
        Assert.AreEqual("不是数字", total.ParseError);
        Assert.IsTrue(total.IsValueError);
        Assert.AreEqual("1 个错误", vm.ErrorText);
        Assert.AreEqual("修正错误后才能保存", vm.FooterHint);
        Assert.IsFalse(vm.CanSave);
        Assert.IsFalse(vm.CanSaveAsNew);
        total.Text = "1,234.50";
        vm.RecomputeNow();
        Assert.IsNull(total.ParseError);
        Assert.IsTrue(vm.CanSave);
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Validator_violations_mark_rows_and_unmatched_ones_become_issues() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), Orders(OrderSchema), Order());
        DocumentEditorRow level = Row(vm, "customer.level");
        CollectionAssert.AreEqual(new[] { "普通", "VIP", "SVIP" }, level.EnumOptions.ToArray());
        Assert.IsTrue(level.ShowEnum);
        Assert.IsFalse(level.ShowText);
        Assert.IsTrue(level.ShowEnumHint);
        Assert.AreEqual("enum: 普通 · VIP · SVIP", level.EnumHint);

        DocumentEditorRow discount = vm.AddAfter(Row(vm, "total"))!;
        discount.Name = "discount";
        discount.SetValue(new BsonDouble(-20));
        level.EnumValue = "钻石";
        vm.Remove(Row(vm, "orderNo"));
        vm.RecomputeNow();

        Assert.AreEqual(DocumentEditorRowState.Error, discount.State);
        Assert.AreEqual("违反验证规则:discount 须 ≥ 0", discount.Message);
        Assert.AreEqual(DocumentEditorRowState.Error, level.State);
        StringAssert.StartsWith(level.Message, "违反验证规则:customer.level 不在枚举中");
        CollectionAssert.Contains(vm.Issues.ToArray(), "违反验证规则:缺少必填字段 orderNo");
        Assert.AreEqual("3 个错误", vm.ErrorText);
        Assert.IsFalse(vm.HasAdded, "an erroneous new field counts as an error, not as a new field");
        Assert.IsFalse(vm.CanSave);

        // 收起的容器把子孙的错误提到自己这一行。
        vm.Toggle(Row(vm, "customer"));
        vm.RecomputeNow();
        Assert.AreEqual(DocumentEditorRowState.Error, Row(vm, "customer").State);
        Assert.AreEqual(level.Message, Row(vm, "customer").Message);
        Assert.AreEqual("3 个错误", vm.ErrorText, "the lifted error is not counted twice");
        return Task.CompletedTask;
    });

    [TestMethod]
    public void A_warn_only_validator_does_not_block_saving() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), Orders(OrderSchema, action: "warn"), Order());
        DocumentEditorRow discount = vm.AddAfter(Row(vm, "total"))!;
        discount.Name = "discount";
        discount.SetValue(new BsonDouble(-1));
        vm.RecomputeNow();
        Assert.AreEqual(DocumentEditorRowState.Warning, discount.State);
        Assert.AreEqual("1 个警告", vm.WarningText);
        Assert.IsFalse(vm.HasErrors);
        Assert.IsTrue(vm.CanSave);
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Reordering_top_level_fields_switches_to_replaceOne() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: Order());
        Assert.IsTrue(vm.Move(Row(vm, "status"), Row(vm, "orderNo"), after: false));
        vm.RecomputeNow();
        CollectionAssert.AreEqual(new[] { "_id", "status", "orderNo" }, vm.Roots.Take(3).Select(static r => r.Name).ToArray());
        Assert.AreEqual("replaceOne", vm.CommandName);
        Assert.IsTrue(vm.HasReordered);
        Assert.AreEqual(Zh["Doc_ReplaceNoteNoVersion"], vm.CommandNote);

        // _id 留在第一位。
        _ = vm.Move(Row(vm, "status"), Row(vm, "_id"), after: false);
        Assert.AreEqual("_id", vm.Roots[0].Name);
        Assert.AreEqual("status", vm.Roots[1].Name);
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Reordering_inside_an_object_sets_that_object_and_moves_stay_on_one_level() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: Order());
        Assert.IsTrue(vm.Move(Row(vm, "customer.level"), Row(vm, "customer.id"), after: false));
        vm.RecomputeNow();
        CollectionAssert.AreEqual(new[] { "level", "id", "name" }, Row(vm, "customer").Children.Select(static r => r.Name).ToArray());
        Assert.AreEqual("updateOne", vm.CommandName);
        StringAssert.Contains(vm.CommandText, "customer: { level: \"SVIP\"");
        Assert.AreEqual(DocumentEditorRowState.Modified, Row(vm, "customer").State);

        // 落在别的层上:取那一侧与它同层的祖先,不会把字段拖进另一个对象。
        Assert.IsTrue(vm.Move(Row(vm, "paid"), Row(vm, "customer.name"), after: false));
        Assert.IsNull(vm.FindRow("customer.paid"));
        Assert.AreEqual(vm.Roots.ToList().IndexOf(Row(vm, "customer")) + 1, vm.Roots.ToList().IndexOf(Row(vm, "paid")));
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Array_chips_append_and_remove_items() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: Order());
        DocumentEditorRow tags = Row(vm, "tags");
        tags.BeginItemCommand.Execute(null);
        Assert.IsTrue(tags.IsAddingItem);
        tags.NewItemText = "VIP客户";
        tags.CommitItemCommand.Execute(null);
        Assert.IsTrue(tags.IsAddingItem, "the box stays open for the next item");
        Assert.AreEqual("", tags.NewItemText);
        tags.CommitItemCommand.Execute(null);
        Assert.IsFalse(tags.IsAddingItem, "an empty Enter closes it");
        CollectionAssert.AreEqual(new[] { "企业", "开票", "VIP客户" }, tags.Children.Select(static c => c.ChipText).ToArray());
        tags.Children[0].RemoveCommand.Execute(null);
        CollectionAssert.AreEqual(new[] { "0", "1" }, tags.Children.Select(static c => c.Name).ToArray());
        vm.RecomputeNow();
        Assert.AreEqual(DocumentEditorRowState.Modified, tags.State);
        StringAssert.Contains(vm.CommandText, "tags: [ \"开票\", \"VIP客户\" ]");

        // 等长数组里改一项:只 $set 那一项的路径。
        vm.Toggle(Row(vm, "items"));
        DocumentEditorRow qty = Row(vm, "items").Children[1].Children.Single(static c => c.Name == "qty");
        qty.Text = "5";
        vm.RecomputeNow();
        Assert.AreEqual("items.1.qty", qty.Path);
        Assert.AreEqual(DocumentEditorRowState.Modified, qty.State);
        StringAssert.Contains(vm.CommandText, "\"items.1.qty\": 5");
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Search_filters_rows_and_keeps_ancestors() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: Order());
        vm.IsSearching = true;
        vm.Search = "LEV";
        CollectionAssert.AreEqual(new object[] { Row(vm, "customer"), Row(vm, "customer.level") }, vm.FormItems.Take(2).ToArray());
        Assert.HasCount(3, vm.FormItems);
        vm.Search = "sku";
        Assert.IsTrue(vm.FormItems.Contains(Row(vm, "items")), "matches inside a collapsed array are revealed");
        vm.IsSearching = false;
        Assert.AreEqual("", vm.Search);
        Assert.IsTrue(vm.FormItems.Count > 10);
        return Task.CompletedTask;
    });

    // ── JSON / 对比 / 预览 ─────────────────────────────────────────────────

    [TestMethod]
    public void Json_mode_round_trips_with_the_form() => Screens.OnUi(() =>
    {
        var workspace = new DocEditorWorkspace();
        DocumentEditorDialogViewModel vm = Open(workspace, document: Order());
        vm.Mode = DocumentEditorMode.Json;
        Assert.AreEqual(BsonText.Pretty(Order(), EjsonMode.Shell), vm.JsonText);

        vm.JsonText = vm.JsonText.Replace("status: \"paid\"", "status: \"shipped\"", StringComparison.Ordinal);
        vm.ApplyJson();
        Assert.AreEqual("shipped", Row(vm, "status").Text);
        Assert.AreEqual(DocumentEditorRowState.Modified, Row(vm, "status").State);

        vm.JsonText = vm.JsonText.Replace("orderNo:", "orderNo", StringComparison.Ordinal);
        vm.ApplyJson();
        Assert.IsTrue(vm.JsonDiagnostics.Count > 0);
        Assert.IsTrue(vm.HasErrors);
        Assert.IsFalse(vm.CanSave);

        vm.Mode = DocumentEditorMode.Form;
        Assert.IsFalse(vm.HasErrors, "leaving JSON with a syntax error falls back to the last valid document");
        Assert.AreEqual(Zh["Doc_JsonDiscarded"], workspace.Toasts[^1].Title);
        Assert.AreEqual("shipped", Row(vm, "status").Text);

        // 表单改了,再进 JSON 模式就是新的文本。
        Row(vm, "paid").BoolValue = false;
        vm.Mode = DocumentEditorMode.Json;
        StringAssert.Contains(vm.JsonText, "paid: false");

        // JSON 里改 _id:不允许。
        vm.JsonText = vm.JsonText.Replace("66f5c2a1d38b5e1a0c7fe3b7", "66f5c2a1d38b5e1a0c7fe3b8", StringComparison.Ordinal);
        vm.ApplyJson();
        Assert.AreEqual(DocumentEditorRowState.Error, Row(vm, "_id").State);
        Assert.AreEqual(Zh["Doc_IdChanged"], Row(vm, "_id").Message);
        Assert.IsFalse(vm.CanSave);
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Json_dotted_keys_get_a_diagnostic_with_a_fix() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: Order());
        vm.Mode = DocumentEditorMode.Json;
        vm.JsonText = "{ _id: ObjectId(\"66f5c2a1d38b5e1a0c7fe3b7\"), customer.level: 'VIP' }";
        vm.ApplyJson();
        Assert.HasCount(1, vm.JsonDiagnostics);
        Assert.AreEqual("\"customer.level\"", vm.JsonDiagnostics[0].FixText);
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Diff_mode_marks_removed_and_added_lines() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: Order());
        Row(vm, "status").Text = "shipped";
        vm.Mode = DocumentEditorMode.Diff;
        string[] left = vm.DiffLeftText.Split('\n');
        string[] right = vm.DiffRightText.Split('\n');
        Assert.HasCount(1, vm.DiffLeftMarks);
        Assert.HasCount(1, vm.DiffRightMarks);
        Assert.AreEqual(LineMarkKind.Removed, vm.DiffLeftMarks[0].Kind);
        Assert.AreEqual(LineMarkKind.Added, vm.DiffRightMarks[0].Kind);
        Assert.AreEqual("  status: \"paid\",", left[vm.DiffLeftMarks[0].Line - 1]);
        Assert.AreEqual("  status: \"shipped\",", right[vm.DiffRightMarks[0].Line - 1]);
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Preview_marks_changed_added_erroneous_and_removed_lines() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), Orders(OrderSchema), Order());
        Row(vm, "status").Text = "shipped";
        DocumentEditorRow discount = vm.AddAfter(Row(vm, "total"))!;
        discount.Name = "discount";
        discount.SetValue(new BsonDouble(-20));
        DocumentEditorRow shipped = vm.AddAfter(Row(vm, "paid"))!;
        shipped.Name = "shippedAt";
        shipped.SetValue(new BsonDateTime(new DateTime(2026, 9, 27, 2, 30, 0, DateTimeKind.Utc)));
        vm.Remove(Row(vm, "note"));
        Row(vm, "items").Children[0].Children.Single(static c => c.Name == "qty").Text = "3";
        vm.RecomputeNow();

        string[] lines = vm.PreviewText.Split('\n');
        LineMarkKind? MarkOf(string start)
        {
            int index = Array.FindIndex(lines, l => l.TrimStart().StartsWith(start, StringComparison.Ordinal));
            Assert.IsGreaterThanOrEqualTo(0, index, start);
            return vm.PreviewMarks.FirstOrDefault(m => m.Line == index + 1)?.Kind;
        }
        Assert.AreEqual("  _id: ObjectId(\"66f5c2a1…e3b7\"),", lines[1]);
        Assert.AreEqual(LineMarkKind.Modified, MarkOf("status:"));
        Assert.AreEqual(LineMarkKind.Removed, MarkOf("discount:"), "a rule violation is red");
        Assert.AreEqual(LineMarkKind.Added, MarkOf("shippedAt:"));
        Assert.AreEqual(LineMarkKind.Removed, MarkOf("// note:"));
        Assert.AreEqual(LineMarkKind.Modified, MarkOf("items:"), "a change inside a folded array marks the array line");
        Assert.IsNull(MarkOf("orderNo:"));
        StringAssert.Contains(vm.PreviewText, "items: [ …2 项 ],");
        StringAssert.Contains(vm.PreviewText, "tags: [ \"企业\", \"开票\" ]\n  // note: null", "the last real field has no trailing comma");
        return Task.CompletedTask;
    });

    // ── 粘贴、补全、新建、只读 ────────────────────────────────────────────

    [TestMethod]
    public void Paste_loads_the_clipboard_document_but_keeps_the_original_id() => Screens.OnUi(() =>
    {
        var workspace = new DocEditorWorkspace();
        DocumentEditorDialogViewModel vm = Open(workspace, document: Order());
        vm.ApplyPastedText("""{ "_id": { "$oid": "000000000000000000000001" }, "orderNo": "SO-X", "n": { "$numberDecimal": "1.50" } }""");
        vm.RecomputeNow();
        Assert.AreEqual("_id", vm.Roots[0].Name);
        Assert.AreEqual("66f5c2a1d38b5e1a0c7fe3b7", vm.Roots[0].Text);
        Assert.AreEqual("SO-X", Row(vm, "orderNo").Text);
        Assert.AreEqual(BsonKind.Decimal128, Row(vm, "n").Kind);
        Assert.AreEqual(DocumentEditorRowState.Added, Row(vm, "n").State);
        Assert.AreEqual(ToastKind.Success, workspace.Toasts[^1].Kind);
        Assert.AreEqual(Zh["Doc_PasteKeptId"], workspace.Toasts[^1].Detail);

        int before = vm.Roots.Count;
        vm.ApplyPastedText("this is not json");
        Assert.AreEqual(ToastKind.Error, workspace.Toasts[^1].Kind);
        vm.ApplyPastedText("   ");
        Assert.AreEqual(Zh["Doc_PasteEmpty"], workspace.Toasts[^1].Title);
        Assert.AreEqual(before, vm.Roots.Count);
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Fill_from_schema_adds_frequent_missing_fields_by_their_dominant_type() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: Order());
        vm.Remove(Row(vm, "note"));
        var sample = new List<BsonDocument>();
        for (int i = 0; i < 10; i++)
        {
            var doc = new BsonDocument { ["_id"] = i, ["status"] = "paid", ["items"] = new BsonArray { new BsonDocument("sku", "A") } };
            if (i < 8)
            {
                doc["note"] = BsonNull.Value;
            }
            if (i < 5)
            {
                doc["invoice"] = new BsonDocument { ["title"] = "某公司", ["amount"] = 3 };
                doc["customer"] = new BsonDocument("vip", true);
            }
            if (i < 2)
            {
                doc["rare"] = 1;
            }
            sample.Add(doc);
        }
        var schema = DocumentEditorSchema.From(sample);
        Assert.AreEqual(10, schema.Sampled);

        List<string> added = vm.FillFrom(schema);
        CollectionAssert.AreEquivalent(new[] { "note", "invoice", "customer.vip", "invoice.title", "invoice.amount" }, added);
        Assert.AreEqual(BsonKind.Null, Row(vm, "note").Kind);
        Assert.AreEqual(BsonKind.Object, Row(vm, "invoice").Kind);
        Assert.AreEqual(BsonKind.String, Row(vm, "invoice.title").Kind);
        Assert.AreEqual(BsonKind.Int32, Row(vm, "invoice.amount").Kind);
        Assert.AreEqual(BsonKind.Boolean, Row(vm, "customer.vip").Kind);
        Assert.IsNull(vm.FindRow("rare"), "fields under the threshold are left out");
        Assert.IsNull(vm.FindRow("items.sku"), "fields inside arrays are never filled");
        Assert.HasCount(0, vm.FillFrom(schema), "a second run has nothing left to fill");
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Field_name_suggestions_come_from_the_sample() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: Order());
        vm.UseSchema(DocumentEditorSchema.From([
            new BsonDocument { ["status"] = "x", ["discount"] = 1.5, ["shippedAt"] = new BsonDateTime(DateTime.UtcNow) },
            new BsonDocument { ["status"] = "x", ["discount"] = 2.5 },
            new BsonDocument { ["customer"] = new BsonDocument("vip", true) }
        ]));
        DocumentEditorRow fresh = vm.AddFieldAtEnd()!;
        IReadOnlyList<DocumentEditorSuggestion> all = vm.SuggestNames(fresh, fresh.Name);
        CollectionAssert.AreEqual(new[] { "discount", "shippedAt" }, all.Select(static s => s.Name).ToArray(), "existing names are left out");
        Assert.AreEqual("67%", all[0].RatioText);
        IReadOnlyList<DocumentEditorSuggestion> typed = vm.SuggestNames(fresh, "ship");
        Assert.HasCount(1, typed);
        vm.AcceptSuggestion(fresh, typed[0]);
        Assert.AreEqual("shippedAt", fresh.Name);
        Assert.AreEqual(BsonKind.Date, fresh.Kind, "a fresh empty field takes the sampled type");

        DocumentEditorRow nested = vm.AddChild(Row(vm, "customer"))!;
        CollectionAssert.AreEqual(new[] { "vip" }, vm.SuggestNames(nested, "").Select(static s => s.Name).ToArray());
        return Task.CompletedTask;
    });

    [TestMethod]
    public void A_new_document_inserts_and_its_id_can_be_edited() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace());
        Assert.AreEqual("新建文档", vm.Title);
        Assert.IsFalse(vm.IsExisting);
        DocumentEditorRow id = vm.Roots.Single();
        Assert.AreEqual(BsonKind.ObjectId, id.Kind);
        Assert.IsFalse(id.IsIdLocked);
        Assert.IsTrue(id.IsValueEditable);
        Assert.IsFalse(id.IsNameEditable);
        Assert.AreEqual("insertOne", vm.CommandName);
        Assert.IsTrue(vm.ShowNewDocument);
        Assert.AreEqual("新文档 · 1 个字段", vm.NewDocumentText);
        Assert.IsTrue(vm.CanSave);
        id.SelectedKind = BsonKind.String;
        id.Text = "SKU-9999";
        vm.RecomputeNow();
        Assert.AreEqual("\"SKU-9999\"", vm.IdText);
        Assert.AreEqual(DocumentEditorRowState.None, id.State, "a new document has nothing to compare against");
        StringAssert.StartsWith(vm.CommandText, "db.orders.insertOne(\n  {\n    _id: \"SKU-9999\"");
        return Task.CompletedTask;
    });

    [TestMethod]
    public void A_view_is_read_only() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), Orders(kind: CollectionKind.View), Order());
        Assert.AreEqual("查看文档", vm.Title);
        Assert.IsFalse(vm.CanEdit);
        Assert.IsFalse(vm.CanSave);
        Assert.IsFalse(vm.CanSaveAsNew);
        Assert.IsNull(vm.AddFieldAtEnd());
        Assert.IsTrue(Row(vm, "status").IsValueReadOnly);
        Assert.IsFalse(Row(vm, "status").IsNameEditable);
        Assert.IsFalse(Row(vm, "status").CanRemove);
        Row(vm, "status").Name = "renamed";
        Assert.IsNotNull(vm.FindRow("status"));
        Assert.AreEqual(Zh["Doc_ViewReadOnly"], vm.FooterHint);
        Assert.IsFalse(vm.AddFieldCommand.CanExecute(null));
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Copy_sends_the_full_document() => Screens.OnUi(async () =>
    {
        var workspace = new DocEditorWorkspace();
        DocumentEditorDialogViewModel vm = Open(workspace, document: Order());
        vm.CopyCommand.Execute(null);
        await Screens.PumpAsync(5);
        Assert.AreEqual(BsonText.Pretty(Order(), EjsonMode.Shell), workspace.Copied);
    });

    [TestMethod]
    public void Picking_a_day_keeps_the_time_of_day() => Screens.OnUi(() =>
    {
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: Order());
        DocumentEditorRow created = Row(vm, "createdAt");
        string time = created.Text[11..];
        created.PickDate(new DateTime(2026, 10, 1));
        Assert.AreEqual("2026-10-01 " + time, created.Text);
        vm.RecomputeNow();
        Assert.AreEqual(DocumentEditorRowState.Modified, created.State);
        return Task.CompletedTask;
    });

    // ── 视图上的交互:拖动排序、粘贴、快捷键、字段名补全、芯片追加 ───────────

    [TestMethod]
    public void The_view_handles_drag_paste_shortcuts_completion_and_chips() => Screens.OnUi(async () =>
    {
        // 只读模式:Ctrl+S 走到写护栏就停,刚好用来验证快捷键接上了保存。
        var workspace = new DocEditorWorkspace(settings: new MongoSettings { ReadOnly = true });
        DocumentEditorDialogViewModel vm = Open(workspace, document: Order());
        vm.UseSchema(DocumentEditorSchema.From([new BsonDocument { ["discount"] = 1.5, ["status"] = "paid" }]));
        var view = new DocumentEditorDialogView(vm);
        var window = new Window { Width = 1100, Height = 680, Content = view };
        window.Show();
        try
        {
            await Screens.PumpAsync();
            ItemsControl list = view.GetVisualDescendants().OfType<ItemsControl>().First(static c => c.Name == "FormList");
            Control RowControl(string path) => list.ContainerFromItem(Row(vm, path)) ?? throw new AssertFailedException(path + " is not realized");
            Avalonia.Point At(Control control, double y) => control.TranslatePoint(new Avalonia.Point(14, y), window)!.Value;

            // 拖动 paid 的手柄,放到 orderNo 那一行的上半。
            Control grip = RowControl("paid").GetVisualDescendants().OfType<Glyph>().First(static g => g.Key == "Mongo.grip-vertical")
                .GetVisualParent<Control>()!;
            window.MouseDown(grip.TranslatePoint(new Avalonia.Point(14, 16), window)!.Value, Avalonia.Input.MouseButton.Left);
            window.MouseMove(At(RowControl("orderNo"), 4), Avalonia.Input.RawInputModifiers.LeftMouseButton);
            window.MouseUp(At(RowControl("orderNo"), 4), Avalonia.Input.MouseButton.Left);
            await Screens.PumpAsync(10);
            CollectionAssert.AreEqual(new[] { "_id", "paid", "orderNo" }, vm.Roots.Take(3).Select(static r => r.Name).ToArray());

            // Ctrl+Enter 在焦点那一行后面加字段;新字段的名字框拿到焦点并弹出抽样补全。
            TextBox orderNoKey = RowControl("orderNo").GetVisualDescendants().OfType<TextBox>().First(static t => t.Name == "KeyBox");
            _ = orderNoKey.Focus();
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Enter, null);
            await Screens.PumpAsync(20);
            int at = vm.Roots.ToList().FindIndex(static r => r.Name == "orderNo");
            DocumentEditorRow fresh = vm.Roots[at + 1];
            Assert.AreEqual("newField", fresh.Name);
            window.KeyTextInput("disc");
            await Screens.PumpAsync(10);
            Assert.AreEqual("disc", fresh.Name);
            Assert.IsTrue(view.FindControl<Avalonia.Controls.Primitives.Popup>("NamePopup")!.IsOpen, "suggestions pop up while typing");
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            await Screens.PumpAsync(10);
            Assert.AreEqual("discount", fresh.Name);
            Assert.AreEqual(BsonKind.Double, fresh.Kind, "the sampled type comes along");

            // 芯片行的「+ String」:点开输入框,回车追加。
            Button add = RowControl("tags").GetVisualDescendants().OfType<Button>().First(static b => b.Classes.Contains("addchip"));
            add.Command!.Execute(null);
            await Screens.PumpAsync(10);
            window.KeyTextInput("新标签");
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            await Screens.PumpAsync(10);
            CollectionAssert.AreEqual(new[] { "企业", "开票", "新标签" }, Row(vm, "tags").Children.Select(static c => c.ChipText).ToArray());

            // Ctrl+F 打开查找;Ctrl+S 走保存(只读模式下被护栏拦住)。
            window.KeyPress(Avalonia.Input.Key.F, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.F, null);
            await Screens.PumpAsync(5);
            Assert.IsTrue(vm.IsSearching);
            vm.IsSearching = false;
            vm.RecomputeNow();
            window.KeyPress(Avalonia.Input.Key.S, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.S, null);
            await Screens.PumpAsync(10);
            Assert.AreEqual(Zh["Common_ReadOnlyBlocked"], workspace.Toasts[^1].Title);

            // 从剪贴板粘贴:读 TopLevel 的剪贴板,保留原 _id。
            Avalonia.Input.Platform.IClipboard? clipboard = TopLevel.GetTopLevel(view)?.Clipboard;
            Assert.IsNotNull(clipboard, "the headless platform provides a clipboard");
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, "{ orderNo: 'SO-PASTE', extra: 1 }");
            view.FindControl<Button>("PasteButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => vm.FindRow("extra") is not null);
            Assert.AreEqual("SO-PASTE", Row(vm, "orderNo").Text);
            Assert.AreEqual("_id", vm.Roots[0].Name);
        }
        finally
        {
            window.Close();
        }
    });

    // ── 纯逻辑:更新计划、逐行对比、命令与预览的文本 ──────────────────────

    [TestMethod]
    public void Plan_sets_only_changed_paths_and_unsets_removed_ones()
    {
        BsonDocument before = Order();
        BsonDocument after = Order();
        after["status"] = "shipped";
        after["customer"]["level"] = "VIP";
        after["items"][0]["qty"] = 3;
        after.Remove("note");
        after["discount"] = -20.0;
        DocumentEditorPlan plan = DocumentEditorDiff.Plan(before, after);
        Assert.IsFalse(plan.Replace);
        CollectionAssert.AreEqual(new[] { "customer.level", "items.0.qty", "status", "discount" }, plan.Set.Names.ToArray());
        CollectionAssert.AreEqual(new[] { "note" }, plan.Unset.Names.ToArray());
        Assert.IsTrue(DocumentEditorDiff.Plan(Order(), Order()).IsEmpty);
    }

    [TestMethod]
    public void Plan_sets_whole_values_when_shapes_change()
    {
        BsonDocument before = Order();
        BsonDocument after = Order();
        _ = after["tags"].AsBsonArray.Add("新");
        after["customer"] = new BsonDocument { ["name"] = "张伟", ["id"] = before["customer"]["id"], ["level"] = "SVIP" };
        after["paid"] = "yes";
        DocumentEditorPlan plan = DocumentEditorDiff.Plan(before, after);
        CollectionAssert.AreEquivalent(new[] { "customer", "paid", "tags" }, plan.Set.Names.ToArray());
    }

    [TestMethod]
    public void Plan_replaces_on_reorder_or_unaddressable_names()
    {
        BsonDocument before = Order();
        var reordered = new BsonDocument(before.Elements.Reverse());
        Assert.IsTrue(DocumentEditorDiff.Plan(before, reordered).Replace);

        BsonDocument dotted = Order();
        dotted["a.b"] = 1;
        DocumentEditorPlan plan = DocumentEditorDiff.Plan(before, dotted);
        Assert.IsTrue(plan.Replace);
        Assert.AreEqual(0, plan.Set.ElementCount);

        BsonDocument appended = Order();
        appended["zzz"] = 1;
        Assert.IsFalse(DocumentEditorDiff.Plan(before, appended).Replace, "a new field anywhere is still a $set");
    }

    [TestMethod]
    public void Apply_replays_a_plan_onto_another_version()
    {
        BsonDocument before = Order();
        BsonDocument mine = Order();
        mine["status"] = "shipped";
        mine.Remove("note");
        mine["customer"]["level"] = "VIP";
        BsonDocument theirs = Order();
        theirs["paid"] = false;
        BsonDocument merged = DocumentEditorDialogViewModel.Apply(DocumentEditorDiff.Plan(before, mine), theirs);
        Assert.AreEqual("shipped", merged["status"].AsString);
        Assert.IsFalse(merged.Contains("note"));
        Assert.AreEqual("VIP", merged["customer"]["level"].AsString);
        Assert.IsFalse(merged["paid"].AsBoolean, "their change survives");
    }

    [TestMethod]
    public void Line_diff_finds_the_changed_lines()
    {
        (HashSet<int> removed, HashSet<int> added) = DocumentEditorDiff.Lines(["{", "  a: 1,", "  b: 2", "}"], ["{", "  a: 1,", "  c: 3,", "  b: 2", "}"]);
        Assert.HasCount(0, removed);
        CollectionAssert.AreEqual(new[] { 3 }, added.ToArray());
        (removed, added) = DocumentEditorDiff.Lines(["x", "y", "z"], ["x", "Y", "z"]);
        CollectionAssert.AreEqual(new[] { 2 }, removed.ToArray());
        CollectionAssert.AreEqual(new[] { 2 }, added.ToArray());
        (_, added) = DocumentEditorDiff.Lines([], ["a", "b"]);
        CollectionAssert.AreEquivalent(new[] { 1, 2 }, added.ToArray());
    }

    [TestMethod]
    public void Command_text_follows_the_design_layout()
    {
        var update = new BsonDocument("$set", new BsonDocument { ["status"] = "shipped", ["discount"] = -20.0 });
        string text = DocumentEditorFormat.Command("orders", "updateOne", new BsonDocument("_id", ObjectId.Parse("66f5c2a1d38b5e1a0c7fe3b7")), update);
        Assert.AreEqual(
            "db.orders.updateOne(\n  { _id: ObjectId(\"66f5c2a1d38b5e1a0c7fe3b7\") },\n  { $set: {\n      status: \"shipped\",\n      discount: -20\n  } }\n)",
            text);
        update["$unset"] = new BsonDocument("note", "");
        string two = DocumentEditorFormat.Command("orders", "updateOne", new BsonDocument("_id", 1), update);
        StringAssert.Contains(two, "      discount: -20\n    },\n    $unset: {\n      note: \"\"\n  } }\n)");
    }

    [TestMethod]
    public void Version_field_goes_into_the_filter_and_is_refreshed() => Screens.OnUi(() =>
    {
        BsonDocument order = Order();
        order["updatedAt"] = new BsonDateTime(new DateTime(2026, 9, 27, 1, 0, 0, DateTimeKind.Utc));
        DocumentEditorDialogViewModel vm = Open(new DocEditorWorkspace(), document: order);
        Row(vm, "status").Text = "shipped";
        vm.RecomputeNow();
        Assert.AreEqual(Zh["Doc_OnlyChanged"], vm.CommandNote);
        StringAssert.Contains(vm.CommandText, "{ _id: ObjectId(\"66f5c2a1d38b5e1a0c7fe3b7\"), updatedAt: ISODate(\"2026-09-27T01:00:00Z\") }");
        StringAssert.Contains(vm.CommandText, "$currentDate: {\n      updatedAt: true");

        // 用户自己改了 updatedAt,就不替他刷新。
        Row(vm, "updatedAt").Text = "2026-10-01 08:00:00";
        vm.RecomputeNow();
        Assert.IsFalse(vm.CommandText.Contains("$currentDate", StringComparison.Ordinal));
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Sampled_schema_counts_documents_and_dominant_types()
    {
        var schema = DocumentEditorSchema.From([
            new BsonDocument { ["a"] = 1, ["items"] = new BsonArray { new BsonDocument("sku", "x"), new BsonDocument("sku", "y") } },
            new BsonDocument { ["a"] = "s", ["items"] = new BsonArray() },
            new BsonDocument { ["a"] = 2 }
        ]);
        DocumentEditorFieldStat a = schema.Fields.Single(static f => f.Path == "a");
        Assert.AreEqual(3, a.Documents);
        Assert.AreEqual(BsonKind.Int32, a.Dominant);
        DocumentEditorFieldStat sku = schema.Fields.Single(static f => f.Path == "items.sku");
        Assert.AreEqual(1, sku.Documents, "an array of documents counts once per document");
        Assert.AreEqual("items", sku.Parent);
        Assert.AreEqual(1.0 / 3, schema.Ratio(sku), 1e-9);
        CollectionAssert.AreEqual(new[] { "a", "items" }, schema.ChildrenOf("").Select(static f => f.Name).ToArray());
    }

    [TestMethod]
    public void Every_editor_text_exists_in_both_languages()
    {
        string source = File.ReadAllText(Path.Combine(MongoStylesTests.RepoRoot(), "plugins", "VelaShell.Plugin.Mongo", "Loc.DocumentEditor.cs"));
        var en = new Loc("en");
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(source, "\\(\"((?:Doc|Schema)_[A-Za-z]+)\""))
        {
            string key = match.Groups[1].Value;
            Assert.AreNotEqual(key, Zh[key], key);
            Assert.AreNotEqual(key, en[key], key);
        }
    }

    // ── 打真实 MongoDB 的保存路径 ─────────────────────────────────────────

    private static async Task<(MongoConnection Connection, IMongoCollection<BsonDocument> Collection, string Database)> ScratchAsync()
    {
        MongoConnection connection = await TestServer.OpenAsync();
        string database = "velashell_doceditor_" + Guid.NewGuid().ToString("N")[..8];
        IMongoCollection<BsonDocument> collection = connection.Collection(database, "orders");
        return (connection, collection, database);
    }

    private static CollectionInfo Info(string database, string? validator = null) =>
        new(database, "orders", CollectionKind.Collection, validator is null ? [] : new BsonDocument("validator", ShellJson.ParseDocument(validator)));

    [TestMethod]
    [TestCategory("Integration")]
    public void Save_sends_only_the_changed_fields() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        (MongoConnection connection, IMongoCollection<BsonDocument> collection, string database) = await ScratchAsync();
        try
        {
            BsonDocument order = Order();
            await collection.InsertOneAsync(order.DeepClone().AsBsonDocument);
            var workspace = new DocEditorWorkspace(connection);
            BsonDocument? savedDoc = null;
            DocumentEditorDialogViewModel vm = Open(workspace, Info(database), order, doc =>
            {
                savedDoc = doc;
                return Task.CompletedTask;
            });
            Row(vm, "status").Text = "shipped";
            vm.Remove(Row(vm, "note"));
            vm.RecomputeNow();

            // 编辑期间别人改了另一个字段:只 $set 变更字段时,这次修改不会被抹掉。
            _ = await collection.UpdateOneAsync(new BsonDocument("_id", order["_id"]), new BsonDocument("$set", new BsonDocument("paid", false)));

            vm.SaveCommand.Execute(null);
            await WaitAsync(() => workspace.ClosedDialogs.Count > 0);

            BsonDocument stored = await collection.Find(new BsonDocument("_id", order["_id"])).FirstAsync();
            Assert.AreEqual("shipped", stored["status"].AsString);
            Assert.IsFalse(stored.Contains("note"));
            Assert.IsFalse(stored["paid"].AsBoolean, "the concurrent change survives");
            Assert.IsNotNull(savedDoc);
            Assert.AreEqual(stored, savedDoc);
            Assert.AreEqual(ToastKind.Success, workspace.Toasts[^1].Kind);
            Assert.HasCount(0, workspace.Confirms, "a development connection does not ask");
        }
        finally
        {
            await connection.Client.DropDatabaseAsync(database);
            await connection.DisposeAsync();
        }
    });

    [TestMethod]
    [TestCategory("Integration")]
    public void A_changed_updatedAt_is_a_conflict_and_redo_reapplies_the_edits() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        (MongoConnection connection, IMongoCollection<BsonDocument> collection, string database) = await ScratchAsync();
        try
        {
            BsonDocument order = Order();
            order["updatedAt"] = new BsonDateTime(new DateTime(2026, 9, 27, 1, 0, 0, DateTimeKind.Utc));
            await collection.InsertOneAsync(order.DeepClone().AsBsonDocument);
            var workspace = new DocEditorWorkspace(connection);
            DocumentEditorDialogViewModel vm = Open(workspace, Info(database), order);
            Row(vm, "status").Text = "shipped";
            vm.RecomputeNow();

            var later = new BsonDateTime(new DateTime(2026, 9, 27, 3, 0, 0, DateTimeKind.Utc));
            _ = await collection.UpdateOneAsync(new BsonDocument("_id", order["_id"]),
                new BsonDocument("$set", new BsonDocument { ["paid"] = false, ["updatedAt"] = later }));

            vm.SaveCommand.Execute(null);
            await WaitAsync(() => workspace.Toasts.Count > 0);
            ToastRequest conflict = workspace.Toasts[^1];
            Assert.AreEqual(Zh["Doc_Conflict"], conflict.Title);
            Assert.IsNotNull(conflict.Action);
            Assert.HasCount(0, workspace.ClosedDialogs, "the editor stays open with the edits");
            BsonDocument untouched = await collection.Find(new BsonDocument("_id", order["_id"])).FirstAsync();
            Assert.AreEqual("paid", untouched["status"].AsString, "nothing is overwritten");

            await conflict.Action!();
            Assert.AreEqual(later, vm.Original!["updatedAt"]);
            Assert.AreEqual("shipped", Row(vm, "status").Text);
            Assert.IsFalse(Row(vm, "paid").BoolValue, "the other change is the new baseline");
            Assert.IsTrue(vm.CanSave);

            vm.SaveCommand.Execute(null);
            await WaitAsync(() => workspace.ClosedDialogs.Count > 0);
            BsonDocument stored = await collection.Find(new BsonDocument("_id", order["_id"])).FirstAsync();
            Assert.AreEqual("shipped", stored["status"].AsString);
            Assert.IsFalse(stored["paid"].AsBoolean);
            Assert.IsGreaterThan(later.ToUniversalTime(), stored["updatedAt"].ToUniversalTime(), "updatedAt is refreshed");
        }
        finally
        {
            await connection.Client.DropDatabaseAsync(database);
            await connection.DisposeAsync();
        }
    });

    [TestMethod]
    [TestCategory("Integration")]
    public void Save_as_new_and_new_documents_insert() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        (MongoConnection connection, IMongoCollection<BsonDocument> collection, string database) = await ScratchAsync();
        try
        {
            BsonDocument order = Order();
            await collection.InsertOneAsync(order.DeepClone().AsBsonDocument);
            var workspace = new DocEditorWorkspace(connection);
            BsonDocument? copy = null;
            DocumentEditorDialogViewModel vm = Open(workspace, Info(database), order, doc =>
            {
                copy = doc;
                return Task.CompletedTask;
            });
            Row(vm, "orderNo").Text = "SO2609-99999";
            vm.SaveAsNewCommand.Execute(null);
            await WaitAsync(() => workspace.ClosedDialogs.Count > 0);
            Assert.AreEqual(2, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            Assert.IsNotNull(copy);
            Assert.AreNotEqual(order["_id"], copy["_id"]);
            Assert.AreEqual("SO2609-10403", (await collection.Find(new BsonDocument("_id", order["_id"])).FirstAsync())["orderNo"].AsString,
                "the original is untouched");

            DocumentEditorDialogViewModel fresh = Open(workspace, Info(database));
            DocumentEditorRow name = fresh.AddFieldAtEnd()!;
            name.Name = "orderNo";
            name.Text = "SO-NEW";
            fresh.SaveCommand.Execute(null);
            await WaitAsync(() => workspace.ClosedDialogs.Count > 1);
            Assert.AreEqual(1, await collection.CountDocumentsAsync(new BsonDocument("orderNo", "SO-NEW")));
        }
        finally
        {
            await connection.Client.DropDatabaseAsync(database);
            await connection.DisposeAsync();
        }
    });

    [TestMethod]
    [TestCategory("Integration")]
    public void Read_only_mode_and_write_confirmation_guard_the_save() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        (MongoConnection connection, IMongoCollection<BsonDocument> collection, string database) = await ScratchAsync();
        try
        {
            BsonDocument order = Order();
            await collection.InsertOneAsync(order.DeepClone().AsBsonDocument);

            var readOnly = new DocEditorWorkspace(connection, new MongoSettings { ReadOnly = true });
            DocumentEditorDialogViewModel vm = Open(readOnly, Info(database), order);
            Row(vm, "status").Text = "shipped";
            vm.RecomputeNow();
            Assert.AreEqual(Zh["Doc_ReadOnlyMode"], vm.FooterHint);
            vm.SaveCommand.Execute(null);
            await Screens.PumpAsync(10);
            Assert.AreEqual(Zh["Common_ReadOnlyBlocked"], readOnly.Toasts[^1].Title);

            var production = new DocEditorWorkspace(connection, new MongoSettings { ConfirmWrites = true, Environment = MongoEnvironment.Production })
            {
                ConfirmResult = false
            };
            vm = Open(production, Info(database), order);
            Row(vm, "status").Text = "shipped";
            vm.RecomputeNow();
            vm.SaveCommand.Execute(null);
            await WaitAsync(() => production.Confirms.Count > 0);
            await Screens.PumpAsync(10);
            Assert.AreEqual(Zh["Doc_ConfirmTitle"], production.Confirms[0].Title);
            Assert.IsFalse(production.Confirms[0].Danger);
            Assert.AreEqual("updateOne", production.Confirms[0].Facts[0].Value);
            Assert.AreEqual("status", production.Confirms[0].Facts[1].Value);
            Assert.AreEqual("paid", (await collection.Find(new BsonDocument("_id", order["_id"])).FirstAsync())["status"].AsString);

            production.ConfirmResult = true;
            vm.SaveCommand.Execute(null);
            await WaitAsync(() => production.ClosedDialogs.Count > 0);
            Assert.AreEqual("shipped", (await collection.Find(new BsonDocument("_id", order["_id"])).FirstAsync())["status"].AsString);
        }
        finally
        {
            await connection.Client.DropDatabaseAsync(database);
            await connection.DisposeAsync();
        }
    });

    [TestMethod]
    [TestCategory("Integration")]
    public void A_server_side_rejection_is_reported() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        (MongoConnection connection, IMongoCollection<BsonDocument> collection, string database) = await ScratchAsync();
        try
        {
            // 查询表达式写的规则客户端不评估:放过去,由服务器拒(121)。
            const string validator = "{ status: { $in: ['paid', 'shipped'] } }";
            await connection.Database(database).CreateCollectionAsync("orders",
                new CreateCollectionOptions<BsonDocument> { Validator = ShellJson.ParseDocument(validator) });
            BsonDocument order = Order();
            await collection.InsertOneAsync(order.DeepClone().AsBsonDocument);
            var workspace = new DocEditorWorkspace(connection);
            DocumentEditorDialogViewModel vm = Open(workspace, Info(database, validator), order);
            Row(vm, "status").Text = "bogus";
            vm.RecomputeNow();
            Assert.IsTrue(vm.CanSave);
            vm.SaveCommand.Execute(null);
            await WaitAsync(() => workspace.Toasts.Count > 0);
            Assert.AreEqual(Zh["Doc_ServerRejected"], workspace.Toasts[^1].Title);
            Assert.HasCount(0, workspace.ClosedDialogs);
        }
        finally
        {
            await connection.Client.DropDatabaseAsync(database);
            await connection.DisposeAsync();
        }
    });

    [TestMethod]
    [TestCategory("Integration")]
    public void Sampling_reads_the_collection() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using MongoConnection connection = await TestServer.OpenAsync();
        var workspace = new DocEditorWorkspace(connection);
        DocumentEditorDialogViewModel vm = Open(workspace, new CollectionInfo(Screens.Database, "orders", CollectionKind.Collection, []),
            await connection.Collection(Screens.Database, "orders").Find(FilterDefinition<BsonDocument>.Empty).FirstOrDefaultAsync());
        DocumentEditorSchema? schema = await vm.EnsureSampleAsync(reportErrors: true);
        if (schema is null || schema.Sampled == 0)
        {
            Assert.Inconclusive("the screenshot database has no orders");
        }
        Assert.IsTrue(schema.Fields.Any(static f => f.Path == "orderNo"));
        Assert.AreSame(schema, await vm.EnsureSampleAsync(reportErrors: true), "sampled once");
    });

    // ── 截图 ────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board05_Document_editor() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        IMongoCollection<BsonDocument> orders = bench.Connection.Collection(Screens.Database, "orders");
        BsonDocument? document = await orders.Find(new BsonDocument("orderNo", "SO2609-10403")).FirstOrDefaultAsync()
                                 ?? await orders.Find(FilterDefinition<BsonDocument>.Empty).FirstOrDefaultAsync();
        if (document is null)
        {
            Assert.Inconclusive("the screenshot database has no orders");
        }
        // 验证规则用测试里构造的(shop.orders 本身不带规则;设计稿里那条 discount ≥ 0 / level 枚举)。
        var info = new CollectionInfo(Screens.Database, "orders", CollectionKind.Collection, new BsonDocument("validator", ShellJson.ParseDocument(OrderSchema)));
        var vm = new DocumentEditorDialogViewModel(bench.Session, info, document, null);
        bench.ViewModel.ShowDialog(vm);
        await Screens.PumpAsync();

        // 改一个值、加一个字段、制造一个违反验证规则的值(只在编辑器里,不保存)。
        Row(vm, "status").Text = "shipped";
        DocumentEditorRow discount = vm.AddAfter(Row(vm, "total"))!;
        discount.Name = "discount";
        discount.SetValue(new BsonDouble(-20));
        DocumentEditorRow shipped = vm.AddAfter(Row(vm, "paid"))!;
        shipped.Name = "shippedAt";
        shipped.SetValue(new BsonDateTime(new DateTime(2026, 9, 27, 2, 30, 0, DateTimeKind.Utc)));
        vm.RecomputeNow();
        await Screens.PumpAsync(40);

        // 焦点放到 customer.name 的值上(设计稿那一格是聚焦态)。
        DocumentEditorDialogView view = bench.View.GetVisualDescendants().OfType<DocumentEditorDialogView>().Single();
        Control? nameRow = view.GetVisualDescendants().OfType<ItemsControl>().First(static c => c.Name == "FormList")
            .ContainerFromItem(Row(vm, "customer.name"));
        _ = (nameRow?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(static t => t.Name != "KeyBox" && t.IsEffectivelyVisible)?.Focus());
        await Screens.PumpAsync(40);

        WriteableBitmap? frame = Screens.Capture(bench.Window, "05-doceditor");
        Assert.IsNotNull(frame);
        Assert.AreEqual(DocumentEditorRowState.Modified, Row(vm, "status").State);
        Assert.AreEqual(DocumentEditorRowState.Added, shipped.State);
        Assert.AreEqual(DocumentEditorRowState.Error, discount.State);
        Assert.IsFalse(vm.CanSave);
        vm.Close();
    });

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
            {
                Assert.Fail("timed out waiting for the editor");
            }
            await Screens.PumpAsync(2);
        }
    }
}

/// <summary>测试用的外壳:记下提示、确认与关闭,连接可有可无。</summary>
internal sealed class DocEditorWorkspace : IMongoWorkspace
{
    private readonly TestPluginContext _context = new();

    public DocEditorWorkspace(MongoConnection? connection = null, MongoSettings? settings = null, string locale = "zh-CN")
    {
        Connection = connection;
        Loc = new Loc(locale);
        Store = new MongoStore(_context);
        Guard = new WriteGuard(settings ?? new MongoSettings(), PrivilegeSummary.Unrestricted);
    }

    public List<ToastRequest> Toasts { get; } = [];

    public List<ConfirmRequest> Confirms { get; } = [];

    public List<DialogViewModel> ClosedDialogs { get; } = [];

    public bool ConfirmResult { get; set; } = true;

    public string? Copied { get; private set; }

    public Loc Loc { get; }

    [AllowNull]
    public MongoConnection Connection => field ?? throw new InvalidOperationException("This test has no server.");

    public MongoStore Store { get; }

    public IPluginLogger Log => _context.Log;

    public string ConnectionKey => "127.0.0.1:27017";

    public string ConnectionName => "mongo-test";

    public WriteGuard Guard { get; }

    public (string? Database, string? Collection) Scope => ("shop", "orders");

    public IReadOnlyList<string> Databases => [];

    public bool EnsureWritable(string database)
    {
        if (Guard.Check(database) is not { } reason)
        {
            return true;
        }
        Toasts.Add(new ToastRequest { Title = Loc[reason], Kind = ToastKind.Warning });
        return false;
    }

    public IReadOnlyList<CollectionInfo> CollectionsOf(string database) => [];

    public void OpenCollection(string database, string collection, string? filter = null)
    {
    }

    public void OpenQuery(string database, string? text = null, bool run = false)
    {
    }

    public void OpenPipeline(string database, string collection, BsonArray? pipeline = null)
    {
    }

    public void OpenDesign(string database, string collection, DesignPage page = DesignPage.Indexes)
    {
    }

    public void OpenGridFs(string database, string bucket)
    {
    }

    public void OpenObjects(string database, ObjectFilter filter = ObjectFilter.All)
    {
    }

    public void OpenMonitor()
    {
    }

    public void OpenProfiler(string database)
    {
    }

    public void OpenUsers(string? database, bool roles = false)
    {
    }

    public void ShowDialog(DialogViewModel dialog)
    {
    }

    public void CloseDialog(DialogViewModel dialog)
    {
        ClosedDialogs.Add(dialog);
        dialog.OnClosed();
    }

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        Confirms.Add(request);
        return Task.FromResult(ConfirmResult);
    }

    public void Toast(ToastRequest toast) => Toasts.Add(toast);

    public Task CopyAsync(string text)
    {
        Copied = text;
        return Task.CompletedTask;
    }

    public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileKind> kinds) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileKind> kinds, bool multiple = false) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public Task RefreshTreeAsync(string? database = null) => Task.CompletedTask;

    public void NotifyStatusChanged()
    {
    }
}
