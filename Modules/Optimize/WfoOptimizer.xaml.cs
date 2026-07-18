using ScottPlot;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace 币安量化机器人.Modules.Optimize
{
    public partial class WfoOptimizer : UserControl
    {
        readonly Random _rng = new(123);
        readonly DataTable _table = new();

        public WfoOptimizer()
        {
            InitializeComponent();

            // 摘要表结构
            _table.Columns.Add("窗口序号", typeof(int));
            _table.Columns.Add("IS期", typeof(string));
            _table.Columns.Add("OOS期", typeof(string));
            _table.Columns.Add("最优参数", typeof(string));
            _table.Columns.Add("目标值", typeof(double));
            GridSummary.ItemsSource = _table.DefaultView;

            // 先清空两张图
            StabPlot.Plot.Clear();
            EquityPlot.Plot.Clear();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            _table.Rows.Clear();
            StabPlot.Plot.Clear(); StabPlot.Refresh();
            EquityPlot.Plot.Clear(); EquityPlot.Refresh();
            StatusText.Text = "状态：已清空结果";
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 读取窗口与搜索空间
                int isLen = Math.Max(30, int.Parse(TbIsLen.Text));
                int oosLen = Math.Max(10, int.Parse(TbOosLen.Text));
                bool rolling = CbRollMode.SelectedIndex == 0;

                int p1Min = int.Parse(TbP1Min.Text);
                int p1Max = int.Parse(TbP1Max.Text);
                int p1Step = Math.Max(1, int.Parse(TbP1Step.Text));

                int p2Min = int.Parse(TbP2Min.Text);
                int p2Max = int.Parse(TbP2Max.Text);
                int p2Step = Math.Max(1, int.Parse(TbP2Step.Text));

                var p1Vals = Enumerable.Range(0, (p1Max - p1Min) / p1Step + 1).Select(i => p1Min + i * p1Step).ToArray();
                var p2Vals = Enumerable.Range(0, (p2Max - p2Min) / p2Step + 1).Select(i => p2Min + i * p2Step).ToArray();

                // —— 演示评分函数（近似“夏普”） —— //
                double Score(int fast, int slow)
                {
                    double dx = (fast - 15.0) / 10.0;
                    double dy = (slow - 120.0) / 40.0;
                    double baseScore = Math.Exp(-(dx * dx + dy * dy));   // [0,1]
                    double noise = _rng.NextDouble() * 0.15 - 0.075;
                    return 0.8 * baseScore + noise;
                }

                // 演示滚动次数
                int runs = Math.Clamp((int)Math.Round(1.0 * isLen / oosLen) + 7, 8, 12);

                _table.Rows.Clear();
                var equity = new List<double>();
                double eq = 1.0;

                // 累计稳定性热力图的平均分
                double[,] heat = new double[p1Vals.Length, p2Vals.Length];

                for (int k = 0; k < runs; k++)
                {
                    double bestScore = double.NegativeInfinity;
                    (int f, int s) best = (0, 0);

                    for (int i = 0; i < p1Vals.Length; i++)
                    {
                        for (int j = 0; j < p2Vals.Length; j++)
                        {
                            double sc = Score(p1Vals[i], p2Vals[j]);
                            heat[i, j] += sc;
                            if (sc > bestScore)
                            {
                                bestScore = sc;
                                best = (p1Vals[i], p2Vals[j]);
                            }
                        }
                    }

                    string isTxt = $"IS: {isLen} 天";
                    string oosTxt = $"OOS: {oosLen} 天";
                    _table.Rows.Add(k + 1, isTxt, oosTxt, $"{best.f},{best.s}", Math.Round(bestScore, 3));

                    // 用 bestScore 合成一小段 OOS 权益并拼接
                    int oosBars = oosLen / 3;
                    for (int t = 0; t < oosBars; t++)
                    {
                        double r = 0.001 + Math.Max(0, bestScore) * 0.005 + (_rng.NextDouble() - 0.5) * 0.002;
                        eq *= (1.0 + r);
                        equity.Add(eq);
                    }
                }

                // —— 画稳定性热力图（平均后） —— //
                for (int i = 0; i < p1Vals.Length; i++)
                    for (int j = 0; j < p2Vals.Length; j++)
                        heat[i, j] /= runs;

                StabPlot.Plot.Clear();

                // ScottPlot 5：直接添加热力图
                var hm = StabPlot.Plot.Add.Heatmap(heat);

                // 自定义坐标刻度（把索引映射为参数值）
                double[] xPos = Enumerable.Range(0, p2Vals.Length).Select(i => (double)i).ToArray();
                string[] xLbl = p2Vals.Select(v => v.ToString()).ToArray();
                StabPlot.Plot.Axes.Bottom.SetTicks(xPos, xLbl);   // X 对应 p2（慢均线）

                double[] yPos = Enumerable.Range(0, p1Vals.Length).Select(i => (double)i).ToArray();
                string[] yLbl = p1Vals.Select(v => v.ToString()).ToArray();
                StabPlot.Plot.Axes.Left.SetTicks(yPos, yLbl);     // Y 对应 p1（快均线）

                StabPlot.Plot.Title("参数稳定性热力图（数值越高越好）");
                // ★ 修复点：Label 为属性而非方法
                StabPlot.Plot.Axes.Left.Label.Text = TbP1Name.Text;
                StabPlot.Plot.Axes.Bottom.Label.Text = TbP2Name.Text;
                StabPlot.Refresh();

                // —— 画 OOS 拼接权益曲线 —— //
                EquityPlot.Plot.Clear();
                double[] ys = equity.ToArray();
                double[] xs = Enumerable.Range(0, ys.Length).Select(i => (double)i).ToArray();
                EquityPlot.Plot.Add.Scatter(xs, ys);
                EquityPlot.Plot.Title("拼接 OOS 权益曲线（演示）");
                // ★ 修复点：Label 为属性而非方法
                EquityPlot.Plot.Axes.Left.Label.Text = "权益";
                EquityPlot.Plot.Axes.Bottom.Label.Text = "样本外序列";
                EquityPlot.Refresh();

                StatusText.Text = $"状态：已完成 {runs} 个滚动窗口的演示优化。";
                Tabs.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "运行错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
