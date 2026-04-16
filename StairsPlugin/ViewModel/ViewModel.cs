using Autodesk.Revit.DB;
using StairsPlugin.Model;
using StairsPlugin.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace StairsPlugin.ViewModel
{
    /// <summary>
    /// 双跑楼梯生成窗口的 ViewModel。
    ///
    /// 职责边界：
    ///   ✓ 所有界面状态属性（标高列表、预览文字、合规标志）
    ///   ✓ 规范校验与踏步解算（调用 Model 层）
    ///   ✓ 命令定义（GenerateCommand）
    ///   ✓ 通过事件通知 View 关闭窗口
    ///   ✗ 不持有任何 WPF 控件引用
    ///   ✗ 不调用 UIDocument.Selection（拾取点由 Code-behind 完成后写入 P1/P2）
    /// </summary>
    public class ViewModel : ViewModelBase
    {
        // =============================================================
        //  事件：通知 View 层"参数合法，可以关窗并执行生成"
        //  View 的 Code-behind 订阅此事件后调用 DialogResult = true; Close()
        // =============================================================
        public event EventHandler GenerateRequested;

        // =============================================================
        //  用户意图标志：替代 DialogResult 在非模态场景下的判断依据。
        //  true  = 用户点击"生成楼梯"且校验通过
        //  false = 用户点击"取消"或直接关闭窗口（默认值）
        //  CommandStairGenerator 弹窗后读此属性，而非依赖 ShowDialog 返回值。
        // =============================================================
        public bool IsConfirmed { get; set; } = false;

        // =============================================================
        //  标高数据
        //  Levels        : Level 对象列表，由 CommandStairGenerator 注入
        //  LevelDisplayNames : 格式化字符串列表，绑定到两个标高 ComboBox 的 ItemsSource
        // =============================================================
        public List<Level> Levels { get; } = new List<Level>();

        public ObservableCollection<string> LevelDisplayNames
        {
            get;
        }
            = new ObservableCollection<string>();

        private int _baseLevelIndex = 0;
        /// <summary>底部标高 ComboBox 的 SelectedIndex（双向绑定）</summary>
        public int BaseLevelIndex
        {
            get => _baseLevelIndex;
            set
            {
                if (SetField(ref _baseLevelIndex, value))
                    Recalculate();
            }
        }

        private int _topLevelIndex = 1;
        /// <summary>顶部标高 ComboBox 的 SelectedIndex（双向绑定）</summary>
        public int TopLevelIndex
        {
            get => _topLevelIndex;
            set
            {
                if (SetField(ref _topLevelIndex, value))
                    Recalculate();
            }
        }

        /// <summary>总高度显示文字（只读，绑定到标高 ComboBox 旁的蓝色标签）</summary>
        private string _totalHeightDisplay = "— mm";
        public string TotalHeightDisplay
        {
            get => _totalHeightDisplay;
            private set => SetField(ref _totalHeightDisplay, value);
        }

        // =============================================================
        //  楼梯族类型
        // =============================================================
        public ObservableCollection<string> StairsTypeNames
        {
            get;
        }
            = new ObservableCollection<string>();

        private int _stairsTypeIndex = 0;
        public int StairsTypeIndex
        {
            get => _stairsTypeIndex;
            set => SetField(ref _stairsTypeIndex, value);
        }

        // =============================================================
        //  建筑功能类型（索引 0~3 对应 Residential/Public/Attached/Supertall）
        // =============================================================
        private int _buildingTypeIndex = 0;
        /// <summary>建筑类型 ComboBox 的 SelectedIndex（双向绑定）</summary>
        public int BuildingTypeIndex
        {
            get => _buildingTypeIndex;
            set
            {
                if (SetField(ref _buildingTypeIndex, value))
                {
                    _currentRule = StairCodeLibrary.Rules[(Model.BuildingType)value];
                    Recalculate();
                }
            }
        }

        // =============================================================
        //  平面定位与方向（P1/P2 由 Code-behind 拾取后写入）
        // =============================================================
        private XYZ _p1;
        /// <summary>插入点 P1；Code-behind 拾取后赋值</summary>
        public XYZ P1
        {
            get => _p1;
            set
            {
                SetField(ref _p1, value);
                OnPropertyChanged(nameof(P1Display));
                OnPropertyChanged(nameof(CanPickP2));
                GenerateCommand.RaiseCanExecuteChanged();
            }
        }

        private XYZ _p2;
        /// <summary>方向点 P2；Code-behind 拾取后赋值</summary>
        public XYZ P2
        {
            get => _p2;
            set
            {
                SetField(ref _p2, value);
                OnPropertyChanged(nameof(P2Display));
                OnPropertyChanged(nameof(ThetaDisplay));
            }
        }

        /// <summary>P1 坐标显示文字（只读，绑定到 TxtCoordP1）</summary>
        public string P1Display => P1 == null ? "未拾取"
            : $"X={ToMm(P1.X):F0}  Y={ToMm(P1.Y):F0} mm";

        /// <summary>P2 坐标显示文字（只读，绑定到 TxtCoordP2）</summary>
        public string P2Display => P2 == null ? "请继续拾取 P2（方向点）"
            : $"X={ToMm(P2.X):F0}  Y={ToMm(P2.Y):F0} mm";

        /// <summary>方向角显示文字（只读，绑定到 TxtTheta）</summary>
        public string ThetaDisplay
        {
            get
            {
                if (P1 == null || P2 == null)
                    return "P1 → P2 决定楼梯爬升朝向，θ = —";
                double deg = Math.Atan2(P2.Y - P1.Y, P2.X - P1.X) * 180.0 / Math.PI;
                if (deg < 0)
                    deg += 360;
                return $"P1 → P2 决定楼梯爬升朝向，θ = {deg:F1}°  {GetCompassDirection(deg)}";
            }
        }

        /// <summary>
        /// P2 拾取按钮的 IsEnabled 来源（绑定到 BtnPickP2.IsEnabled）。
        /// 只有 P1 已拾取时才允许拾取 P2。
        /// </summary>
        public bool CanPickP2 => P1 != null;

        /// <summary>P1→P2 方向角（弧度），生成时传给 Transform.CreateRotation</summary>
        public double DirectionAngleRad => (P1 == null || P2 == null) ? 0.0
            : Math.Atan2(P2.Y - P1.Y, P2.X - P1.X);

        // =============================================================
        //  盘旋方向（true = 顺时针/右旋）
        // =============================================================
        private bool _isClockwise = true;
        public bool IsClockwise
        {
            get => _isClockwise;
            set => SetField(ref _isClockwise, value);
        }

        // =============================================================
        //  几何参数（双向绑定，变化时触发 Recalculate）
        // =============================================================
        private double _runWidthMm = 1200;
        public double RunWidthMm
        {
            get => _runWidthMm;
            set
            {
                if (SetField(ref _runWidthMm, value))
                    Recalculate();
            }
        }

        private double _treadDepthMm = 260;
        public double TreadDepthMm
        {
            get => _treadDepthMm;
            set
            {
                if (SetField(ref _treadDepthMm, value))
                    Recalculate();
            }
        }

        private double _wellWidthMm = 100;
        public double WellWidthMm
        {
            get => _wellWidthMm;
            set => SetField(ref _wellWidthMm, value);
        }

        private double _landingDepthMm = 1200;
        public double LandingDepthMm
        {
            get => _landingDepthMm;
            set => SetField(ref _landingDepthMm, value);
        }

        private double _baseOffsetMm = 0;
        public double BaseOffsetMm
        {
            get => _baseOffsetMm;
            set => SetField(ref _baseOffsetMm, value);
        }

        // =============================================================
        //  辅助选项
        // =============================================================
        private bool _generateRailing = true;
        public bool GenerateRailing
        {
            get => _generateRailing;
            set => SetField(ref _generateRailing, value);
        }

        // 栏杆类型 ComboBox（仅 GenerateRailing = true 时可用）
        public ObservableCollection<string> RailingTypeNames
        {
            get;
        }
            = new ObservableCollection<string>();

        private int _railingTypeIndex = 0;
        public int RailingTypeIndex
        {
            get => _railingTypeIndex;
            set => SetField(ref _railingTypeIndex, value);
        }

        private bool _enableClearCheck = true;
        public bool EnableClearCheck
        {
            get => _enableClearCheck;
            set => SetField(ref _enableClearCheck, value);
        }

        // =============================================================
        //  实时预览（只读，由 Recalculate 计算后通知）
        // =============================================================
        private StairCalculationResult _calcResult;

        public string PreviewSteps => _calcResult == null ? "—" : $"{_calcResult.TotalSteps} 级";
        public string PreviewRiser => _calcResult == null ? "—" : $"{_calcResult.RiserHeight:F1} mm";
        public string PreviewDist => _calcResult == null ? "—"
            : $"{_calcResult.Run1Steps} + {_calcResult.Run2Steps} 步";
        public string PreviewRule => _currentRule?.RuleSource ?? "—";

        // =============================================================
        //  合规标志（绑定到 Badge 的 DataTrigger 和 PanelWarn 的 Visibility）
        // =============================================================
        private bool _runWidthOk = true;
        /// <summary>梯段净宽合规 → Badge DataTrigger 的绑定源</summary>
        public bool RunWidthOk
        {
            get => _runWidthOk;
            private set
            {
                if (SetField(ref _runWidthOk, value))
                    OnPropertyChanged(nameof(RunWidthBadgeText));
            }
        }

        private bool _treadDepthOk = true;
        /// <summary>踏步宽度合规 → Badge DataTrigger 的绑定源</summary>
        public bool TreadDepthOk
        {
            get => _treadDepthOk;
            private set
            {
                if (SetField(ref _treadDepthOk, value))
                    OnPropertyChanged(nameof(TreadDepthBadgeText));
            }
        }

        /// <summary>梯段净宽徽章文字（合规时显示 "合规"，违规时显示具体数值）</summary>
        public string RunWidthBadgeText => RunWidthOk
            ? "合规" : $"违规 < {_currentRule?.MinRunWidth} mm";

        /// <summary>踏步宽度徽章文字</summary>
        public string TreadDepthBadgeText => TreadDepthOk
            ? "合规" : $"违规 < {_currentRule?.MinTreadDepth} mm";

        private bool _hasViolation = false;
        /// <summary>是否存在违规 → 绑定到 PanelWarn.Visibility（BooleanToVisibilityConverter）</summary>
        public bool HasViolation
        {
            get => _hasViolation;
            private set => SetField(ref _hasViolation, value);
        }

        private string _violationDetail = "";
        /// <summary>违规详情文字 → 绑定到 TxtWarnDetail.Text</summary>
        public string ViolationDetail
        {
            get => _violationDetail;
            private set => SetField(ref _violationDetail, value);
        }

        // =============================================================
        //  命令
        // =============================================================
        public RelayCommand GenerateCommand
        {
            get;
        }

        // =============================================================
        //  当前规范（私有，不暴露给 XAML）
        // =============================================================
        private StairCodeParams _currentRule;

        // =============================================================
        //  对外只读属性（供 CommandStairGenerator 读取生成参数）
        // =============================================================
        public Level SelectedBaseLevel =>
            (BaseLevelIndex >= 0 && BaseLevelIndex < Levels.Count)
                ? Levels[BaseLevelIndex] : null;

        public Level SelectedTopLevel =>
            (TopLevelIndex >= 0 && TopLevelIndex < Levels.Count)
                ? Levels[TopLevelIndex] : null;

        public string SelectedStairsTypeName =>
            (StairsTypeIndex >= 0 && StairsTypeIndex < StairsTypeNames.Count)
                ? StairsTypeNames[StairsTypeIndex] : "";

        public StairCodeParams CurrentRule => _currentRule;

        // =============================================================
        //  构造函数
        // =============================================================
        public ViewModel()
        {
            // 初始化规范（默认住宅）
            _currentRule = StairCodeLibrary.Rules[Model.BuildingType.Residential];

            // 生成命令：CanExecute 要求 P1 已拾取且无违规
            GenerateCommand = new RelayCommand(
                execute: OnGenerate,
                canExecute: () => P1 != null && !HasViolation
            );
        }

        // =============================================================
        //  公共方法：由 CommandStairGenerator 在 ShowDialog 前调用，注入数据
        // =============================================================

        /// <summary>注入标高列表（从 RevitLevelTools.GetLevels 获取）</summary>
        public void LoadLevels(IEnumerable<Level> levels)
        {
            Levels.AddRange(levels);
            LevelDisplayNames.Clear();
            foreach (var lv in Levels)
                LevelDisplayNames.Add(RevitLevelTools.FormatLevelDisplay(lv));

            BaseLevelIndex = Levels.Count > 0 ? 0 : -1;
            TopLevelIndex = Levels.Count > 1 ? 1 : -1;
        }

        /// <summary>注入楼梯族名称列表</summary>
        public void LoadStairsTypes(IEnumerable<string> names)
        {
            StairsTypeNames.Clear();
            foreach (var n in names)
                StairsTypeNames.Add(n);
            StairsTypeIndex = StairsTypeNames.Any() ? 0 : -1;
        }

        /// <summary>注入栏杆族名称列表</summary>
        public void LoadRailingTypes(IEnumerable<string> names)
        {
            RailingTypeNames.Clear();
            foreach (var n in names)
                RailingTypeNames.Add(n);
            if (!RailingTypeNames.Any())
                RailingTypeNames.Add("（项目中暂无栏杆族）");
            RailingTypeIndex = 0;
        }

        // =============================================================
        //  私有方法
        // =============================================================

        /// <summary>
        /// 核心解算与校验。由标高/建筑类型/几何参数任意属性变化时触发。
        /// </summary>
        private void Recalculate()
        {
            if (_currentRule == null)
                return;

            Level baseLv = SelectedBaseLevel;
            Level topLv = SelectedTopLevel;

            if (baseLv == null || topLv == null)
            {
                TotalHeightDisplay = "— mm";
                ClearPreview();
                return;
            }

            double totalMm = RevitLevelTools.GetHeightDifferenceMm(baseLv, topLv);

            if (totalMm <= 0)
            {
                TotalHeightDisplay = "⚠ 顶部标高须高于底部标高";
                ClearPreview();
                HasViolation = true;
                ViolationDetail = "顶部标高不得低于底部标高。";
                GenerateCommand.RaiseCanExecuteChanged();
                return;
            }

            TotalHeightDisplay = $"总高 {totalMm:F0} mm";

            // 调用 Model 层踏步解算
            _calcResult = StairCalculator.Calculate(totalMm, _currentRule);

            // 通知所有预览属性刷新
            OnPropertyChanged(nameof(PreviewSteps));
            OnPropertyChanged(nameof(PreviewRiser));
            OnPropertyChanged(nameof(PreviewDist));
            OnPropertyChanged(nameof(PreviewRule));

            // 合规校验
            RunWidthOk = RunWidthMm >= _currentRule.MinRunWidth;
            TreadDepthOk = TreadDepthMm >= _currentRule.MinTreadDepth;

            var violations = new List<string>();
            if (!RunWidthOk)
                violations.Add(
                    $"梯段净宽 {RunWidthMm} mm 不足，规范要求 ≥ {_currentRule.MinRunWidth} mm");
            if (!TreadDepthOk)
                violations.Add(
                    $"踏步宽度 {TreadDepthMm} mm 不足，规范要求 ≥ {_currentRule.MinTreadDepth} mm");
            if (!_calcResult.IsValid)
                violations.Add(
                    $"解算踢面高 {_calcResult.RiserHeight:F1} mm 超出规范上限 {_currentRule.MaxRiserHeight} mm");

            HasViolation = violations.Any();
            ViolationDetail = HasViolation
                ? "规范预警：" + string.Join("；", violations) + "。请修正后再生成。"
                : "";

            GenerateCommand.RaiseCanExecuteChanged();
        }

        private void ClearPreview()
        {
            _calcResult = null;
            OnPropertyChanged(nameof(PreviewSteps));
            OnPropertyChanged(nameof(PreviewRiser));
            OnPropertyChanged(nameof(PreviewDist));
            OnPropertyChanged(nameof(PreviewRule));
        }

        /// <summary>GenerateCommand 的 Execute 委托：触发事件通知 View 关窗</summary>
        private void OnGenerate()
        {
            GenerateRequested?.Invoke(this, EventArgs.Empty);
        }

        // =============================================================
        //  辅助：Revit 内部单位（英尺）→ 毫米
        // =============================================================
        private static double ToMm(double feet)
            => Autodesk.Revit.DB.UnitUtils.ConvertFromInternalUnits(
                feet, Autodesk.Revit.DB.UnitTypeId.Millimeters);

        private static string GetCompassDirection(double deg)
        {
            if (deg > 337.5 || deg <= 22.5)
                return "（朝东）";
            if (deg <= 67.5)
                return "（东北）";
            if (deg <= 112.5)
                return "（朝北）";
            if (deg <= 157.5)
                return "（西北）";
            if (deg <= 202.5)
                return "（朝西）";
            if (deg <= 247.5)
                return "（西南）";
            if (deg <= 292.5)
                return "（朝南）";
            return "（东南）";
        }
    }
}