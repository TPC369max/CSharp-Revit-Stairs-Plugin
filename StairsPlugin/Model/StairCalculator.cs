using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin.Model
{
    public class StairCalculationResult
    {
        public int TotalSteps
        {
            get; set;
        }  // 总踏步数
        public double RiserHeight
        {
            get; set;
        }  // 实际踢面高（mm）
        public int Run1Steps
        {
            get; set;
        }  // 第一跑步数
        public int Run2Steps
        {
            get; set;
        }  // 第二跑步数
        public bool IsValid
        {
            get; set;
        }  // 是否满足规范
    }

    public static class StairCalculator
    {
        public static StairCalculationResult Calculate(double totalHeightMm, StairCodeParams rule)
        {
            if(rule == null&&totalHeightMm<=0) 
                return new StairCalculationResult { IsValid=false};

            int steps =(int) Math.Ceiling(totalHeightMm / rule.MaxRiserHeight);
            if (steps < 4 ) steps = 4;

            double riser = totalHeightMm / steps;

            int run1=(int) Math.Ceiling(steps /2.0);
            int run2=steps - run1;

            bool valid = riser <= rule.MaxRiserHeight
                      && run1 <= rule.MaxStepsPerRun
                      && run2 <= rule.MaxStepsPerRun;

            return new StairCalculationResult
            {
                TotalSteps = steps,
                RiserHeight = Math.Round(riser,1),
                Run1Steps = run1,
                Run2Steps = run2,
                IsValid = valid
            };

        }
    }
}
