using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StairsPlugin
{
    // =========================================================
    //  规范参数值对象
    // =========================================================
    public class StairCodeParams
    {
        public double MinTreadDepth  { get; set; }  // 踏步最小宽度 b（mm）
        public double MaxRiserHeight { get; set; }  // 踏步最大高度 h（mm）
        public double MinRunWidth    { get; set; }  // 梯段最小净宽（mm）
        public double MinLandingDepth{ get; set; }  // 休息平台最小深度（mm）
        public double MinClearHeight { get; set; }  // 梯段净高（mm）
        public int    MaxStepsPerRun { get; set; }  // 每跑最大级数
        public string RuleSource     { get; set; }  // 规范条文来源
    }

    // =========================================================
    //  建筑类型枚举
    // =========================================================
    public enum BuildingType
    {
        Residential,  // 住宅
        Public,        // 一般公共建筑
        Attached,      // 附属楼梯（多层/高层）
        Supertall      // 附属楼梯（超高层）
    }

    // =========================================================
    //  规范规则库（静态字典）
    // =========================================================
    public static class StairCodeLibrary
    {
        public static readonly Dictionary<BuildingType, StairCodeParams> Rules =
            new Dictionary<BuildingType, StairCodeParams>
        {
            [BuildingType.Residential] = new StairCodeParams
            {
                MinTreadDepth   = 260,
                MaxRiserHeight  = 175,
                MinRunWidth     = 1000,
                MinLandingDepth = 1200,
                MinClearHeight  = 2200,
                MaxStepsPerRun  = 18,
                RuleSource = "GB55038-2025 §4.2.2 / GB55031-2022 表5.3.9 第2行"
            },
            [BuildingType.Public] = new StairCodeParams
            {
                MinTreadDepth   = 260,
                MaxRiserHeight  = 165,
                MinRunWidth     = 1100,
                MinLandingDepth = 1200,
                MinClearHeight  = 2200,
                MaxStepsPerRun  = 18,
                RuleSource = "GB55031-2022 表5.3.9 第1行 / GB55037-2022 §7.1.4 第3款"
            },
            [BuildingType.Attached] = new StairCodeParams
            {
                MinTreadDepth   = 260,
                MaxRiserHeight  = 175,
                MinRunWidth     = 1100,
                MinLandingDepth = 1200,
                MinClearHeight  = 2200,
                MaxStepsPerRun  = 18,
                RuleSource = "GB55031-2022 表5.3.9 第2行"
            },
            [BuildingType.Supertall] = new StairCodeParams
            {
                MinTreadDepth   = 250,
                MaxRiserHeight  = 180,
                MinRunWidth     = 1100,
                MinLandingDepth = 1200,
                MinClearHeight  = 2200,
                MaxStepsPerRun  = 18,
                RuleSource = "GB55031-2022 表5.3.9 第3行"
            }
        };
    }

    // =========================================================
    //  窗口 Code-behind
    // =========================================================
    public partial class StairGeneratorWindow : Window
    {
        // ------ Revit 上下文 ------
        private readonly UIDocument _uiDoc;
        private readonly Document   _doc;

        // ------ 拾取结果 ------
        private XYZ _p1 = null;
        private XYZ _p2 = null;

        // ------ 当前规范参数（随建筑类型切换） ------
        private StairCodeParams _currentRule;

        // ------ 标高缓存 ------
        private List<Level> _levels = new List<Level>();

        // ------ 颜色常量（用于徽章切换） ------
        private static readonly SolidColorBrush GreenBg  = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9));
        private static readonly SolidColorBrush GreenFg  = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
        private static readonly SolidColorBrush GreenBd  = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
        private static readonly SolidColorBrush OrangeBg = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xE0));
        private static readonly SolidColorBrush OrangeFg = new SolidColorBrush(Color.FromRgb(0xE6, 0x51, 0x00));
        private static readonly SolidColorBrush OrangeBd = new SolidColorBrush(Color.FromRgb(0xE6, 0x51, 0x00));

        // =========================================================
        //  构造函数
        // =========================================================
        public StairGeneratorWindow(UIDocument uiDoc)
        {
            InitializeComponent();
            _uiDoc = uiDoc;
            _doc   = uiDoc.Document;

            LoadLevels();
            LoadStairsTypes();
            LoadRailingTypes();

            // 默认选第一个建筑类型 → 初始化规范
            _currentRule = StairCodeLibrary.Rules[BuildingType.Residential];
            CmbBuildingType.SelectedIndex = 0;

            UpdatePreview();
        }

        // =========================================================
        //  初始化：读取 Revit 项目数据
        // =========================================================
        private void LoadLevels()
        {
            _levels = new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            foreach (var lv in _levels)
            {
                double elev = UnitUtils.ConvertFromInternalUnits(lv.Elevation, UnitTypeId.Millimeters);
                string sign = elev >= 0 ? "+" : "";
                string display = $"{lv.Name}  {sign}{elev:F0} mm";
                CmbBaseLevel.Items.Add(display);
                CmbTopLevel.Items.Add(display);
            }

            if (_levels.Count > 0) CmbBaseLevel.SelectedIndex = 0;
            if (_levels.Count > 1) CmbTopLevel.SelectedIndex  = 1;
        }

        private void LoadStairsTypes()
        {
            var types = new FilteredElementCollector(_doc)
                .OfClass(typeof(StairsType))
                .Cast<StairsType>()
                .ToList();

            foreach (var st in types)
                CmbStairsType.Items.Add(st.Name);

            if (types.Any()) CmbStairsType.SelectedIndex = 0;
        }

        private void LoadRailingTypes()
        {
            // 读取项目中的栏杆扶手类型（RailingType）
            var railingTypes = new FilteredElementCollector(_doc)
                .OfClass(typeof(ElementType))
                .Where(e => e.GetType().Name == "RailingType")
                .ToList();

            // 若项目中暂无栏杆族，写入默认占位项
            if (!railingTypes.Any())
            {
                CmbRailingType.Items.Add("（项目中暂无栏杆族）");
                CmbRailingType.SelectedIndex = 0;
                return;
            }

            foreach (var rt in railingTypes)
                CmbRailingType.Items.Add(rt.Name);

            CmbRailingType.SelectedIndex = 0;
        }

        // =========================================================
        //  标高选择变化 → 更新总高显示
        // =========================================================
        private void CmbLevel_Changed(object sender, SelectionChangedEventArgs e)
        {
            UpdateTotalHeight();
            UpdatePreview();
        }

        private void UpdateTotalHeight()
        {
            int bi = CmbBaseLevel.SelectedIndex;
            int ti = CmbTopLevel.SelectedIndex;

            if (bi < 0 || ti < 0 || bi >= _levels.Count || ti >= _levels.Count)
            {
                TxtTotalHeight.Text = "总高 — mm";
                return;
            }

            Level baseLv = _levels[bi];
            Level topLv  = _levels[ti];

            double totalMm = UnitUtils.ConvertFromInternalUnits(
                topLv.Elevation - baseLv.Elevation, UnitTypeId.Millimeters);

            if (totalMm <= 0)
                TxtTotalHeight.Text = "⚠ 请选择更高标高";
            else
                TxtTotalHeight.Text = $"总高 {totalMm:F0} mm";
        }

        // =========================================================
        //  建筑类型变化 → 切换规范规则
        // =========================================================
        private void CmbBuildingType_Changed(object sender, SelectionChangedEventArgs e)
        {
            var selected = (CmbBuildingType.SelectedItem as ComboBoxItem)?.Tag as string;
            if (Enum.TryParse(selected, out BuildingType bt))
                _currentRule = StairCodeLibrary.Rules[bt];

            UpdatePreview();
        }

        // =========================================================
        //  几何参数输入变化 → 实时校验 + 更新预览
        // =========================================================
        private void GeomParam_Changed(object sender, TextChangedEventArgs e)
        {
            UpdatePreview();
        }

        // =========================================================
        //  核心：实时预览计算与合规校验
        // =========================================================
        private void UpdatePreview()
        {
            if (_currentRule == null) return;

            // --- 读取总高 ---
            int bi = CmbBaseLevel?.SelectedIndex ?? -1;
            int ti = CmbTopLevel?.SelectedIndex  ?? -1;
            if (bi < 0 || ti < 0 || bi >= _levels.Count || ti >= _levels.Count) return;

            double totalMm = UnitUtils.ConvertFromInternalUnits(
                _levels[ti].Elevation - _levels[bi].Elevation, UnitTypeId.Millimeters);

            if (totalMm <= 0) { ClearPreview(); return; }

            // --- 读取用户输入 ---
            if (!double.TryParse(TxtRunWidth?.Text,    out double runWidthMm))   runWidthMm   = 1200;
            if (!double.TryParse(TxtTreadDepth?.Text,  out double treadDepthMm)) treadDepthMm = 260;

            // --- 踏步解算算法 ---
            // 向上取整保证每步高度不超过规范上限
            int totalSteps = (int)Math.Ceiling(totalMm / _currentRule.MaxRiserHeight);
            // 每个梯段不超过18级，若超过则每跑18级迭代（本版本聚焦双跑）
            if (totalSteps < 4) totalSteps = 4;

            double riserMm = totalMm / totalSteps;

            int run1Steps = (int)Math.Ceiling(totalSteps / 2.0);
            int run2Steps = totalSteps - run1Steps;

            // --- 更新预览文字 ---
            if (TxtPreviewSteps != null) TxtPreviewSteps.Text = $"{totalSteps} 级";
            if (TxtPreviewRiser != null) TxtPreviewRiser.Text = $"{riserMm:F1} mm";
            if (TxtPreviewDist  != null) TxtPreviewDist.Text  = $"{run1Steps} + {run2Steps} 步";
            if (TxtPreviewRule  != null) TxtPreviewRule.Text   = $"规范依据：{_currentRule.RuleSource}";

            // --- 合规校验 ---
            var violations = new List<string>();

            // 踏步宽
            bool treadOk = treadDepthMm >= _currentRule.MinTreadDepth;
            SetBadge(BadgeTread, TxtBadgeTread,
                treadOk, "合规",
                $"违规 < {_currentRule.MinTreadDepth} mm");
            if (!treadOk)
                violations.Add($"踏步宽度 {treadDepthMm} mm 不足，规范要求 ≥ {_currentRule.MinTreadDepth} mm");

            // 梯段净宽
            bool runWidthOk = runWidthMm >= _currentRule.MinRunWidth;
            SetBadge(BadgeRunWidth, TxtBadgeRunWidth,
                runWidthOk, "合规",
                $"违规 < {_currentRule.MinRunWidth} mm");
            if (!runWidthOk)
                violations.Add($"梯段净宽 {runWidthMm} mm 不足，规范要求 ≥ {_currentRule.MinRunWidth} mm");

            // 踢面高
            bool riserOk = riserMm <= _currentRule.MaxRiserHeight;
            if (!riserOk)
                violations.Add($"解算踢面高 {riserMm:F1} mm 超出规范上限 {_currentRule.MaxRiserHeight} mm，请检查层高");

            // --- 显示/隐藏违规面板 ---
            if (PanelWarn != null)
            {
                if (violations.Any())
                {
                    PanelWarn.Visibility = Visibility.Visible;
                    if (TxtWarnDetail != null)
                        TxtWarnDetail.Text = "规范预警：" + string.Join("；", violations) + "。请修正后再生成。";
                    if (BtnGenerate != null) BtnGenerate.IsEnabled = false;
                }
                else
                {
                    PanelWarn.Visibility = Visibility.Collapsed;
                    if (BtnGenerate != null) BtnGenerate.IsEnabled = true;
                }
            }
        }

        // =========================================================
        //  辅助：设置徽章颜色与文字
        // =========================================================
        private void SetBadge(Border badge, TextBlock text, bool isOk,
                               string okText, string warnText)
        {
            if (badge == null || text == null) return;
            if (isOk)
            {
                badge.Background  = GreenBg;
                badge.BorderBrush = GreenBd;
                text.Foreground   = GreenFg;
                text.Text         = okText;
            }
            else
            {
                badge.Background  = OrangeBg;
                badge.BorderBrush = OrangeBd;
                text.Foreground   = OrangeFg;
                text.Text         = warnText;
            }
        }

        private void ClearPreview()
        {
            if (TxtPreviewSteps != null) TxtPreviewSteps.Text = "—";
            if (TxtPreviewRiser != null) TxtPreviewRiser.Text = "—";
            if (TxtPreviewDist  != null) TxtPreviewDist.Text  = "—";
            if (TxtPreviewRule  != null) TxtPreviewRule.Text  = "规范依据：—";
        }

        // =========================================================
        //  拾取 P1 按钮
        // =========================================================
        private void BtnPickP1_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                this.Hide();
                _p1 = _uiDoc.Selection.PickPoint("请在平面视图中点击楼梯插入点 P1");
                this.Show();

                double x1 = UnitUtils.ConvertFromInternalUnits(_p1.X, UnitTypeId.Millimeters);
                double y1 = UnitUtils.ConvertFromInternalUnits(_p1.Y, UnitTypeId.Millimeters);
                TxtCoordP1.Text = $"X={x1:F0}  Y={y1:F0} mm";

                // P1 拾取成功后激活 P2 按钮
                BtnPickP2.IsEnabled = true;
                TxtCoordP2.Text     = "请继续拾取 P2（方向点）";
                TxtTheta.Text       = "P1 → P2 决定楼梯爬升朝向，θ = —";
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                this.Show();
            }
        }

        // =========================================================
        //  拾取 P2 按钮
        // =========================================================
        private void BtnPickP2_Click(object sender, RoutedEventArgs e)
        {
            if (_p1 == null) return;
            try
            {
                this.Hide();
                _p2 = _uiDoc.Selection.PickPoint("请在平面视图中点击方向点 P2");
                this.Show();

                double x2 = UnitUtils.ConvertFromInternalUnits(_p2.X, UnitTypeId.Millimeters);
                double y2 = UnitUtils.ConvertFromInternalUnits(_p2.Y, UnitTypeId.Millimeters);
                TxtCoordP2.Text = $"X={x2:F0}  Y={y2:F0} mm";

                // 计算方向角 θ
                double dx       = _p2.X - _p1.X;
                double dy       = _p2.Y - _p1.Y;
                double angleRad = Math.Atan2(dy, dx);
                double angleDeg = angleRad * (180.0 / Math.PI);
                if (angleDeg < 0) angleDeg += 360;

                string compass = GetCompassDirection(angleDeg);
                TxtTheta.Text = $"P1 → P2 决定楼梯爬升朝向，θ = {angleDeg:F1}°  {compass}";
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                this.Show();
            }
        }

        // =========================================================
        //  方位描述
        // =========================================================
        private string GetCompassDirection(double deg)
        {
            if (deg > 337.5 || deg <= 22.5)  return "（朝东）";
            if (deg <= 67.5)  return "（东北）";
            if (deg <= 112.5) return "（朝北）";
            if (deg <= 157.5) return "（西北）";
            if (deg <= 202.5) return "（朝西）";
            if (deg <= 247.5) return "（西南）";
            if (deg <= 292.5) return "（朝南）";
            return "（东南）";
        }

        // =========================================================
        //  生成扶手复选框：联动栏杆下拉
        // =========================================================
        private void ChkRailing_Changed(object sender, RoutedEventArgs e)
        {
            if (CmbRailingType != null)
                CmbRailingType.IsEnabled = ChkRailing.IsChecked == true;
        }

        // =========================================================
        //  对外暴露的属性（供 ExternalCommand.Execute 读取）
        // =========================================================
        public Level     SelectedBaseLevel  => CmbBaseLevel.SelectedIndex >= 0 ? _levels[CmbBaseLevel.SelectedIndex] : null;
        public Level     SelectedTopLevel   => CmbTopLevel.SelectedIndex  >= 0 ? _levels[CmbTopLevel.SelectedIndex]  : null;
        public double    BaseOffsetMm       => double.TryParse(TxtBaseOffset.Text,   out double v) ? v : 0;
        public double    RunWidthMm         => double.TryParse(TxtRunWidth.Text,     out double v) ? v : 1200;
        public double    TreadDepthMm       => double.TryParse(TxtTreadDepth.Text,   out double v) ? v : 260;
        public double    WellWidthMm        => double.TryParse(TxtWellWidth.Text,    out double v) ? v : 100;
        public double    LandingDepthMm     => double.TryParse(TxtLandingDepth.Text, out double v) ? v : 1200;
        public bool      GenerateRailing    => ChkRailing.IsChecked == true;
        public bool      EnableClearCheck   => ChkClearance.IsChecked == true;
        public bool      IsClockwise        => RbClockwise.IsChecked == true;
        public XYZ       PickedP1           => _p1;
        public XYZ       PickedP2           => _p2;
        public string    SelectedStairsType => CmbStairsType.SelectedItem as string ?? "";
        public StairCodeParams CurrentRule  => _currentRule;

        /// <summary>
        /// P1→P2 向量的旋转角（弧度），用于 Transform.CreateRotation
        /// </summary>
        public double DirectionAngleRad
        {
            get
            {
                if (_p1 == null || _p2 == null) return 0;
                return Math.Atan2(_p2.Y - _p1.Y, _p2.X - _p1.X);
            }
        }

        // =========================================================
        //  按钮事件
        // =========================================================
        private void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            // 前置拦截：P1 必须已拾取
            if (_p1 == null)
            {
                MessageBox.Show("请先拾取楼梯插入点 P1。", "提示",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 标高合法性
            if (SelectedBaseLevel == null || SelectedTopLevel == null ||
                SelectedTopLevel.Elevation <= SelectedBaseLevel.Elevation)
            {
                MessageBox.Show("顶部标高必须高于底部标高，请重新选择。", "提示",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
