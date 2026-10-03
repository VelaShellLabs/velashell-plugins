using Avalonia.Controls;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>连接中 / 连接失败的占位标签(设计稿 22)。</summary>
public sealed partial class ConnectionStateTabView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    /// <param name="viewModel">视图模型。</param>
    internal ConnectionStateTabView(ConnectionStateTabViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>设计器用的无参构造。</summary>
    public ConnectionStateTabView()
    {
        InitializeComponent();
    }
}
