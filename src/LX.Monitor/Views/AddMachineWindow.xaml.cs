using System.Windows;
using System.Windows.Input;
using LingXi.Monitor.ViewModels;

namespace LingXi.Monitor.Views;

/// <summary>
/// 添加机器悬浮窗（机器状态标题行 "+ 添加机器"）：自绘玻璃窗，Owner=主窗口中心、不进任务栏。
/// 段 1 局域网 agent 接入（Hub 地址 / Token / 部署命令 + 复制，复用 CopyToClipboardCommand）；
/// 段 2 上报目标摘要 + "管理上报目标…"二级入口（ReporterManagerWindow）。
/// </summary>
public partial class AddMachineWindow : Window
{
    private AddMachineWindow(DashboardViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    /// <summary>模态弹出；Owner 取当前活动窗口或主窗口。</summary>
    public static void Show(DashboardViewModel vm)
    {
        var window = new AddMachineWindow(vm)
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
