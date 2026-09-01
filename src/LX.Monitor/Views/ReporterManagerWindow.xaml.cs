using System.Windows;
using System.Windows.Input;
using LingXi.Monitor.ViewModels;

namespace LingXi.Monitor.Views;

/// <summary>
/// 上报目标管理悬浮窗（原仪表盘平铺卡迁入；入口：机器详情"上报设置…" / 添加机器窗"管理上报目标…"）。
/// 自绘玻璃窗，Owner=当前活动窗口中心、不进任务栏；命令全部复用 DashboardViewModel（增删改/启停即时保存生效）。
/// </summary>
public partial class ReporterManagerWindow : Window
{
    private ReporterManagerWindow(DashboardViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    /// <summary>模态弹出；Owner 取当前活动窗口或主窗口。</summary>
    public static void Show(DashboardViewModel vm)
    {
        var window = new ReporterManagerWindow(vm)
        {
            Owner = Application.Current?.Windows
                .OfType<Window>()
                .FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow,
        };
        window.ShowDialog();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
