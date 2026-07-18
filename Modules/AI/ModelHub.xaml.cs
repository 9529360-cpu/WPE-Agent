using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using 币安量化机器人.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace 币安量化机器人.Modules.AI
{
    public partial class ModelHub : UserControl
    {
        private readonly ObservableCollection<ModelRow> _all = new();
        private readonly ICollectionView _view;
        private readonly AiForecastService _aiService = ServiceLocator.Ai;

        public ModelHub()
        {
            InitializeComponent();

            foreach (var artifact in _aiService.Models)
            {
                _all.Add(new ModelRow
                {
                    Name = artifact.Name,
                    Version = Version.Parse(artifact.Version).Major,
                    Stage = artifact.Stage,
                    Metric = artifact.Metric,
                    UpdatedAt = artifact.UpdatedAt.ToString("yyyy-MM-dd HH:mm"),
                    Description = artifact.Description
                });
            }

            _view = CollectionViewSource.GetDefaultView(_all);
            GridModels.ItemsSource = _view;

            GridModels.SelectionChanged += (_, __) => UpdateDetail();
            if (_all.Any()) GridModels.SelectedIndex = 0;
            StatusText.Text = $"状态：已加载 {_all.Count} 个上线模型";
        }

        private void UpdateDetail()
        {
            if (GridModels.SelectedItem is ModelRow r)
            {
                DetailTitle.Text = $"{r.Name} v{r.Version} · {r.Stage}";
                DetailInfo.Text = $"主指标：{r.Metric}\n更新时间：{r.UpdatedAt}\n\n{r.Description}";
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var kw = SearchBox.Text?.Trim() ?? "";
            _view.Filter = o =>
            {
                if (o is not ModelRow r) return false;
                return string.IsNullOrEmpty(kw)
                       || r.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)
                       || r.Stage.Contains(kw, StringComparison.OrdinalIgnoreCase);
            };
            _view.Refresh();
        }

        private void Train_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "状态：训练由外部流水线负责，请在 CICD 中触发";
            MessageBox.Show("模型训练由离线管道执行，请在 MLFlow/CI 中触发任务。", "训练任务", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Register_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("模型注册由自动化部署完成，此处仅展示线上模型状态。", "模型注册", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void PromoteStaging_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("上线流程由 Release 系统控制，此处仅同步展示模型阶段。", "模型阶段", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void PromoteProd_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("请在部署中心执行 Production 切换。", "模型上线", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Rollback_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("回滚操作请在部署流水线执行。", "回滚", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CreateAB_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("A/B 实验占位：后续接入流量分配与对照指标。", "A/B 实验", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private void CreateCanary_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("金丝雀占位：小流量灰度，指标稳定再放量。", "金丝雀", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private void CreateShadow_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Shadow 占位：新模型仅收请求不出结果，用于对齐与观测。", "Shadow", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public class ModelRow : INotifyPropertyChanged
    {
        private string _stage = "Draft";

        public string Name { get; set; } = "";
        public int Version { get; set; }
        public string Stage
        {
            get => _stage;
            set { _stage = value; OnPropertyChanged(nameof(Stage)); }
        }
        public string Metric { get; set; } = "N/A";
        public string UpdatedAt { get; set; } = "";
        public string Description { get; set; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
