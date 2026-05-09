using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin.Model
{
    // =========================================================
    //  规范规则库（静态只读字典）
    //
    //  职责：
    //    集中存储不同建筑类型对应的楼梯规范参数，
    //    以 BuildingType 枚举为键、StairCodeParams 为值。
    //    作为"规则工厂"供 ViewModel 在切换建筑类型时查询。
    //
    //  扩展方式：
    //    若将来需要新增建筑类型（如"地下车库"、"工业厂房"），
    //    只需在 BuildingType 枚举中增加条目，并在此字典中补充对应条目，
    //    无需修改 ViewModel 或 StairCalculator 的逻辑。
    //
    //  数据来源（截至 2025-05）：
    //    GB55031-2022 《民用建筑通用规范》表5.3.9
    //    GB55038-2025 《住宅建筑规范》§4.2.2
    //    GB55037-2022 《建筑防火通用规范》§7.1.4
    // =========================================================
    public static class StairCodeLibrary
    {
        /// <summary>
        /// 以建筑类型为键的规范参数只读字典。
        ///
        /// 初始化时机：类首次被访问时由静态初始化器构造，线程安全。
        /// 所有参数单位均为毫米（mm），与规范原文一致。
        /// </summary>
        public static readonly Dictionary<BuildingType, StairCodeParams> Rules =
            new Dictionary<BuildingType, StairCodeParams>
            {
                // ── 住宅楼梯 ─────────────────────────────────────────────
                // 依据：GB55038-2025 §4.2.2（踢面高上限 175 mm）
                //        GB55031-2022 表5.3.9 第2行（净高 2200 mm）
                // 梯段净宽 1000 mm 为住宅套内楼梯下限；
                // 公用楼梯通常须满足 GB55037 疏散宽度（另行复核）。
                [BuildingType.Residential] = new StairCodeParams
                {
                    MinTreadDepth   = 260,   // 踏面宽下限 260 mm
                    MaxRiserHeight  = 175,   // 踢面高上限 175 mm
                    MinRunWidth     = 1000,  // 梯段净宽下限 1000 mm
                    MinLandingDepth = 1200,  // 休息平台深度下限 1200 mm
                    MinClearHeight  = 2200,  // 梯段净高下限 2200 mm
                    MaxStepsPerRun  = 18,    // 单跑最多 18 级
                    RuleSource      = "GB55038-2025 §4.2.2 / GB55031-2022 表5.3.9 第2行"
                },

                // ── 一般公共建筑楼梯 ──────────────────────────────────────
                // 依据：GB55031-2022 表5.3.9 第1行（踢面高上限 165 mm，要求更严）
                //        GB55037-2022 §7.1.4 第3款（疏散楼梯净宽 ≥ 1100 mm）
                // 公共建筑人流量大，踢面高上限比住宅低 10 mm（更平缓）。
                [BuildingType.Public] = new StairCodeParams
                {
                    MinTreadDepth   = 260,
                    MaxRiserHeight  = 165,   // 公共建筑踢面高更严：≤ 165 mm
                    MinRunWidth     = 1100,  // 疏散净宽 ≥ 1100 mm
                    MinLandingDepth = 1200,
                    MinClearHeight  = 2200,
                    MaxStepsPerRun  = 18,
                    RuleSource      = "GB55031-2022 表5.3.9 第1行 / GB55037-2022 §7.1.4 第3款"
                },

                // ── 附属楼梯（多层/高层）─────────────────────────────────
                // 依据：GB55031-2022 表5.3.9 第2行
                // 附属楼梯（如设备楼梯、屋面检修梯）踢面高与住宅相同，
                // 但净宽要求与公共建筑对齐（≥ 1100 mm）。
                [BuildingType.Attached] = new StairCodeParams
                {
                    MinTreadDepth   = 260,
                    MaxRiserHeight  = 175,
                    MinRunWidth     = 1100,  // 附属楼梯净宽须 ≥ 1100 mm
                    MinLandingDepth = 1200,
                    MinClearHeight  = 2200,
                    MaxStepsPerRun  = 18,
                    RuleSource      = "GB55031-2022 表5.3.9 第2行"
                },

                // ── 附属楼梯（超高层）────────────────────────────────────
                // 依据：GB55031-2022 表5.3.9 第3行
                // 超高层建筑附属楼梯的踏面宽下限稍宽松（≥ 250 mm），
                // 踢面高上限适当放宽（≤ 180 mm），其余与多层/高层相同。
                [BuildingType.Supertall] = new StairCodeParams
                {
                    MinTreadDepth   = 250,   // 超高层踏面宽下限稍宽松：≥ 250 mm
                    MaxRiserHeight  = 180,   // 超高层踢面高稍宽松：≤ 180 mm
                    MinRunWidth     = 1100,
                    MinLandingDepth = 1200,
                    MinClearHeight  = 2200,
                    MaxStepsPerRun  = 18,
                    RuleSource      = "GB55031-2022 表5.3.9 第3行"
                }
            };
    }
}
