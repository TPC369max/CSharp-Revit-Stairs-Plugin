using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin.Model
{
    /// <summary>
    /// 楼梯踏步解算结果值对象。
    ///
    /// 由 <see cref="StairCalculator.Calculate"/> 计算后返回，
    /// 供 ViewModel 缓存（CalcResult）并在生成事件中传递给
    /// <see cref="StairGlobalEventHandler"/>，用于设定 Revit 楼梯参数。
    /// </summary>
    public class StairCalculationResult
    {
        /// <summary>
        /// 总踏步数（不含两端踢面补偿）。
        /// 注意：Revit 中实际写入的踢面数为 TotalSteps + 2，
        /// 因为楼梯首尾各额外计入一个踢面（起步和落步）。
        /// </summary>
        public int TotalSteps
        {
            get; set;
        }

        /// <summary>
        /// 实际踢面高（mm）。
        /// 由总高度除以踢面数计算得到（保留 1 位小数），
        /// 未经四舍五入对齐，ViewModel 以此值与规范上限比较合规性。
        /// </summary>
        public double RiserHeight
        {
            get; set;
        }

        /// <summary>
        /// 第一跑踏步数。
        /// 奇数总步数时第一跑多一步（行业惯例：先多后少）。
        /// 用于在 StairGlobalEventHandler 中计算 run1 的长度和平台高程。
        /// </summary>
        public int Run1Steps
        {
            get; set;
        }

        /// <summary>
        /// 第二跑踏步数（= TotalSteps - Run1Steps）。
        /// </summary>
        public int Run2Steps
        {
            get; set;
        }

        /// <summary>
        /// 解算结果是否满足规范约束。
        /// false 表示踢面高超过上限或某跑级数超过 MaxStepsPerRun，
        /// ViewModel 据此显示违规 Badge，并阻止"生成"按钮激活。
        /// </summary>
        public bool IsValid
        {
            get; set;
        }
    }

    /// <summary>
    /// 楼梯踏步数与踢面高解算器（纯静态，无状态）。
    ///
    /// 提供两个重载：
    ///   1. <see cref="Calculate(double, int, StairCodeParams)"/>
    ///      — 由调用方指定总踏步数，仅计算踢面高与分跑方案。
    ///      — 用于 ViewModel 从 P1P2 距离反算踏步数后驱动解算。
    ///
    ///   2. <see cref="Calculate(double, StairCodeParams)"/>
    ///      — 由规范上限自动推算最少踏步数，再计算踢面高。
    ///      — 可用于"自动模式"或单元测试。
    ///
    /// 两个重载均按"奇数时第一跑多一步"的行业惯例分配分跑步数。
    /// </summary>
    public static class StairCalculator
    {
        /// <summary>
        /// 在已知总踏步数的前提下解算楼梯参数（ViewModel 驱动的主路径）。
        ///
        /// ── 算法 ─────────────────────────────────────────────────────
        /// 踢面高 = totalHeightMm / (totalSteps + 2)
        ///   加 2 的原因：Revit 楼梯首尾各计入一个额外踢面，
        ///   因此实际踢面数 = 用户感知踏步数 + 2。
        ///
        /// 分跑规则（行业惯例）：
        ///   偶数步 → 两跑等分（run1 = run2 = totalSteps/2）
        ///   奇数步 → 第一跑多一步（run1 = (totalSteps+1)/2，run2 = totalSteps/2）
        ///
        /// ── 合规校验 ─────────────────────────────────────────────────
        /// IsValid 仅校验踢面高是否在规范上限以内；
        /// 调用方（ViewModel）负责单独校验级数范围（4~36级）。
        /// </summary>
        /// <param name="totalHeightMm">底部标高到顶部标高的净高差（mm）</param>
        /// <param name="totalSteps">由 P1P2 距离反算出的总踏步数</param>
        /// <param name="rule">当前建筑类型对应的规范参数</param>
        /// <returns>解算结果，<see cref="StairCalculationResult.IsValid"/> 表示合规性</returns>
        public static StairCalculationResult Calculate(
            double totalHeightMm, int totalSteps, StairCodeParams rule)
        {
            // 踏步数为零或负时直接返回无效结果，避免除零
            if (totalSteps <= 0)
                return new StairCalculationResult { IsValid = false };

            double riserHeight = totalHeightMm / (totalSteps + 2);
            // 奇数步时第一跑多一步（行业惯例）
            int run1 = (totalSteps % 2 == 0)
                ? totalSteps / 2
                : (totalSteps + 1) / 2;
            int run2 = totalSteps - run1;

            return new StairCalculationResult
            {
                TotalSteps = totalSteps,
                RiserHeight = riserHeight,
                Run1Steps = run1,
                Run2Steps = run2,
                // 仅校验踢面高上限；级数范围校验由 ViewModel 负责
                IsValid = riserHeight <= rule.MaxRiserHeight
            };
        }

        /// <summary>
        /// 由规范自动推算最少踏步数，再解算楼梯参数（自动模式/单元测试）。
        ///
        /// ── 算法 ─────────────────────────────────────────────────────
        /// 最少踏步数 = ⌈totalHeightMm / MaxRiserHeight⌉，保证不超高。
        /// 若推算结果 < 4，强制取 4（规范下限）。
        ///
        /// 踢面高 = totalHeightMm / steps（均分，不含 Revit 首尾补偿，
        ///          与重载1不同，注意语义差异）。
        ///
        /// ── IsValid 综合校验 ─────────────────────────────────────────
        /// 同时检查踢面高上限和单跑最大级数，两者均满足才为 true。
        /// </summary>
        /// <param name="totalHeightMm">底部到顶部净高差（mm）</param>
        /// <param name="rule">规范参数，为 null 且同时 totalHeightMm≤0 时返回无效结果</param>
        /// <returns>解算结果</returns>
        public static StairCalculationResult Calculate(double totalHeightMm, StairCodeParams rule)
        {
            // 参数保护：规范对象为 null 且高差同时非正时，返回无效结果。
            // 注意：条件为 &&（两者同时满足才提前返回）：
            //   · 若 rule != null 但 totalHeightMm <= 0，后续 Math.Ceiling 会返回 0，
            //     steps 被修正为 4，IsValid=false 足以兜底，此处不提前中止。
            //   · 若 rule == null 但 totalHeightMm > 0，后续访问 rule.MaxRiserHeight
            //     会抛出 NullReferenceException，调用方须自行保证 rule 不为 null。
            if (rule == null && totalHeightMm <= 0)
                return new StairCalculationResult { IsValid = false };

            // 向上取整，确保每级踢面高 ≤ MaxRiserHeight
            int steps = (int)Math.Ceiling(totalHeightMm / rule.MaxRiserHeight);
            // 规范下限：楼梯不少于 4 级
            if (steps < 4) steps = 4;

            double riser = totalHeightMm / steps;

            // 奇数步时第一跑多一步
            int run1 = (int)Math.Ceiling(steps / 2.0);
            int run2 = steps - run1;

            // 同时校验踢面高上限和单跑级数上限
            bool valid = riser <= rule.MaxRiserHeight
                      && run1 <= rule.MaxStepsPerRun
                      && run2 <= rule.MaxStepsPerRun;

            return new StairCalculationResult
            {
                TotalSteps  = steps,
                RiserHeight = Math.Round(riser, 1), // 保留 1 位小数，与界面显示精度一致
                Run1Steps   = run1,
                Run2Steps   = run2,
                IsValid     = valid
            };
        }
    }
}
