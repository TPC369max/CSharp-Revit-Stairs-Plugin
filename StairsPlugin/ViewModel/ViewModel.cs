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
    /// 双跑楼梯生成窗口的 ViewModel（MVVM 模式的中间层）。
    ///
    /// ── 职责边界 ──────────────────────────────────────────────────────
    ///   ✓ 所有界面状态属性（标高列表、预览文字、合规标志）
    ///   ✓ 规范校验与踏步解算（调用 Model 层的 StairCalculator / StairCodeLibrary）
    ///   ✓ 命令定义（GenerateCommand）及 CanExecute 逻辑
    ///   ✓ 点击"生成"时通过 ExternalEvent.Raise() 将任务推送至 Revit API 上下文
    ///   ✗ 不持有任何 WPF 控件引用
    ///   ✗ 不调用 UIDocument.Selection（拾取由 Code-behind 完成后写入 P1/P2 属性）
    ///
    /// ── 解算触发时机 ─────────────────────────────────────────────────
    /// Recalculate() 在以下属性变更时自动调用：
    ///   BaseLevelIndex / TopLevelIndex / BuildingTypeIndex /
    ///   P2（赋值时）/ RunWidthText / TreadDepthText / LandingDepthText / BaseOffsetText
    ///
    /// ── 重构说明（相对上一版本） ──────────────────────────────────────
    ///   • 移除私有 ToMm()，改用 UnitConverter.FtToMm()，与其他层统一。
    ///   • Recalculate() 中的违规收集改用 ViolationCollector 值对象，
    ///     消除原先多个 return 路径上格式不一致的问题。
    /// </summary>
    public class ViewModel : ViewModelBase
    {
        // =============================================================
        //  外部事件（非模态架构核心）
        //
        //  _externalEvent 由 CommandStairGenerator 创建并注入，
        //  OnGenerate() 通过 Raise() 将生成任务异步提交给 Revit 主线程。
        //  _handler 在 Raise() 前被写入 this 引用，
        //  使 Handler.Execute() 能读取当前 ViewModel 的最新参数。
        // =============================================================
        private readonly ExternalEvent _externalEvent;
        private readonly StairGlobalEventHandler _handler;

        // =============================================================
        //  标高数据
        //
        //  Levels        — 原始标高对象列表（按高程升序）
        //  LevelDisplayNames — 对应的格式化显示字符串（绑定到 ComboBox.ItemsSource）
        // =============================================================
        public List<Level> Levels { get; } = new List<Level>();

        public ObservableCollection<string> LevelDisplayNames
        {
            get;
        }
            = new ObservableCollection<string>();

        /// <summary>
        /// 底部标高的 ComboBox 选中索引。
        /// 变更时触发 Recalculate() 以更新总高和踏步解算。
        /// </summary>
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

        /// <summary>
        /// 顶部标高的 ComboBox 选中索引。
        /// 变更时触发 Recalculate()。
        /// </summary>
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

        /// <summary>
        /// 总高显示文字（绑定到顶部标高旁的 Badge）。
        /// 合规时显示"总高 XXXX mm"（蓝色）；
        /// 顶低于底时显示"⚠ 顶部标高须高于底部标高"（橙色）。
        /// 变更时同时通知 TotalHeightForeground 更新颜色。
        /// </summary>
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

        /// <summary>
        /// 总高 Badge 的前景色：警告状态（⚠ 开头）时为红色，否则为蓝色。
        /// 由 XAML DataTrigger 绑定，此属性仅供兼容旧绑定路径使用。
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

        /// <summary>楼梯族类型 ComboBox 的选中索引。</summary>
        private int _stairsTypeIndex = 0;
        public int StairsTypeIndex
        {
            get => _stairsTypeIndex;
            set => SetField(ref _stairsTypeIndex, value);
        }

        // =============================================================
        //  建筑功能类型
        //
        //  索引与 BuildingType 枚举值一一对应（强制类型转换安全）：
        //    0=Residential, 1=Public, 2=Attached, 3=Supertall
        // =============================================================
        private int _buildingTypeIndex = 0;
        public int BuildingTypeIndex
        {
            get => _buildingTypeIndex;
            set
            {
                if (SetField(ref _buildingTypeIndex, value))
                {
                    // 切换规范规则集，并立即重新解算以更新合规 Badge
                    _currentRule = StairCodeLibrary.Rules[(Model.BuildingType)value];
                    Recalculate();
                }
            }
        }

        // =============================================================
        //  平面定位与方向
        //
        //  P1：楼梯插入点（由 Code-behind BtnPickP1_Click 写入）
        //  P2：方向点（由 Code-behind BtnPickP2_Click 写入）
        //  P1→P2 向量决定楼梯爬升轴的水平方向角（θ）。
        // =============================================================
        private XYZ _p1;
        /// <summary>
        /// 楼梯插入点（世界坐标，Revit 内部单位英尺）。
        /// 赋值后触发 P1Display、CanPickP2、GenerateCommand.CanExecute 等多个通知。
        /// </summary>
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
        /// <summary>
        /// 方向点（世界坐标，Revit 内部单位英尺）。
        /// 赋值后触发 P2Display、ThetaDisplay，并启动 Phase-2 解算（Recalculate）。
        /// </summary>
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

        /// <summary>P1 坐标的格式化显示（mm，绑定到 P1 坐标文本框）。</summary>
        public string P1Display => P1 == null ? "未拾取"
            : $"X={UnitConverter.FtToMm(P1.X):F0}  Y={UnitConverter.FtToMm(P1.Y):F0} mm";

        /// <summary>P2 坐标的格式化显示（mm）。</summary>
        public string P2Display => P2 == null ? "未拾取"
            : $"X={UnitConverter.FtToMm(P2.X):F0}  Y={UnitConverter.FtToMm(P2.Y):F0} mm";

        /// <summary>
        /// P1→P2 方向角提示文字（含罗盘方位词）。
        /// 未完成 P1/P2 拾取时显示占位符。
        /// 角度由 Atan2 计算，范围 [0°, 360°)。
        /// </summary>
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

        /// <summary>P2 拾取按钮的启用状态：仅当 P1 已拾取后方可拾取 P2。</summary>
        public bool CanPickP2 => P1 != null;

        /// <summary>P2 是否已拾取（当前未在 XAML 中直接绑定，保留供后续使用）。</summary>
        public bool IsPickP2 => P2 != null;

        /// <summary>
        /// P1→P2 方向角（弧度），供 StairGlobalEventHandler 计算坐标变换矩阵。
        /// P1/P2 任一未设置时返回 0.0（默认朝东，不影响生成按钮禁用逻辑）。
        /// </summary>
        public double DirectionAngleRad => (P1 == null || P2 == null) ? 0.0
            : Math.Atan2(P2.Y - P1.Y, P2.X - P1.X);

        // =============================================================
        //  盘旋方向
        // =============================================================
        private bool _isClockwise = true;
        /// <summary>
        /// true = 右旋（顺时针）：俯视时第一跑在右侧（Y 负方向）。
        /// false = 左旋（逆时针）：第一跑在左侧（Y 正方向）。
        /// 影响两跑的局部 Y 坐标符号及平台位置。
        /// </summary>
        public bool IsClockwise
        {
            get => _isClockwise;
            set => SetField(ref _isClockwise, value);
        }

        // =============================================================
        //  几何参数（双层结构：Text + 内部 double）
        //
        //  每个数值参数由两层组成：
        //    • XxxText  (string) ← XAML TextBox 双向绑定目标，
        //                          负责实时解析和错误追踪（SetInputError）。
        //    • _xxxMm   (double) ← 业务计算层使用的合法数值，
        //                          仅在解析成功且值变化时更新，
        //                          避免无效解析值流入 StairCalculator。
        //
        //  XAML 修改说明：
        //    TextBox.Text 绑定 XxxText（不绑定 XxxMm），
        //    UpdateSourceTrigger=PropertyChanged 实现逐字实时校验。
        //    红框/错误提示通过 HasInputError 或各字段对应的 XxxHasError 属性驱动，
        //    不使用 ValidatesOnExceptions（避免异常冒泡导致 WPF 警告）。
        // =============================================================

        // ── 梯段净宽（最低下限由 _currentRule.MinRunWidth 决定）────────
        private double _runWidthMm = 1200;
        public double RunWidthMm
        {
            get => _runWidthMm;
            set => RunWidthText = value.ToString(); // 写入经文本路由，保证 UI 同步
        }

        private string _runWidthText = "1200";
        /// <summary>
        /// 梯段净宽 TextBox 绑定属性（mm）。
        /// 解析成功且值变化时更新 _runWidthMm 并触发 Recalculate。
        /// 解析失败（非法字符）时登记 SetInputError，禁用"生成"按钮。
        /// </summary>
        public string RunWidthText
        {
            get => _runWidthText;
            set
            {
                if (SetField(ref _runWidthText, value))
                {
                    if (double.TryParse(value, out double parsed) && parsed > 0)
                    {
                        SetInputError(nameof(RunWidthText), false);
                        if (_runWidthMm != parsed)
                        {
                            _runWidthMm = parsed;
                            Recalculate();
                        }
                    }
                    else
                        SetInputError(nameof(RunWidthText), true);
                }
            }
        }

        // ── 踏步宽（用户期望值；实际踏步宽由 P1P2 距离反算，见 ActualTreadDepthMm）──
        private double _treadDepthMm = 260;
        public double TreadDepthMm
        {
            get => _treadDepthMm;
            set => TreadDepthText = value.ToString();
        }

        private string _treadDepthText = "260";
        /// <summary>
        /// 踏步宽 TextBox 绑定属性（mm）。
        /// 用户输入的"期望踏步宽"，用于合规 Badge 判断；
        /// 实际生成所用的踏步宽由 P1P2 距离反算（ActualTreadDepthMm）。
        /// </summary>
        public string TreadDepthText
        {
            get => _treadDepthText;
            set
            {
                if (SetField(ref _treadDepthText, value))
                {
                    if (double.TryParse(value, out double parsed) && parsed > 0)
                    {
                        SetInputError(nameof(TreadDepthText), false);
                        if (_treadDepthMm != parsed)
                        {
                            _treadDepthMm = parsed;
                            Recalculate();
                        }
                    }
                    else
                        SetInputError(nameof(TreadDepthText), true);
                }
            }
        }

        // ── 梯井宽（两跑之间的空隙，允许为 0）────────────────────────
        private double _wellWidthMm = 100;
        public double WellWidthMm
        {
            get => _wellWidthMm;
            set => WellWidthText = value.ToString();
        }

        private string _wellWidthText = "100";
        /// <summary>
        /// 梯井宽 TextBox 绑定属性（mm）。
        /// 允许为 0（两跑紧贴）。不触发 Recalculate，
        /// 仅影响 StairGlobalEventHandler 中的横向偏移计算。
        /// </summary>
        public string WellWidthText
        {
            get => _wellWidthText;
            set
            {
                if (SetField(ref _wellWidthText, value))
                {
                    if (double.TryParse(value, out double parsed) && parsed >= 0)
                    {
                        SetInputError(nameof(WellWidthText), false);
                        if (_wellWidthMm != parsed)
                        {
                            _wellWidthMm = parsed;
                            OnPropertyChanged(nameof(WellWidthMm));
                        }
                    }
                    else
                        SetInputError(nameof(WellWidthText), true);
                }
            }
        }

        // ── 休息平台深度（须 ≥ max(MinLandingDepth, RunWidthMm)）────────
        private double _landingDepthMm = 1200;
        public double LandingDepthMm
        {
            get => _landingDepthMm;
            set => LandingDepthText = value.ToString();
        }

        private string _landingDepthText = "1200";
        /// <summary>
        /// 休息平台深度 TextBox 绑定属性（mm）。
        /// 变更时触发 Recalculate，同时影响 P1P2 有效跨度的计算
        /// （跨度 = 2×每跑长度 + 平台深度，须大于总高/最大踢面高 × 踏步宽）。
        /// </summary>
        public string LandingDepthText
        {
            get => _landingDepthText;
            set
            {
                if (SetField(ref _landingDepthText, value))
                {
                    if (double.TryParse(value, out double parsed) && parsed > 0)
                    {
                        SetInputError(nameof(LandingDepthText), false);
                        if (_landingDepthMm != parsed)
                        {
                            _landingDepthMm = parsed;
                            Recalculate();
                        }
                    }
                    else
                        SetInputError(nameof(LandingDepthText), true);
                }
            }
        }

        // ── 底部偏移（允许为负，表示起步低于底部标高）────────────────
        private double _baseOffsetMm = 0;
        public double BaseOffsetMm
        {
            get => _baseOffsetMm;
            set => BaseOffsetText = value.ToString();
        }

        private string _baseOffsetText = "0";
        /// <summary>
        /// 底部偏移 TextBox 绑定属性（mm）。
        /// 允许为负（如地下室楼梯起步在楼板面以下）。
        /// 偏移值参与总高计算（GetHeightDifferenceMm 中扣除偏移）。
        /// </summary>
        public string BaseOffsetText
        {
            get => _baseOffsetText;
            set
            {
                if (SetField(ref _baseOffsetText, value))
                {
                    if (double.TryParse(value, out double parsed)) // 偏移可为 0 或负
                    {
                        SetInputError(nameof(BaseOffsetText), false);
                        if (_baseOffsetMm != parsed)
                        {
                            _baseOffsetMm = parsed;
                            Recalculate();
                        }
                    }
                    else
                        SetInputError(nameof(BaseOffsetText), true);
                }
            }
        }

        // =============================================================
        //  辅助选项
        // =============================================================

        /// <summary>是否在楼梯生成后保留（或替换为指定类型的）栏杆扶手。</summary>
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

        /// <summary>当前选中的栏杆族类型名称，供 StairGlobalEventHandler 查找 ElementType。</summary>
        public string SelectedRailingTypeName =>
            (RailingTypeIndex >= 0 && RailingTypeIndex < RailingTypeNames.Count)
                ? RailingTypeNames[RailingTypeIndex] : "";

        private int _railingTypeIndex = 0;
        public int RailingTypeIndex
        {
            get => _railingTypeIndex;
            set => SetField(ref _railingTypeIndex, value);
        }

        /// <summary>
        /// 是否在生成前执行净空合规校验（射线法）。
        /// 开启后 StairGlobalEventHandler 会调用 ClearanceChecker.Check()，
        /// 不合规时弹出 Yes/No 对话框。
        /// </summary>
        private bool _enableClearCheck = false;
        public bool EnableClearCheck
        {
            get => _enableClearCheck;
            set => SetField(ref _enableClearCheck, value);
        }

        // =============================================================
        //  实时预览（只读，由 Recalculate 计算后通知更新）
        // =============================================================
        private StairCalculationResult _calcResult;

        /// <summary>总踏步级数预览文字（= TotalSteps + 2，含首尾踢面）。</summary>
        public string PreviewSteps => _calcResult == null ? "—" : $"{_calcResult.TotalSteps + 2} 级";

        /// <summary>踢面高预览文字（mm，保留 1 位小数）。</summary>
        public string PreviewRiser => _calcResult == null ? "—" : $"{_calcResult.RiserHeight:F1} mm";

        /// <summary>两跑步数分配预览（格式："N1 + N2 步"）。</summary>
        public string PreviewDist
        {
            get
            {
                if (_calcResult == null)
                    return "—";
                return $"{_calcResult.Run1Steps} + {_calcResult.Run2Steps} 步  ";
            }
        }

        /// <summary>当前规范依据（来源字符串，绑定到预览区底部）。</summary>
        public string PreviewRule => _currentRule?.RuleSource ?? "—";

        /// <summary>由 P1P2 距离反算的实际踏步宽预览（mm）。</summary>
        public string PreviewActualTread => _actualTreadDepthMm.HasValue
            ? $"{_actualTreadDepthMm.Value:F1} mm" : "—";

        // 由 P1P2 距离和平台深度反算的实际踏步宽，可能与用户输入的期望踏步宽不同
        private double? _actualTreadDepthMm = null;
        /// <summary>
        /// 实际踏步宽（mm）。由几何反算：
        ///   actualTread = (p1p2距离 - 平台深度) × 2 / 总踏步数
        /// null 表示 P1P2 未拾取或解算失败，供 StairGlobalEventHandler 使用。
        /// </summary>
        public double? ActualTreadDepthMm => _actualTreadDepthMm;

        // =============================================================
        //  合规标志（影响 Badge 颜色和"生成"按钮的 CanExecute）
        // =============================================================

        private bool _runWidthOk = true;
        /// <summary>梯段净宽是否满足规范下限（≥ MinRunWidth）。</summary>
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
        /// <summary>用户期望踏步宽是否满足规范下限（≥ MinTreadDepth）。</summary>
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
        /// <summary>休息平台深度是否满足规范要求（≥ max(MinLandingDepth, RunWidthMm)）。</summary>
        public bool LandingDepthOk
        {
            get => _landingDepthOk;
            private set
            {
                if (SetField(ref _landingDepthOk, value))
                    OnPropertyChanged(nameof(LandingDepthHint));
            }
        }

        /// <summary>梯段净宽 Badge 文字（合规："合规"；违规：显示要求值）。</summary>
        public string RunWidthBadgeText => RunWidthOk
            ? "合规" : $"违规 < {_currentRule?.MinRunWidth} mm";

        /// <summary>踏步宽 Badge 文字。</summary>
        public string TreadDepthBadgeText => TreadDepthOk
            ? "合规" : $"违规 < {_currentRule?.MinTreadDepth} mm";

        // 用于总高差计算的字段，由 LevelInfoRefresh 赋值
        double totalMm = 0;

        /// <summary>
        /// 休息平台深度提示文字。
        /// 合规时显示"合规"或"同梯段净宽"；
        /// 违规时显示最小要求值以引导用户修正。
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

        private bool _totalStepsOk = true;
        /// <summary>总踏步级数（+2 后）是否在规范范围内（4 ~ 36 级）。</summary>
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
        /// <summary>由 P1P2 距离反算的实际踏步宽是否满足规范下限。</summary>
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
        /// <summary>解算踢面高是否在规范上限以内（≤ MaxRiserHeight）。</summary>
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

        // =============================================================
        //  输入错误追踪（TextBox 内容非法时置位，控制"生成"按钮状态）
        //
        //  使用 HashSet<string> 存储当前含非法字符的字段名，
        //  任意字段有错时 HasInputError = true，禁用"生成"按钮，
        //  避免 Revit 事务收到 NaN 或负数等无效参数。
        // =============================================================
        private readonly System.Collections.Generic.HashSet<string> _inputErrors
            = new System.Collections.Generic.HashSet<string>();

        /// <summary>
        /// 任意数值 TextBox 含非法字符（无法 double.TryParse）时为 true。
        /// 加入 GenerateCommand.canExecute，确保非法输入时按钮变灰。
        /// </summary>
        public bool HasInputError => _inputErrors.Count > 0;

        /// <summary>
        /// 登记或撤销某字段的解析错误，并通知 GenerateCommand 重新评估 CanExecute。
        /// 由各数值属性的 setter 调用：解析失败时 hasError=true，成功时 hasError=false。
        /// </summary>
        /// <param name="fieldName">字段属性名（如 nameof(RunWidthText)）</param>
        /// <param name="hasError">true=登记错误；false=撤销错误</param>
        private void SetInputError(string fieldName, bool hasError)
        {
            if (hasError)
                _inputErrors.Add(fieldName);
            else
                _inputErrors.Remove(fieldName);
            OnPropertyChanged(nameof(HasInputError));
            GenerateCommand?.RaiseCanExecuteChanged();
        }

        private bool _hasViolation = false;
        /// <summary>
        /// 是否存在至少一条规范违规（Phase-1 或 Phase-2）。
        /// true 时"生成"按钮禁用，并显示红色违规警告区域。
        /// </summary>
        public bool HasViolation
        {
            get => _hasViolation;
            private set => SetField(ref _hasViolation, value);
        }

        private string _violationDetail = "";
        /// <summary>违规详情文字（绑定到红色警告 Border 内的 TextBlock）。</summary>
        public string ViolationDetail
        {
            get => _violationDetail;
            private set => SetField(ref _violationDetail, value);
        }

        // =============================================================
        //  命令
        // =============================================================
        /// <summary>
        /// "生成楼梯"按钮的绑定命令。
        /// CanExecute 条件（全部满足时按钮激活）：
        ///   1. P1 已拾取
        ///   2. P2 已拾取
        ///   3. 无规范违规（!HasViolation）
        ///   4. 无几何违规（!hasGeometryViolation，即梯段净宽/踏步宽/平台深度均合规）
        ///   5. 无输入错误（!HasInputError，即所有数值 TextBox 均可解析）
        /// </summary>
        public RelayCommand GenerateCommand
        {
            get;
        }

        // =============================================================
        //  当前规范（私有）
        // =============================================================
        /// <summary>当前生效的规范参数集，由 BuildingTypeIndex 切换时从 StairCodeLibrary 查询。</summary>
        private StairCodeParams _currentRule;

        // =============================================================
        //  对外只读属性（供 StairGlobalEventHandler 读取）
        // =============================================================

        /// <summary>当前选中的底部标高对象；索引越界时返回 null。</summary>
        public Level SelectedBaseLevel =>
            (BaseLevelIndex >= 0 && BaseLevelIndex < Levels.Count)
                ? Levels[BaseLevelIndex] : null;

        /// <summary>当前选中的顶部标高对象；索引越界时返回 null。</summary>
        public Level SelectedTopLevel =>
            (TopLevelIndex >= 0 && TopLevelIndex < Levels.Count)
                ? Levels[TopLevelIndex] : null;

        /// <summary>当前选中的楼梯族类型名称；索引越界时返回空字符串。</summary>
        public string SelectedStairsTypeName =>
            (StairsTypeIndex >= 0 && StairsTypeIndex < StairsTypeNames.Count)
                ? StairsTypeNames[StairsTypeIndex] : "";

        /// <summary>当前规范参数（供 Handler 或单元测试访问）。</summary>
        public StairCodeParams CurrentRule => _currentRule;

        /// <summary>最新一次 Recalculate 的踏步解算结果（供 Handler 使用）。</summary>
        public StairCalculationResult CalcResult => _calcResult;

        /// <summary>底部到顶部的净高差（mm），由 LevelInfoRefresh 计算后缓存。</summary>
        public double TotalHeightMm => totalMm;

        // hasGeometryViolation：Phase-1 几何参数违规标志，
        // 在 Recalculate 中更新，参与 GenerateCommand.CanExecute 判断
        bool hasGeometryViolation;

        // =============================================================
        //  构造函数
        // =============================================================
        /// <summary>
        /// 初始化 ViewModel，注入外部事件和处理器，并设置默认规范（住宅）。
        /// </summary>
        /// <param name="externalEvent">由 CommandStairGenerator 创建的 ExternalEvent</param>
        /// <param name="handler">与 externalEvent 关联的处理器</param>
        public ViewModel(ExternalEvent externalEvent, StairGlobalEventHandler handler)
        {
            _externalEvent = externalEvent
                ?? throw new ArgumentNullException(nameof(externalEvent));
            _handler = handler
                ?? throw new ArgumentNullException(nameof(handler));

            // 初始规范：住宅（与 XAML ComboBox 默认选项对应）
            _currentRule = StairCodeLibrary.Rules[Model.BuildingType.Residential];

            // GenerateCommand 的 CanExecute 综合五个条件（见属性注释）
            GenerateCommand = new RelayCommand(
                execute:    OnGenerate,
                canExecute: () => P1 != null && P2 != null && !HasViolation && !hasGeometryViolation && !HasInputError
            );
        }

        // =============================================================
        //  公共方法：注入 Revit 项目数据（由 Main.cs 在初始化阶段调用）
        // =============================================================

        /// <summary>
        /// 注入标高列表，填充 LevelDisplayNames 并初始化选中索引。
        /// BaseLevelIndex=0，TopLevelIndex=1（项目至少两个标高的常规假设）。
        /// </summary>
        public void LoadLevels(IEnumerable<Level> levels)
        {
            Levels.AddRange(levels);
            LevelDisplayNames.Clear();
            foreach (var lv in Levels)
                LevelDisplayNames.Add(RevitLevelTools.FormatLevelDisplay(lv));

            BaseLevelIndex = Levels.Count > 0 ? 0  : -1;
            TopLevelIndex  = Levels.Count > 1 ? 1  : -1;
            LevelInfoRefresh(); // 初始化总高显示
        }

        /// <summary>注入楼梯族类型名称列表，填充 StairsTypeNames 并默认选第一项。</summary>
        public void LoadStairsTypes(IEnumerable<string> names)
        {
            StairsTypeNames.Clear();
            foreach (var n in names)
                StairsTypeNames.Add(n);
            StairsTypeIndex = StairsTypeNames.Any() ? 0 : -1;
        }

        /// <summary>
        /// 注入栏杆族类型名称列表。
        /// 若项目中无栏杆族，添加提示条目（不影响功能，生成时 targetType 为 null 则保留默认）。
        /// </summary>
        public void LoadRailingTypes(IEnumerable<string> names)
        {
            RailingTypeNames.Clear();
            foreach (var n in names)
                RailingTypeNames.Add(n);
            if (!RailingTypeNames.Any())
                RailingTypeNames.Add("（项目中暂无栏杆族）");
            RailingTypeIndex = 0;
        }

        /// <summary>总高为负或为零时返回 true，供 XAML DataTrigger 控制警告颜色。</summary>
        public bool TotalHeightIsWarning => totalMm <= 0;

        /// <summary>
        /// 刷新总高显示和 HasViolation 基础状态。
        /// 在 Recalculate() 开头调用（Phase-1 入口）。
        /// 若标高无效或总高非正，清除预览并早返回。
        /// </summary>
        public void LevelInfoRefresh()
        {
            if (_currentRule == null)
                return;

            Level baseLv = SelectedBaseLevel;
            Level topLv  = SelectedTopLevel;

            if (baseLv == null || topLv == null)
            {
                TotalHeightDisplay = "— mm";
                ClearPreview();
                HasViolation  = false;
                ViolationDetail = "";
                GenerateCommand.RaiseCanExecuteChanged();
                return;
            }

            // 计算净高差（扣除底部偏移）
            totalMm = RevitLevelTools.GetHeightDifferenceMm(baseLv, topLv, BaseOffsetMm);

            if (totalMm <= 0)
            {
                // 顶部标高不高于底部标高+偏移，显示警告并阻止解算
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
        /// 核心解算与校验，分两个阶段执行。
        ///
        /// ── Phase 1（始终执行，无论 P1/P2 是否已拾取）──────────────────
        ///   1. 调用 LevelInfoRefresh() 验证标高有效性、更新总高显示。
        ///   2. 校验梯段净宽、踏步宽、平台深度是否满足规范下限（几何参数合规性）。
        ///   3. 汇总 hasGeometryViolation，影响"生成"按钮 CanExecute。
        ///
        /// ── Phase 2（仅当 P1/P2 均已拾取且 Phase-1 无几何违规时执行）────
        ///   4. 由 P1P2 水平距离反算踏步数（totalSteps）。
        ///   5. 调用 StairCalculator.Calculate 解算踢面高（calcResult）。
        ///   6. 校验三项 Phase-2 规范指标：
        ///        a. 总级数在 4 ~ 36 范围内
        ///        b. 实际踏步宽 ≥ MinTreadDepth
        ///        c. 踢面高 ≤ MaxRiserHeight
        ///   7. 通过 ViolationCollector 汇总所有违规消息，更新 ViolationDetail。
        ///
        /// ── 违规收集 ─────────────────────────────────────────────────
        /// 改用 ViolationCollector 值对象替代原先的 List&lt;string&gt; violations，
        /// 统一消息拼接格式，消除多个 return 路径上格式不一致的风险。
        /// </summary>
        private void Recalculate()
        {
            // ── Phase 1：标高与几何参数合规性校验 ──────────────────────
            LevelInfoRefresh();

            RunWidthOk   = RunWidthMm    >= _currentRule.MinRunWidth;
            TreadDepthOk = TreadDepthMm  >= _currentRule.MinTreadDepth;
            // 平台深度须同时满足规范下限和不小于梯段净宽
            double landingMin = Math.Max(_currentRule.MinLandingDepth, RunWidthMm);
            LandingDepthOk    = LandingDepthMm >= landingMin;

            // 通知所有依赖 Phase-1 结果的属性刷新
            OnPropertyChanged(nameof(LandingDepthHint));
            OnPropertyChanged(nameof(TotalHeightIsWarning));
            OnPropertyChanged(nameof(RunWidthBadgeText));
            OnPropertyChanged(nameof(TreadDepthBadgeText));

            // 使用 ViolationCollector 替代原先的 List<string> violations
            var collector = new ViolationCollector();

            // Phase-1 几何违规汇总（不进 collector，由 hasGeometryViolation 单独控制按钮）
            hasGeometryViolation = !RunWidthOk || !TreadDepthOk || !LandingDepthOk || TotalHeightIsWarning;

            // ── Phase 2：踏步解算与规范校验（需 P1/P2 已拾取且无几何违规）──
            if (P1 != null && P2 != null && !hasGeometryViolation)
            {
                // P1P2 距离（mm）：英尺转 mm 后参与踏步数反算
                double p1p2Mm = UnitConverter.FtToMm(Math.Sqrt(
                    Math.Pow(P2.X - P1.X, 2) + Math.Pow(P2.Y - P1.Y, 2)));

                // 反算总踏步数：(P1P2距离 - 平台深度) / 踏步宽 × 2（两跑）
                int totalSteps = TreadDepthMm > 0
                    ? (int)Math.Ceiling((p1p2Mm - LandingDepthMm) * 2 / TreadDepthMm)
                    : 0;

                // P1P2 距离过短（不足平台深度）时，无法布置任何踏步，早返回
                if (p1p2Mm <= LandingDepthMm)
                {
                    collector.Add(
                        $"P1P2 距离 {p1p2Mm:F0} mm 须大于休息平台深度 {LandingDepthMm:F0} mm");
                    ClearPreview();
                    HasViolation  = collector.HasViolation;
                    ViolationDetail = collector.Detail;
                    GenerateCommand.RaiseCanExecuteChanged();
                    return;
                }

                // 强制为偶数（保证两跑步数对称，行业惯例）
                if (totalSteps % 2 != 0)
                    totalSteps -= 1;

                // 计算实际踏步宽（几何反算值，可能与用户期望值有差异）
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

                // 调用踏步解算器（指定步数 + 规范 → 踢面高 + 分跑方案）
                _calcResult = StairCalculator.Calculate(totalMm, totalSteps, _currentRule);

                // 通知预览区所有属性刷新
                OnPropertyChanged(nameof(PreviewSteps));
                OnPropertyChanged(nameof(PreviewRiser));
                OnPropertyChanged(nameof(PreviewDist));
                OnPropertyChanged(nameof(PreviewRule));

                // ── Phase-2 规范校验（将违规信息登记到 collector）────────

                // 校验总级数范围（4 ~ 36，含首尾踢面补偿后）
                TotalStepsOk = totalSteps + 2 >= 4 && totalSteps + 2 <= 36;
                if (!TotalStepsOk)
                    collector.Add($"总踏步级数 {totalSteps + 2} 级不在规范范围（4 ~ 36 级）内");

                // 校验实际踏步宽（几何反算值）是否满足规范下限
                ActualTreadOk = _actualTreadDepthMm.HasValue
                    && _actualTreadDepthMm.Value >= _currentRule.MinTreadDepth;
                if (!ActualTreadOk)
                    collector.Add(
                        $"实际踏步宽 {(_actualTreadDepthMm.HasValue ? $"{_actualTreadDepthMm.Value:F1}" : "—")} mm"
                        + $" 低于规范下限 {_currentRule.MinTreadDepth} mm");

                // 校验解算踢面高是否在规范上限以内
                RiserHeightOk = _calcResult.RiserHeight <= _currentRule.MaxRiserHeight;
                if (!RiserHeightOk)
                    collector.Add(
                        $"解算踢面高 {_calcResult.RiserHeight:F1} mm 超过上限"
                        + $"（{_currentRule.MaxRiserHeight} mm）");
            }
            else
            {
                // P1/P2 未拾取或有几何违规时，清空预览，不显示 Phase-2 结果
                ClearPreview();
            }

            // ── 汇总违规状态（Phase-1 + Phase-2 合并到 HasViolation）──────
            HasViolation  = collector.HasViolation || !TotalStepsOk || !ActualTreadOk || !RiserHeightOk;
            ViolationDetail = HasViolation ? collector.Detail : "";
            GenerateCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 清空踏步解算结果和所有 Phase-2 合规标志，回到初始预览状态。
        /// 在 P1/P2 未拾取、距离不足、或有几何违规时调用。
        /// </summary>
        private void ClearPreview()
        {
            _calcResult         = null;
            _actualTreadDepthMm = null;
            TotalStepsOk  = true;
            ActualTreadOk = true;
            RiserHeightOk = true;
            // 批量通知预览区属性更新（显示"—"占位符）
            OnPropertyChanged(nameof(PreviewSteps));
            OnPropertyChanged(nameof(PreviewRiser));
            OnPropertyChanged(nameof(PreviewDist));
            OnPropertyChanged(nameof(PreviewRule));
            OnPropertyChanged(nameof(PreviewActualTread));
        }

        /// <summary>
        /// "生成"按钮触发：将当前 ViewModel 引用写入 Handler，
        /// 然后通过 ExternalEvent.Raise() 异步提交生成任务。
        /// Raise() 返回后，Revit 将在下一个空闲帧调用 Handler.Execute()。
        /// </summary>
        private void OnGenerate()
        {
            _handler.ViewModel = this; // Handler 通过此引用读取所有参数
            _externalEvent.Raise();
        }

        /// <summary>
        /// 将角度转换为简略罗盘方位词，辅助用户判断楼梯朝向是否正确。
        /// 角度以正东为 0°，逆时针增大（与 Math.Atan2 约定一致）。
        /// </summary>
        /// <param name="deg">方位角度（0° ~ 360°）</param>
        /// <returns>八方位中的最近方向文字，格式如"（朝东）"</returns>
        private static string GetCompassDirection(double deg)
        {
            if (deg > 337.5 || deg <= 22.5)  return "（朝东）";
            if (deg <= 67.5)                  return "（东北）";
            if (deg <= 112.5)                 return "（朝北）";
            if (deg <= 157.5)                 return "（西北）";
            if (deg <= 202.5)                 return "（朝西）";
            if (deg <= 247.5)                 return "（西南）";
            if (deg <= 292.5)                 return "（朝南）";
            return "（东南）";
        }
    }
}
