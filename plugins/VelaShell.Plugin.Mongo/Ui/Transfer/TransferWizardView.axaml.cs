using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>数据传输向导的视图。代码里只做日志自动滚到底(新的一行总在视野里)。</summary>
public sealed partial class TransferWizardView : UserControl
{
    private TransferWizardViewModel? _subscribed;

    /// <summary>用给定的视图模型初始化。</summary>
    internal TransferWizardView(TransferWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public TransferWizardView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot is not null)
        {
            Subscribe();
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 对话框被确认框临时顶掉、或转入后台时视图会被拆掉:解除订阅,免得视图模型反过来钉住视图。
        Unsubscribe();
        base.OnDetachedFromVisualTree(e);
    }

    private void Subscribe()
    {
        Unsubscribe();
        if (DataContext is TransferWizardViewModel viewModel)
        {
            viewModel.Log.CollectionChanged += OnLogChanged;
            _subscribed = viewModel;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed is not null)
        {
            _subscribed.Log.CollectionChanged -= OnLogChanged;
            _subscribed = null;
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
}
