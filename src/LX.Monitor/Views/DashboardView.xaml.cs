using System.Windows;
using System.Windows.Controls;

namespace LingXi.Monitor.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdateFallback();
        UpdateFallback();
    }

    private void UpdateFallback()
    {
        var unavailable = DataContext is null;
        FallbackText.Visibility = unavailable ? Visibility.Visible : Visibility.Collapsed;
        MainScroll.Visibility = unavailable ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>卡片"⋮"按钮 → 打开其 ContextMenu（菜单 DataContext 经 PlacementTarget 绑到卡片 VM）。</summary>
    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
}
