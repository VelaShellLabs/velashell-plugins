using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>集合设计 · Schema 分析页。代码里只建 <c>$sample</c> 数量的下拉菜单。</summary>
public sealed partial class DesignSchemaView : UserControl
{
    /// <summary>构造。</summary>
    public DesignSchemaView()
    {
        InitializeComponent();
        SamplePick.Click += OnSamplePick;
    }

    private void OnSamplePick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DesignTabViewModel vm)
        {
            return;
        }
        var items = new List<Control>();
        foreach (int size in DesignTabViewModel.SampleSizes)
        {
            var item = new MenuItem
            {
                Header = BsonText.Grouped(size),
                Command = vm.SetSampleCommand,
                CommandParameter = size.ToString(CultureInfo.InvariantCulture),
                Icon = size == vm.SampleSize ? new Glyph { Key = "Mongo.check", Size = 12, Brush = ThemeBrushes.Get("VelaAccent", Avalonia.Media.Brushes.Gray) } : null
            };
            items.Add(item);
        }
        new ContextMenu { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft, PlacementTarget = SamplePick }.Open(SamplePick);
    }
}
