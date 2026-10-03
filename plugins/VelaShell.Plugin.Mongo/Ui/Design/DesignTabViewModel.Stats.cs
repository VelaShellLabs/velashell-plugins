using System.Collections.ObjectModel;
using System.Globalization;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>集合设计 · 统计(<c>$collStats</c> 的要点 + 各索引大小 + 原文)。</summary>
internal sealed partial class DesignTabViewModel
{
    private CollectionStats? _stats;
    private string _statsRaw = "";

    /// <summary>指标卡。</summary>
    public ObservableCollection<StatCard> StatCards { get; } = [];

    /// <summary>各索引大小条。</summary>
    public ObservableCollection<IndexSizeBar> IndexSizeBars { get; } = [];

    /// <summary>原始 JSON。</summary>
    public string StatsRaw
    {
        get => _statsRaw;
        private set => SetProperty(ref _statsRaw, value);
    }

    /// <summary>刷新统计。</summary>
    public AsyncCommand RefreshStatsCommand { get; private set; } = null!;

    /// <summary>复制原始 JSON。</summary>
    public AsyncCommand CopyStatsCommand { get; private set; } = null!;

    private void InitializeStats()
    {
        RefreshStatsCommand = new(LoadStatsAsync);
        CopyStatsCommand = new(async () =>
        {
            await Workspace.CopyAsync(_statsRaw).ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc["Common_Copied"], Kind = ToastKind.Success });
        });
    }

    /// <summary>加载统计页。</summary>
    private async Task LoadStatsAsync()
    {
        try
        {
            CollectionStats stats = await Workspace.Connection.GetStatsAsync(Database, CollectionName, Lifetime).ConfigureAwait(true);
            _stats = stats;
            double ratio = stats.StorageSize > 0 ? (double)stats.Size / stats.StorageSize : 0;
            bool reclaimable = stats.StorageSize > 0 && stats.FreeStorageSize > stats.StorageSize / 5;
            StatCards.Clear();
            StatCards.Add(new(Loc["Design_StatDocs"], BsonText.Grouped(stats.Count), Loc.Format("Design_StatAvg", BsonText.Bytes(stats.AvgObjSize))));
            StatCards.Add(new(Loc["Design_StatData"], BsonText.Bytes(stats.Size), Loc["Design_StatUncompressed"]));
            StatCards.Add(new(Loc["Design_StatStorage"], BsonText.Bytes(stats.StorageSize), stats.Engine));
            StatCards.Add(new(Loc["Design_StatRatio"], ratio > 0 ? ratio.ToString("0.0", CultureInfo.InvariantCulture) + "×" : "—", Loc["Design_StatRatioSub"], "VelaShellGreen"));
            StatCards.Add(new(Loc["Design_StatIndexes"], BsonText.Bytes(stats.TotalIndexSize), Loc.Format("Design_StatIndexCount", stats.IndexCount)));
            StatCards.Add(new(Loc["Design_StatFree"], BsonText.Bytes(stats.FreeStorageSize),
                reclaimable ? Loc["Design_StatFreeCompact"] : Loc["Design_StatFreeOk"], reclaimable ? "VelaWarning" : "VelaTextPrimary"));
            IndexSizeBars.Clear();
            long total = Math.Max(1, stats.IndexSizes.Values.Sum());
            foreach ((string name, long size) in stats.IndexSizes.OrderByDescending(static p => p.Value))
            {
                IndexSizeBars.Add(new(name, BsonText.Bytes(size), (double)size / total));
            }
            StatsRaw = BsonText.Pretty(stats.Raw);
            _statsLoaded = true;
            UpdateStatus();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Fail(ex);
        }
    }
}
