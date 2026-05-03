using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using StairsPlugin.Model;
using StairsPlugin.Utils;
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
    ///
    /// 重构说明（相对上一版本）：
    ///   • 移除私有 ToMm()，改用 UnitConverter.FtToMm()，与其他层统一
    ///   • Recalculate() 中的违规收集改用 ViolationCollector 值对象，
    ///     消除原先多个 return 路径上格式不一致的问题
    /// </summary>
    public class ViewModel : ViewModelBase
    {
        // =============================================================
        //  外部事件（非模态架构核心）
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
        public string TotalHeightDisplay
        {
            get => _totalHeightDisplay;
            private set
            {
                if (SetField(ref _totalHeightDisplay, value))
                    OnPropertyChanged(nameof(TotalHeightForeground));
            }
        }

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
        //  建筑功能类型
        // =============================================================
        private int _buildingTypeIndex = 0;
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
        //  平面定位与方向
        // =============================================================
        private XYZ _p1;
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
        public XYZ P2
        {
            get => _p2;
            set
            {
                SetField(ref _p2, value);
                OnPropertyChanged(nameof(P2Display));
                OnPropertyChanged(nameof(ThetaDisplay));
                Recalculate();
            }
        }

        // 使用 UnitConverter.FtToMm 替代原先私有的 ToMm() 方法
        public string P1Display => P1 == null ? "未拾取"
            : $"X={UnitConverter.FtToMm(P1.X):F0}  Y={UnitConverter.FtToMm(P1.Y):F0} mm";

        public string P2Display => P2 == null ? "未拾取"
            : $"X={UnitConverter.FtToMm(P2.X):F0}  Y={UnitConverter.FtToMm(P2.Y):F0} mm";

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

        public bool CanPickP2 => P1 != null;
        public bool IsPickP2 => P2 != null;

        public double DirectionAngleRad => (P1 == null || P2 == null) ? 0.0
            : Math.Atan2(P2.Y - P1.Y, P2.X - P1.X);

        // =============================================================
        //  盘旋方向
        // =============================================================
        private bool _isClockwise = true;
        public bool IsClockwise
        {
            get => _isClockwise;
            set => SetField(ref _isClockwise, value);
        }

        // =============================================================
        //  几何参数
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

        public string SelectedRailingTypeName =>
            (RailingTypeIndex >= 0 && RailingTypeIndex < RailingTypeNames.Count)
                ? RailingTypeNames[RailingTypeIndex] : "";

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

        public string PreviewSteps => _calcResult == null ? "—" : $"{_calcResult.TotalSteps + 2} 级";
        public string PreviewRiser => _calcResult == null ? "—" : $"{_calcResult.RiserHeight:F1} mm";

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

        public string PreviewActualTread => _actualTreadDepthMm.HasValue
            ? $"{_actualTreadDepthMm.Value:F1} mm" : "—";

        private double? _actualTreadDepthMm = null;
        public double? ActualTreadDepthMm => _actualTreadDepthMm;

        // =============================================================
        //  合规标志
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

        private bool _totalStepsOk = true;
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
            ? "合规" : $"违规：踢面高须小于 {_currentRule?.MaxRiserHeight} mm";

        private bool _hasViolation = false;
        public bool HasViolation
        {
            get => _hasViolation;
            private set => SetField(ref _hasViolation, value);
        }

        private string _violationDetail = "";
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
        //  当前规范（私有）
        // =============================================================
        private StairCodeParams _currentRule;

        // =============================================================
        //  对外只读属性（供 StairGlobalEventHandler 读取）
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
        public StairCalculationResult CalcResult => _calcResult;
        public double TotalHeightMm => totalMm;

        bool hasGeometryViolation;

        // =============================================================
        //  构造函数
        // =============================================================
        public ViewModel(ExternalEvent externalEvent, StairGlobalEventHandler handler)
        {
            _externalEvent = externalEvent
                ?? throw new ArgumentNullException(nameof(externalEvent));
            _handler = handler
                ?? throw new ArgumentNullException(nameof(handler));

            _currentRule = StairCodeLibrary.Rules[Model.BuildingType.Residential];

            GenerateCommand = new RelayCommand(
                execute: OnGenerate,
                canExecute: () => P1 != null && P2 != null && !HasViolation && !hasGeometryViolation
            );
        }

        // =============================================================
        //  公共方法：注入数据
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

        public bool TotalHeightIsWarning => totalMm <= 0;

        public void LevelInfoRefresh()
        {
            if (_currentRule == null)
                return;

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

            totalMm = RevitLevelTools.GetHeightDifferenceMm(baseLv, topLv, BaseOffsetMm);

            if (totalMm <= 0)
            {
                TotalHeightDisplay = "⚠ 顶部标高须高于底部标高";
                ClearPreview();
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
        ///   验证标高有效性、更新总高显示、校验梯段净宽和踏步宽合规性。
        ///
        /// Phase 2（仅 P1/P2 均已拾取后执行）：
        ///   由 P1P2 水平距离反算踏步数，调用 StairCalculator 解算踢面高，
        ///   校验三项 Phase-2 规范指标并更新实时预览区。
        ///
        /// 违规收集改用 ViolationCollector 值对象（替代原先散落的 violations.Add 语句），
        /// 统一格式，消除多个 return 路径上的不一致风险。
        /// </summary>
        private void Recalculate()
        {
            LevelInfoRefresh();

            RunWidthOk = RunWidthMm >= _currentRule.MinRunWidth;
            TreadDepthOk = TreadDepthMm >= _currentRule.MinTreadDepth;
            double landingMin = Math.Max(_currentRule.MinLandingDepth, RunWidthMm);
            LandingDepthOk = LandingDepthMm >= landingMin;

            OnPropertyChanged(nameof(LandingDepthHint));
            OnPropertyChanged(nameof(TotalHeightIsWarning));
            OnPropertyChanged(nameof(RunWidthBadgeText));
            OnPropertyChanged(nameof(TreadDepthBadgeText));

            // ── 使用 ViolationCollector 替代原先的 List<string> violations ──
            var collector = new ViolationCollector();

            hasGeometryViolation = !RunWidthOk || !TreadDepthOk || !LandingDepthOk || TotalHeightIsWarning;

            if (P1 != null && P2 != null && !hasGeometryViolation)
            {
                // P1P2 距离由 Revit 内部单位（英尺）转换为毫米参与计算
                double p1p2Mm = UnitConverter.FtToMm(Math.Sqrt(
                    Math.Pow(P2.X - P1.X, 2) + Math.Pow(P2.Y - P1.Y, 2)));

                int totalSteps = TreadDepthMm > 0
                    ? (int)Math.Ceiling((p1p2Mm - LandingDepthMm) * 2 / TreadDepthMm)
                    : 0;

                if (p1p2Mm <= LandingDepthMm)
                {
                    collector.Add(
                        $"P1P2 距离 {p1p2Mm:F0} mm 须大于休息平台深度 {LandingDepthMm:F0} mm");
                    ClearPreview();
                    HasViolation = collector.HasViolation;
                    ViolationDetail = collector.Detail;
                    GenerateCommand.RaiseCanExecuteChanged();
                    return;
                }

                if (totalSteps % 2 != 0)
                    totalSteps -= 1;

                if (totalSteps > 0)
                {
                    _actualTreadDepthMm = (p1p2Mm - LandingDepthMm) * 2 / totalSteps;
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

                // ── Phase-2 规范校验 ─────────────────────────────────────

                TotalStepsOk = totalSteps + 2 >= 4 && totalSteps + 2 <= 36;
                if (!TotalStepsOk)
                    collector.Add($"总踏步级数 {totalSteps + 2} 级不在规范范围（4 ~ 36 级）内");

                ActualTreadOk = _actualTreadDepthMm.HasValue
                    && _actualTreadDepthMm.Value >= _currentRule.MinTreadDepth;
                if (!ActualTreadOk)
                    collector.Add(
                        $"实际踏步宽 {(_actualTreadDepthMm.HasValue ? $"{_actualTreadDepthMm.Value:F1}" : "—")} mm"
                        + $" 低于规范下限 {_currentRule.MinTreadDepth} mm");

                RiserHeightOk = _calcResult.RiserHeight <= _currentRule.MaxRiserHeight;
                if (!RiserHeightOk)
                    collector.Add(
                        $"解算踢面高 {_calcResult.RiserHeight:F1} mm 超过上限"
                        + $"（{_currentRule.MaxRiserHeight} mm）");
            }
            else
            {
                ClearPreview();
            }

            // ── 汇总违规状态，由 ViolationCollector 统一生成 Detail 文字 ──
            HasViolation = collector.HasViolation || !TotalStepsOk || !ActualTreadOk || !RiserHeightOk;
            ViolationDetail = HasViolation ? collector.Detail : "";
            GenerateCommand.RaiseCanExecuteChanged();
        }

        private void ClearPreview()
        {
            _calcResult = null;
            _actualTreadDepthMm = null;
            TotalStepsOk = true;
            ActualTreadOk = true;
            RiserHeightOk = true;
            OnPropertyChanged(nameof(PreviewSteps));
            OnPropertyChanged(nameof(PreviewRiser));
            OnPropertyChanged(nameof(PreviewDist));
            OnPropertyChanged(nameof(PreviewRule));
            OnPropertyChanged(nameof(PreviewActualTread));
        }

        private void OnGenerate()
        {
            _handler.ViewModel = this;
            _externalEvent.Raise();
        }

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