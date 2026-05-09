using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin.Model
{
    // =========================================================
    //  规范参数值对象（Value Object）
    //
    //  设计说明：
    //    本类作为"值对象"使用——不包含业务逻辑，仅承载
    //    某一建筑类型对应的楼梯规范数值。
    //    所有属性均为可设置（set），方便从 StairCodeLibrary
    //    的字典初始化器中一次性赋值。
    //
    //    单位约定：所有长度属性均以毫米（mm）为单位，
    //    与国内规范原文保持一致，不在此处做单位转换。
    //    调用方（ViewModel、StairCalculator）在与 Revit API
    //    交互前再通过 UnitConverter 转换为英尺。
    // =========================================================
    public class StairCodeParams
    {
        /// <summary>
        /// 踏步最小宽度 b（mm）。
        /// 即踏面（tread depth）的水平投影净尺寸，不含踢面厚度。
        /// 规范要求：住宅/公共建筑均 ≥ 260 mm（GB55031-2022 表5.3.9）。
        /// </summary>
        public double MinTreadDepth
        {
            get; set;
        }

        /// <summary>
        /// 踏步最大高度 h（mm）。
        /// 即踢面（riser height）的竖向净高上限。
        /// 规范要求因建筑类型而异：住宅 ≤ 175 mm，公共建筑 ≤ 165 mm。
        /// <see cref="StairCalculator"/> 以此值反算最少踏步数。
        /// </summary>
        public double MaxRiserHeight
        {
            get; set;
        }

        /// <summary>
        /// 梯段最小净宽（mm）。
        /// 指两侧扶手内边缘（或墙内边缘）之间的水平净距，
        /// 不含扶手宽度本身。
        /// ViewModel 将用户输入与此值比较并显示合规 Badge。
        /// </summary>
        public double MinRunWidth
        {
            get; set;
        }

        /// <summary>
        /// 休息平台最小深度（mm）。
        /// 平台深度沿楼梯爬升方向量取，须不小于梯段净宽，
        /// 且不小于本字段值（两者取较大值）。
        /// ViewModel 合规校验逻辑：landingMin = Max(MinLandingDepth, RunWidthMm)。
        /// </summary>
        public double MinLandingDepth
        {
            get; set;
        }

        /// <summary>
        /// 梯段净高最小值（mm）。
        /// 沿踏步鼻端（nosing）铅垂方向量取，至上方遮挡物底面。
        /// 规范统一要求 ≥ 2200 mm（GB55031-2022 §5.3.9）。
        /// 该值目前主要供 ClearanceChecker 用于射线法净空校验的阈值传参。
        /// </summary>
        public double MinClearHeight
        {
            get; set;
        }

        /// <summary>
        /// 单跑最大级数（步数上限）。
        /// 超过此值的连续踏步段须设置休息平台分隔。
        /// 各类建筑均为 18 级（GB55031-2022 §5.3.9 注1）。
        /// <see cref="StairCalculator.Calculate(double, StairCodeParams)"/> 
        /// 在 IsValid 中检查每跑级数是否超限。
        /// </summary>
        public int MaxStepsPerRun
        {
            get; set;
        }

        /// <summary>
        /// 规范条文来源（字符串描述）。
        /// 仅用于界面展示（PreviewRule 绑定），不参与任何计算。
        /// 格式示例："GB55031-2022 表5.3.9 第1行"。
        /// </summary>
        public string RuleSource
        {
            get; set;
        }
    }

    // =========================================================
    //  建筑类型枚举
    //
    //  对应 StairCodeLibrary.Rules 字典的键，
    //  也与 XAML 中"建筑功能类型" ComboBox 的条目顺序一一对应：
    //    索引 0 → Residential（住宅）
    //    索引 1 → Public（一般公共建筑）
    //    索引 2 → Attached（附属楼梯·多层/高层）
    //    索引 3 → Supertall（附属楼梯·超高层）
    //  ViewModel.BuildingTypeIndex 通过强制类型转换直接用作字典键。
    // =========================================================
    public enum BuildingType
    {
        /// <summary>住宅楼梯（含多层、高层住宅）</summary>
        Residential,

        /// <summary>一般公共建筑楼梯（学校、办公、商业等）</summary>
        Public,

        /// <summary>附属楼梯（多层/高层建筑附设的疏散或服务楼梯）</summary>
        Attached,

        /// <summary>附属楼梯（超高层建筑，踢面高限值稍宽松）</summary>
        Supertall
    }
}
