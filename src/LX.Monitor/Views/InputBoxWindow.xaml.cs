using System.Windows;

namespace LingXi.Monitor.Views;

/// <summary>通用单行输入框（机器别名等）：ShowDialog，确定返回输入值，取消返回 null。</summary>
public partial class InputBoxWindow : Window
{
    public string ValueText => ValueBox.Text;

    private InputBoxWindow(string title, string message, string value)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ValueBox.Text = value;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    /// <summary>模态弹出：确定返回输入值（未 Trim），取消/Esc 返回 null。</summary>
    public static string? Show(string title, string message, string? value = null)
    {
        var window = new InputBoxWindow(title, message, value ?? "");
        window.Owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        return window.ShowDialog() == true ? window.ValueText : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
