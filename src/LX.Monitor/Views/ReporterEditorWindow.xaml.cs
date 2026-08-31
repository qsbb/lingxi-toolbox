using System.Windows;
using LingXi.Monitor.Core;

namespace LingXi.Monitor.Views;

/// <summary>上报目标编辑对话框（新增/编辑共用；确定返回填好的目标，取消/Esc 返回 null）。</summary>
public partial class ReporterEditorWindow : Window
{
    private ReporterEditorWindow(ReporterTarget? existing)
    {
        InitializeComponent();
        var isEdit = existing is not null;
        Title = isEdit ? "编辑上报目标" : "添加上报目标";
        HeaderText.Text = isEdit ? "编辑上报目标" : "添加上报目标";
        var source = existing ?? new ReporterTarget();
        UrlBox.Text = source.Url;
        TokenBox.Text = source.Token;
        NameBox.Text = source.Name;
        IntervalBox.Text = Math.Max(5, source.IntervalSec).ToString();
        TimeoutBox.Text = Math.Max(1000, source.TimeoutMs).ToString();
        EnabledSwitch.IsChecked = source.Enabled;
        Loaded += (_, _) =>
        {
            UrlBox.Focus();
            UrlBox.SelectAll();
        };
    }

    /// <summary>模态弹出。existing 为编辑源（内部克隆展示，不直接改原对象）；返回编辑后的新实例，取消返回 null。</summary>
    public static ReporterTarget? Show(ReporterTarget? existing)
    {
        var window = new ReporterEditorWindow(existing is null ? null : Clone(existing));
        window.Owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        return window.ShowDialog() == true ? window.BuildResult() : null;
    }

    private static ReporterTarget Clone(ReporterTarget t) => new()
    {
        Url = t.Url,
        Token = t.Token,
        Name = t.Name,
        Enabled = t.Enabled,
        IntervalSec = t.IntervalSec,
        TimeoutMs = t.TimeoutMs,
    };

    /// <summary>校验并构建结果；不合法时在窗内显示错误（返回 null 保持弹窗打开）。</summary>
    private ReporterTarget? BuildResult()
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0)
        {
            return Fail("Report URL 不能为空。");
        }
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Report URL 需以 http:// 或 https:// 开头。");
        }
        if (!int.TryParse(IntervalBox.Text.Trim(), out var interval) || interval < 5)
        {
            return Fail("间隔需为不小于 5 的整数（秒），与官方 agent 的最小间隔一致。");
        }
        if (!int.TryParse(TimeoutBox.Text.Trim(), out var timeout) || timeout < 1000)
        {
            return Fail("超时需为不小于 1000 的整数（毫秒）。");
        }
        return new ReporterTarget
        {
            Url = url,
            Token = TokenBox.Text.Trim(),
            Name = NameBox.Text.Trim(),
            Enabled = EnabledSwitch.IsChecked == true,
            IntervalSec = interval,
            TimeoutMs = timeout,
        };
    }

    private ReporterTarget? Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        return null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (BuildResult() is not null)
        {
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
