using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin.Model
{
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
                    MinTreadDepth = 260,
                    MaxRiserHeight = 175,
                    MinRunWidth = 1000,
                    MinLandingDepth = 1200,
                    MinClearHeight = 2200,
                    MaxStepsPerRun = 18,
                    RuleSource = "GB55038-2025 §4.2.2 / GB55031-2022 表5.3.9 第2行"
                },
                [BuildingType.Public] = new StairCodeParams
                {
                    MinTreadDepth = 260,
                    MaxRiserHeight = 165,
                    MinRunWidth = 1100,
                    MinLandingDepth = 1200,
                    MinClearHeight = 2200,
                    MaxStepsPerRun = 18,
                    RuleSource = "GB55031-2022 表5.3.9 第1行 / GB55037-2022 §7.1.4 第3款"
                },
                [BuildingType.Attached] = new StairCodeParams
                {
                    MinTreadDepth = 260,
                    MaxRiserHeight = 175,
                    MinRunWidth = 1100,
                    MinLandingDepth = 1200,
                    MinClearHeight = 2200,
                    MaxStepsPerRun = 18,
                    RuleSource = "GB55031-2022 表5.3.9 第2行"
                },
                [BuildingType.Supertall] = new StairCodeParams
                {
                    MinTreadDepth = 250,
                    MaxRiserHeight = 180,
                    MinRunWidth = 1100,
                    MinLandingDepth = 1200,
                    MinClearHeight = 2200,
                    MaxStepsPerRun = 18,
                    RuleSource = "GB55031-2022 表5.3.9 第3行"
                }
            };
    }
}
