using Autodesk.Revit.DB;
using StairsPlugin.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin.ViewModel
{
    public class ViewModel: ViewModelBase
    {
        public ObservableCollection<Level> levels { get; } = new();

        Level _baseLevel;
        public Level BaseLevel
        {
            get { return _baseLevel; }
            set { if (SetField(ref _baseLevel, value))
                    Recalculate();
            }
        }

        private Level _topLevel;
        public Level TopLevel
        {
            get => _topLevel;
            set
            {
                if (SetField(ref _topLevel, value))
                    Recalculate();
            }
        }

        private string _totalHeightDisplay = "— mm";
        public string TotalHeightDisplay
        {
            get => _totalHeightDisplay;
            private set => SetField(ref _totalHeightDisplay, value);
        }

        private Model.BuildingType _buildingType = Model.BuildingType.Residential;
        public Model.BuildingType BuildingType
        {
            get => _buildingType;
            set
            {
                if (SetField(ref _buildingType, value))
                {
                    LoadRule();
                    Recalculate();
                }
            }
        }

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

        private bool _isClockwise = true;
        public bool IsClockwise
        {
            get => _isClockwise;
            set => SetField(ref _isClockwise, value);
        }

        private XYZ _p1;
        public XYZ P1
        {
            get => _p1;
            set
            {
                SetField(ref _p1, value);
                OnPropertyChanged(nameof(P1Display));
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
            }
        }

        public string P1Display => P1 == null ? "未拾取"
            : $"X={UnitUtils.ConvertFromInternalUnits(P1.X, UnitTypeId.Millimeters):F0}  Y={UnitUtils.ConvertFromInternalUnits(P1.Y, UnitTypeId.Millimeters):F0} mm";

        public string P2Display => P2 == null ? "请继续拾取 P2（方向点）"
            : $"X={UnitUtils.ConvertFromInternalUnits(P2.X, UnitTypeId.Millimeters):F0}  Y={UnitUtils.ConvertFromInternalUnits(P2.Y, UnitTypeId.Millimeters):F0} mm";

        public string ThetaDisplay
        {
            get
            {
                if (P1 == null || P2 == null)
                    return "P1 → P2 决定楼梯爬升朝向，θ = —";
                double deg = System.Math.Atan2(P2.Y - P1.Y, P2.X - P1.X) * 180 / System.Math.PI;
                if (deg < 0)
                    deg += 360;
                return $"P1 → P2 决定楼梯爬升朝向，θ = {deg:F1}°";
            }
        }

        public double DirectionAngleRad => (P1 == null || P2 == null) ? 0
            : System.Math.Atan2(P2.Y - P1.Y, P2.X - P1.X);

        // ===== 辅助选项 =====
        private bool _generateRailing = true;
        public bool GenerateRailing
        {
            get => _generateRailing;
            set => SetField(ref _generateRailing, value);
        }

        private bool _enableClearCheck = true;
        public bool EnableClearCheck
        {
            get => _enableClearCheck;
            set => SetField(ref _enableClearCheck, value);
        }

        // ===== 预览计算结果 =====
        private StairCalculationResult _calcResult;
        public string PreviewSteps => _calcResult == null ? "—" : $"{_calcResult.TotalSteps} 级";
        public string PreviewRiser => _calcResult == null ? "—" : $"{_calcResult.RiserHeight:F1} mm";
        public string PreviewDist => _calcResult == null ? "—" : $"{_calcResult.Run1Steps} + {_calcResult.Run2Steps} 步";
        public string PreviewRule => _currentRule == null ? "—" : _currentRule.RuleSource;

        // ===== 合规状态 =====
        private bool _runWidthOk = true;
        public bool RunWidthOk
        {
            get => _runWidthOk; private set => SetField(ref _runWidthOk, value);
        }
        public string RunWidthBadge => RunWidthOk ? "合规" : $"违规 < {_currentRule?.MinRunWidth} mm";

        private bool _treadDepthOk = true;
        public bool TreadDepthOk
        {
            get => _treadDepthOk; private set => SetField(ref _treadDepthOk, value);
        }
        public string TreadDepthBadge => TreadDepthOk ? "合规" : $"违规 < {_currentRule?.MinTreadDepth} mm";

        private bool _hasViolation = false;
        public bool HasViolation
        {
            get => _hasViolation; private set => SetField(ref _hasViolation, value);
        }

        private string _violationDetail = "";
        public string ViolationDetail
        {
            get => _violationDetail; private set => SetField(ref _violationDetail, value);
        }

        // ===== 当前规范 =====
        private StairCodeParams _currentRule;

        // ===== 命令 =====
        public RelayCommand GenerateCommand
        {
            get;
        }

        // ===== 构造 =====
        public ViewModel()
        {
            LoadRule();
            GenerateCommand = new RelayCommand(
                execute: () => { /* 由 Code-behind 监听并关闭窗口 */ },
                canExecute: () => P1 != null && !HasViolation
            );
        }


        void LoadRule()
        {
            _currentRule = StairCodeLibrary.Rules.TryGetValue(BuildingType, out var r) ?r:null;
        }

        void Recalculate()
        {
            if (BaseLevel==null||TopLevel==null) return;

            double totalMm = UnitUtils.ConvertFromInternalUnits(TopLevel.Elevation - BaseLevel
                .Elevation, UnitTypeId.Millimeters);


            TotalHeightDisplay = totalMm > 0 ? $"总高 {totalMm:F0} mm" : "⚠ 请选择更高标高";

            _calcResult = StairCalculator.Calculate(totalMm, _currentRule);
            OnPropertyChanged(nameof(PreviewSteps));
            OnPropertyChanged(nameof(PreviewRiser));
            OnPropertyChanged(nameof(PreviewDist));
            OnPropertyChanged(nameof(PreviewRule));

            // 合规校验
            RunWidthOk = RunWidthMm >= (_currentRule?.MinRunWidth ?? 0);
            TreadDepthOk = TreadDepthMm >= (_currentRule?.MinTreadDepth ?? 0);
            OnPropertyChanged(nameof(RunWidthBadge));
            OnPropertyChanged(nameof(TreadDepthBadge));

            var violations = new System.Collections.Generic.List<string>();
            if (!RunWidthOk)
                violations.Add($"梯段净宽 {RunWidthMm} mm 不足，规范要求 ≥ {_currentRule.MinRunWidth} mm");
            if (!TreadDepthOk)
                violations.Add($"踏步宽度 {TreadDepthMm} mm 不足，规范要求 ≥ {_currentRule.MinTreadDepth} mm");

            HasViolation = violations.Any();
            ViolationDetail = HasViolation ? "规范预警：" + string.Join("；", violations) : "";
            GenerateCommand.RaiseCanExecuteChanged();
        }
    }
}
