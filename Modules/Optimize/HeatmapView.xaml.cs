using System.Windows.Controls;

namespace 币安量化机器人.Modules.Optimize
{
    public partial class HeatmapView : UserControl
    {
        public HeatmapView()
        {
            InitializeComponent();

            // 可选：加载时画一张小演示图，验证控件是否正常
            this.Loaded += (_, __) =>
            {
                double[,] z = new double[20, 20];
                for (int i = 0; i < 20; i++)
                    for (int j = 0; j < 20; j++)
                        z[i, j] = 0.6 + 0.4 * System.Math.Sin(i * .2) * System.Math.Cos(j * .15);

                var plt = Plot.Plot;                 // v5：从 WpfPlot 取 Plot
                plt.Clear();
                var hm = plt.Add.Heatmap(z);         // v5：Plot.Add.Heatmap
                hm.Colormap = new ScottPlot.Colormaps.Turbo();
                plt.Add.ColorBar(hm);
                plt.Title("热力图（演示）");
                plt.Axes.Bottom.Label.Text = "X";
                plt.Axes.Left.Label.Text = "Y";
                Plot.Refresh();
            };
        }

        // 真正使用时：调用它来显示你的矩阵
        public void Show(double[,] z, string xLabel, string yLabel, string title)
        {
            var plt = Plot.Plot;
            plt.Clear();
            var hm = plt.Add.Heatmap(z);
            hm.Colormap = new ScottPlot.Colormaps.Turbo();
            plt.Add.ColorBar(hm);
            plt.Title(title);
            plt.Axes.Bottom.Label.Text = xLabel;
            plt.Axes.Left.Label.Text = yLabel;
            Plot.Refresh();
        }
    }
}
