using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using StairsPlugin.Model;
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
    ///   ✓ 点击"生成"时通过 ExternalEvent.Raise() 异步触发事务
    ///   ✗ 不持有任何 WPF 控件引用
    ///   ✗ 不调用 UIDocument.Selection（拾取点由 Code-behind 完成后写入 P1/P2）
    ///   ✗ 不包含 ShowDialog / DialogResult / IsConfirmed（已随模态方案废弃）
    ///
    /// ★ 架构变更（对应 AI对话.txt 建议）：
    ///   旧：通过 GenerateRequested 事件通知 View 关窗（模态方案）
    ///   新：通过 ExternalEvent.Raise() 异步触发 StairGlobalEventHandler（非模态方案）
    /// </summary>
    public class ViewModel : ViewModelBase
    {
        // =============================================================
        //  外部事件（非模态架构核心）
        //  由 CommandStairGenerator 构建后注入，OnGenerate() 调用 Raise()
        // =============================================================
        private readonly ExternalEvent _externalEvent;
        private readonly StairGlobalEventHandler _handler;

        // =============================================================
        //  标高数据
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

        private string _totalHeightDisplay = "— mm";
        /// <summary>总高度显示文字（只读，绑定到标高 ComboBox 旁的蓝色标签）</summary>
        public string TotalHeightDisplay
        {
            get => _totalHeightDisplay;
            private set
            {
                if (SetField(ref _totalHeightDisplay, value))
                    OnPropertyChanged(nameof(TotalHeightForeground));
            }
        }

        /// <summary>
        /// 总高显示文字的前景色。
        /// 顶部标高低于底部标高时变红，其余情况使用正常前景色。
        /// 绑定到 TextBlock.Foreground（需在 XAML 转为 SolidColorBrush）。
        /// </summary>
        public string TotalHeightForeground =>
            _totalHeightDisplay.StartsWith("⚠") ? "#E53935" : "#1565C0";


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
        /// <summary>方向点 P2；Code-behind 拾取后赋值。赋值后触发踏步解算与按钮刷新。</summary>
        public XYZ P2
        {
            get => _p2;
            set
            {
                SetField(ref _p2, value);
                OnPropertyChanged(nameof(P2Display));
                OnPropertyChanged(nameof(ThetaDisplay));
                // P2 拾取完成 → 进入完整解算流程（Phase 2）
                Recalculate();
            }
        }

        /// <summary>P1 坐标显示文字（只读，绑定到 TxtCoordP1）</summary>
        public string P1Display => P1 == null ? "未拾取"
            : $"X={ToMm(P1.X):F0}  Y={ToMm(P1.Y):F0} mm";

        /// <summary>P2 坐标显示文字（只读，绑定到 TxtCoordP2）</summary>
        public string P2Display => P2 == null ? "未拾取"
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

        /// <summary>P2 拾取按钮的 IsEnabled（绑定到 BtnPickP2.IsEnabled）</summary>
        public bool CanPickP2 => P1 != null;

        public bool IsPickP2 => P2 != null;

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
            set
            {
                if (SetField(ref _landingDepthMm, value))
                    Recalculate();
            }
        }

        private double _baseOffsetMm = 0;
        public double BaseOffsetMm
        {
            get => _baseOffsetMm;
            set
            {
                if (SetField(ref _baseOffsetMm, value))
                    Recalculate();
            }
        }

        // =============================================================
        //  辅助选项
        // =============================================================
        private bool _generateRailing = false;
        public bool GenerateRailing
        {
            get => _generateRailing;
            set => SetField(ref _generateRailing, value);
        }

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

        private bool _enableClearCheck = false;
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
        /// <summary>
        /// 显示两跑的步数分配和水平投影长度。
        /// 长度 = 步数 × 用户输入的踏步宽（TreadDepthMm），
        /// 当 TreadDepthMm 变化时此属性会随之刷新，使踏宽输入有实际预览反馈。
        /// </summary>
        public string PreviewDist
        {
            get
            {
                if (_calcResult == null)
                    return "—";
                return $"{_calcResult.Run1Steps} + {_calcResult.Run2Steps} 步  ";
            }
        }
        public string PreviewRule => _currentRule?.RuleSource ?? "—";

        /// <summary>
        /// 几何约束反算后的实际踏步宽（只读，显示在预览区）。
        /// 与用户输入的 TreadDepthMm（期望值）分离，互不干扰。
        /// </summary>
        public string PreviewActualTread => _actualTreadDepthMm.HasValue
            ? $"{_actualTreadDepthMm.Value:F1} mm"
            : "—";

        private double? _actualTreadDepthMm = null;
        public double? ActualTreadDepthMm
        {
            get => _actualTreadDepthMm;
        }


        // =============================================================
        //  合规标志（绑定到 Badge 的 DataTrigger 和 PanelWarn 的 Visibility）
        // =============================================================
        private bool _runWidthOk = true;
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
        public bool TreadDepthOk
        {
            get => _treadDepthOk;
            private set
            {
                if (SetField(ref _treadDepthOk, value))
                    OnPropertyChanged(nameof(TreadDepthBadgeText));
            }
        }

        private bool _landingDepthOk = true;
        public bool LandingDepthOk
        {
            get => _landingDepthOk;
            private set
            {
                if (SetField(ref _landingDepthOk, value))
                    OnPropertyChanged(nameof(LandingDepthHint));
            }
        }


        public string RunWidthBadgeText => RunWidthOk
            ? "合规" : $"违规 < {_currentRule?.MinRunWidth} mm";

        public string TreadDepthBadgeText => TreadDepthOk
            ? "合规" : $"违规 < {_currentRule?.MinTreadDepth} mm";

        double totalMm = 0;

        /// <summary>
        /// 平台深度输入框下方的提示文字，共三种状态：
        ///   1) 规范尚未载入或平台深度为 0：显示 "默认同梯段净宽"
        ///   2) 平台深度满足下限要求：显示 "-"
        ///   3) 平台深度不满足下限：显示 "应大于 {下限} mm"
        /// 下限 = Max(规范 MinLandingDepth, 梯段净宽 RunWidthMm)
        /// </summary>
        public string LandingDepthHint
        {
            get
            {
                if ((_currentRule == null || LandingDepthMm == RunWidthMm) && LandingDepthOk)
                    return "同梯段净宽";
                double minRequired = Math.Max(_currentRule.MinLandingDepth, RunWidthMm);
                if (LandingDepthMm >= minRequired)
                    return "合规";
                return $"应大于 {minRequired:F0} mm";
            }
        }

        // ── Phase-2 合规标志（依赖 P1/P2 解算结果）──────────────────────

        private bool _totalStepsOk = true;
        /// <summary>总踏步级数是否在规范范围内（4 ≤ N ≤ 36）</summary>
        public bool TotalStepsOk
        {
            get => _totalStepsOk;
            private set
            {
                if (SetField(ref _totalStepsOk, value))
                    OnPropertyChanged(nameof(TotalStepsBadgeText));
            }
        }
        public string TotalStepsBadgeText => TotalStepsOk
            ? "合规" : "违规：级数须在 4 ~ 36 级之间";

        private bool _actualTreadOk = true;
        /// <summary>实际踏步宽是否满足规范下限</summary>
        public bool ActualTreadOk
        {
            get => _actualTreadOk;
            private set
            {
                if (SetField(ref _actualTreadOk, value))
                    OnPropertyChanged(nameof(ActualTreadBadgeText));
            }
        }
        public string ActualTreadBadgeText => ActualTreadOk
            ? "合规" : $"违规 < {_currentRule?.MinTreadDepth} mm";

        private bool _riserHeightOk = true;
        /// <summary>踢面高是否在规范范围内（MinRiserHeight ≤ h ≤ MaxRiserHeight）</summary>
        public bool RiserHeightOk
        {
            get => _riserHeightOk;
            private set
            {
                if (SetField(ref _riserHeightOk, value))
                    OnPropertyChanged(nameof(RiserHeightBadgeText));
            }
        }
        public string RiserHeightBadgeText => RiserHeightOk
            ? "合规"
            : $"违规：踢面高须小于 {_currentRule?.MaxRiserHeight} mm ";

        private bool _hasViolation = false;
        /// <summary>是否存在违规 → 绑定到 PanelWarn.Visibility</summary>
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
        //  对外只读属性（供 StairGlobalEventHandler 读取生成参数）
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

        /// <summary>
        /// Recalculate() 解算后的踏步结果快照。
        /// StairGlobalEventHandler 直接读取，避免重复推导。
        /// P1/P2 尚未拾取或几何违规时为 null。
        /// </summary>
        public StairCalculationResult CalcResult => _calcResult;

        /// <summary>
        /// 含底部偏移的总高度（mm），与 Recalculate() 内部 totalMm 保持一致。
        /// StairGlobalEventHandler 读取后换算为 Revit 内部单位（英尺）。
        /// </summary>
        public double TotalHeightMm => totalMm;

        // =============================================================
        //  构造函数
        //  ★ 接收 ExternalEvent + Handler（非模态架构必须）
        // =============================================================
        public ViewModel(ExternalEvent externalEvent, StairGlobalEventHandler handler)
        {
            _externalEvent = externalEvent
                ?? throw new ArgumentNullException(nameof(externalEvent));
            _handler = handler
                ?? throw new ArgumentNullException(nameof(handler));

            // 初始化规范（默认住宅）
            _currentRule = StairCodeLibrary.Rules[Model.BuildingType.Residential];

            // 生成命令：CanExecute 要求 P1、P2 均已拾取且无规范违规
            GenerateCommand = new RelayCommand(
                execute: OnGenerate,
                canExecute: () => P1 != null && P2 != null && !HasViolation
            );
        }

        // =============================================================
        //  公共方法：由 CommandStairGenerator 在 Show 前调用，注入数据
        // =============================================================

        public void LoadLevels(IEnumerable<Level> levels)
        {
            Levels.AddRange(levels);
            LevelDisplayNames.Clear();
            foreach (var lv in Levels)
                LevelDisplayNames.Add(RevitLevelTools.FormatLevelDisplay(lv));

            BaseLevelIndex = Levels.Count > 0 ? 0 : -1;
            TopLevelIndex = Levels.Count > 1 ? 1 : -1;
            LevelInfoRefresh();
        }

        public void LoadStairsTypes(IEnumerable<string> names)
        {
            StairsTypeNames.Clear();
            foreach (var n in names)
                StairsTypeNames.Add(n);
            StairsTypeIndex = StairsTypeNames.Any() ? 0 : -1;
        }

        public void LoadRailingTypes(IEnumerable<string> names)
        {
            RailingTypeNames.Clear();
            foreach (var n in names)
                RailingTypeNames.Add(n);
            if (!RailingTypeNames.Any())
                RailingTypeNames.Add("（项目中暂无栏杆族）");
            RailingTypeIndex = 0;
        }

        public bool TotalHeightIsWarning
        {
            get
            {
                if (totalMm <= 0)
                    return true;
                else
                    return false;
            }

        }

        public void LevelInfoRefresh()
        {
            if (_currentRule == null)
                return;

            // ── Phase 1：标高与几何参数校验（不依赖 P2）─────────────────
            Level baseLv = SelectedBaseLevel;
            Level topLv = SelectedTopLevel;

            if (baseLv == null || topLv == null)
            {
                TotalHeightDisplay = "— mm";
                ClearPreview();
                HasViolation = false;
                ViolationDetail = "";
                GenerateCommand.RaiseCanExecuteChanged();
                return;
            }

            totalMm = RevitLevelTools.GetHeightDifferenceMm(baseLv, topLv);

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
        }

        // =============================================================
        //  私有方法
        // =============================================================

        /// <summary>
        /// 核心解算与校验，分两阶段执行：
        ///
        /// Phase 1（始终执行）：
        ///   验证标高有效性、更新总高显示、校验梯段净宽和踏步宽度合规性。
        ///   此阶段不依赖 P2，用户填写几何参数时即可实时看到违规徽章。
        ///
        /// Phase 2（仅 P2 已拾取后执行）：
        ///   调用 StairCalculator 解算踏步数与踢面高，校验踢面高是否超限，
        ///   更新实时预览区（级数 / 踢面高 / 分配方案）。
        ///
        /// 生成按钮 CanExecute = P1 ≠ null ∧ P2 ≠ null ∧ !HasViolation。
        /// </summary>
        private void Recalculate()
        {
            LevelInfoRefresh();
            // ── totalMm 加入底部偏移（与 StairGlobalEventHandler 保持一致）─
            totalMm += BaseOffsetMm;

            // 几何参数合规性（无论 P2 是否拾取，始终实时反馈）
            RunWidthOk = RunWidthMm >= _currentRule.MinRunWidth;
            TreadDepthOk = TreadDepthMm >= _currentRule.MinTreadDepth;
            // 休息平台深度须同时满足：规范最小值 AND 不小于梯段净宽
            double landingMin = Math.Max(_currentRule.MinLandingDepth, RunWidthMm);
            LandingDepthOk = LandingDepthMm >= landingMin;
            // LandingDepthHint 依赖 LandingDepthMm 和 RunWidthMm，强制刷新
            OnPropertyChanged(nameof(LandingDepthHint));
            OnPropertyChanged(nameof(TotalHeightIsWarning));
            OnPropertyChanged(nameof(RunWidthBadgeText));
            OnPropertyChanged(nameof(TreadDepthBadgeText));
            var violations = new List<string>();

            // ── Phase 2：踏步解算（P1、P2 均已拾取后，任何影响计算的参数变化均触发）────
            bool hasGeometryViolation = !RunWidthOk || !TreadDepthOk || !LandingDepthOk || TotalHeightIsWarning;
            if (P1 != null && P2 != null && !hasGeometryViolation)
            {
                // ── 由水平约束推导踏步级数 ───────────────────────────────────
                // P1→P2 距离 = 楼梯水平投影矩形的一条边（固定值）
                // TotalSteps × TreadDepthMm + LandingDepthMm = P1P2距离
                // → TotalSteps = Floor((P1P2距离 - LandingDepthMm) / TreadDepthMm)
                double p1p2Mm = ToMm(Math.Sqrt(
                    Math.Pow(P2.X - P1.X, 2) + Math.Pow(P2.Y - P1.Y, 2)));
                int totalSteps = TreadDepthMm > 0
                    ? (int)Math.Floor((p1p2Mm - LandingDepthMm) * 2 / TreadDepthMm)
                    : 0;

                // P1P2 距离必须大于休息平台深度，否则扣除平台后无空间容纳任何踏步
                if (p1p2Mm <= LandingDepthMm)
                {
                    violations.Add(
                        $"P1P2 距离 {p1p2Mm:F0} mm 须大于休息平台深度 {LandingDepthMm:F0} mm");
                    ClearPreview();
                    // 跳过后续解算，直接进入违规汇总
                    HasViolation = true;
                    ViolationDetail = "规范预警：\n" + string.Join("；\n", violations) + "。\n请修正后再生成。";
                    GenerateCommand.RaiseCanExecuteChanged();
                    return;
                }

                // 双跑楼梯每跑整数级，总步数必须为偶数
                if (totalSteps % 2 != 0)
                    totalSteps -= 1;

                // 反算实际踏步宽（写入只读预览属性，不回写用户输入框）
                if (totalSteps > 0)
                {
                    _actualTreadDepthMm = Math.Round((p1p2Mm - LandingDepthMm) * 2 / (totalSteps - 2), 1);
                    OnPropertyChanged(nameof(PreviewActualTread));
                }
                else
                {
                    _actualTreadDepthMm = null;
                    OnPropertyChanged(nameof(PreviewActualTread));
                }

                _calcResult = StairCalculator.Calculate(totalMm, totalSteps, _currentRule);

                OnPropertyChanged(nameof(PreviewSteps));
                OnPropertyChanged(nameof(PreviewRiser));
                OnPropertyChanged(nameof(PreviewDist));
                OnPropertyChanged(nameof(PreviewRule));

                // ── 三项 Phase-2 规范校验 ────────────────────────────────

                // ① 踏步级数：4 ≤ totalSteps ≤ 36
                TotalStepsOk = totalSteps >= 4 && totalSteps <= 36;
                if (!TotalStepsOk)
                    violations.Add(
                        $"总踏步级数 {totalSteps} 级不在规范范围（4 ~ 36 级）内");

                // ② 实际踏步宽（几何反算值）须满足规范下限
                ActualTreadOk = _actualTreadDepthMm.HasValue
                    && _actualTreadDepthMm.Value >= _currentRule.MinTreadDepth;
                if (!ActualTreadOk)
                    violations.Add(
                        $"实际踏步宽 {(_actualTreadDepthMm.HasValue ? $"{_actualTreadDepthMm.Value:F1}" : "—")} mm"
                        + $" 低于规范下限 {_currentRule.MinTreadDepth} mm");

                // ③ 踢面高：MinRiserHeight ≤ h ≤ MaxRiserHeight
                RiserHeightOk = _calcResult.RiserHeight <= _currentRule.MaxRiserHeight;
                if (!RiserHeightOk)
                    violations.Add(
                        $"解算踢面高 {_calcResult.RiserHeight:F1} mm 大于"
                        + $"（{_currentRule.MaxRiserHeight} mm）内");
            }
            else
            {
                // P1 或 P2 尚未拾取完毕：预览区保持占位符，仅显示几何合规徽章
                ClearPreview();
            }

            // ── 汇总违规状态，刷新按钮 ───────────────────────────────────
            HasViolation = violations.Any()
                          || !RunWidthOk
                          || !TreadDepthOk
                          || !LandingDepthOk
                          || !TotalStepsOk
                          || !ActualTreadOk
                          || !RiserHeightOk
                          || TotalHeightIsWarning;
            ViolationDetail = HasViolation
                ? "规范预警：\n" + string.Join("；\n", violations) + "。\n请修正后再生成。"
                : "";

            GenerateCommand.RaiseCanExecuteChanged();
        }

        private void ClearPreview()
        {
            _calcResult = null;
            _actualTreadDepthMm = null;
            // Phase-2 标志重置：P1/P2 未就绪时不应残留上次校验结果
            TotalStepsOk = true;
            ActualTreadOk = true;
            RiserHeightOk = true;
            OnPropertyChanged(nameof(PreviewSteps));
            OnPropertyChanged(nameof(PreviewRiser));
            OnPropertyChanged(nameof(PreviewDist));
            OnPropertyChanged(nameof(PreviewRule));
            OnPropertyChanged(nameof(PreviewActualTread));
        }

        /// <summary>
        /// GenerateCommand 的 Execute 委托。
        /// ★ 新方案：将自身引用写入 Handler，然后调用 ExternalEvent.Raise()。
        ///   Revit 将在下一个空闲时间点异步回调 StairGlobalEventHandler.Execute()。
        ///   无需通知 View 关窗，用户可保持窗口开启以便连续生成。
        /// </summary>
        private void OnGenerate()
        {
            _handler.ViewModel = this; // 传递当前参数快照引用
            _externalEvent.Raise();    // 异步触发 Revit 事务
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