using System.Windows;
using System.Windows.Controls;

namespace 币安量化机器人.Helpers;

/// <summary>
/// StackPanel 附加属性辅助类，用于设置子元素间距
/// </summary>
public static class StackPanelHelper
{
    /// <summary>
    /// Spacing 附加属性定义
    /// </summary>
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.RegisterAttached(
            "Spacing",
            typeof(double),
            typeof(StackPanelHelper),
            new PropertyMetadata(0.0, OnSpacingChanged));

    /// <summary>
    /// 获取 Spacing 属性值
    /// </summary>
    public static double GetSpacing(DependencyObject obj)
    {
        return (double)obj.GetValue(SpacingProperty);
    }

    /// <summary>
    /// 设置 Spacing 属性值
    /// </summary>
    public static void SetSpacing(DependencyObject obj, double value)
    {
        obj.SetValue(SpacingProperty, value);
    }

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not StackPanel stackPanel)
            return;

        var spacing = (double)e.NewValue;

        // 当 StackPanel 的子元素发生变化时，更新边距
        stackPanel.Loaded -= StackPanel_Loaded;
        stackPanel.Loaded += StackPanel_Loaded;

        // 立即应用间距
        ApplySpacing(stackPanel, spacing);
    }

    private static void StackPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is StackPanel stackPanel)
        {
            var spacing = GetSpacing(stackPanel);
            ApplySpacing(stackPanel, spacing);
        }
    }

    private static void ApplySpacing(StackPanel stackPanel, double spacing)
    {
        if (stackPanel.Children.Count == 0)
            return;

        var isHorizontal = stackPanel.Orientation == Orientation.Horizontal;

        for (int i = 0; i < stackPanel.Children.Count; i++)
        {
            if (stackPanel.Children[i] is FrameworkElement element)
            {
                // 第一个元素不添加前置间距
                if (i == 0)
                {
                    element.Margin = new Thickness(0);
                }
                else
                {
                    // 根据 StackPanel 方向设置间距
                    if (isHorizontal)
                    {
                        element.Margin = new Thickness(spacing, 0, 0, 0);
                    }
                    else
                    {
                        element.Margin = new Thickness(0, spacing, 0, 0);
                    }
                }
            }
        }
    }
}
