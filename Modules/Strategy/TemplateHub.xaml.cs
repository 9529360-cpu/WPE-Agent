using System.Windows;
using System.Windows.Controls;

namespace 币安量化机器人.Modules.Strategy
{
    public partial class TemplateHub : UserControl
    {
        public TemplateHub()
        {
            InitializeComponent();
        }

        // 点击任意模板卡片：当前先用占位方式提示，后面再跳到“新建策略向导”
        private void Template_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string key)
            {
                MessageBox.Show(
                    $"你点击了模板：{key}\n\n下一步：打开“新建策略向导”，按所选模板生成参数表单与默认风控。",
                    "模板占位",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
