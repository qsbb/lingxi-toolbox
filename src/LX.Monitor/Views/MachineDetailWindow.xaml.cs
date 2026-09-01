using System.Windows;
using System.Windows.Input;
using LingXi.Monitor.ViewModels;

namespace LingXi.Monitor.Views;

/// <summary>
/// 机器详情悬浮窗（卡片 ⋮ → 显示详情）：自绘玻璃窗，Owner=主窗口中心、不进任务栏。
/// 非模态、单例复用：再次"显示详情"换 DataContext 并激活；DataContext=MachineCardVm，
/// 快照差量更新同一实例 → 窗口打开期间内容实时刷新。
/// </summary>
public partial class MachineDetailWindow : Window
{
    private static MachineDetailWindow? _active;

    private MachineDetailWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _active = null;
    }

    /// <summary>非模态弹出（同窗口复用换卡）；Owner 取当前活动窗口或主窗口。</summary>
    public static void Show(MachineCardVm card)
    {
        var window = _active;
        if (window is null)
        {
            window = new MachineDetailWindow
            {
                Owner = Application.Current?.Windows
                    .OfType<Window>()
                    .FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow,
            };
            window.Show();
        }
        else
        {
            window.Activate();
        }
        window.Title = $"机器详情 · {card.DisplayName}";
        window.DataContext = card;
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
