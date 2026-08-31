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
}
