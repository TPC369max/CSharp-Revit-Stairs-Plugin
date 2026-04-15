using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin.Model
{
    // =========================================================
    //  规范参数值对象
    // =========================================================
    public class StairCodeParams
    {
        public double MinTreadDepth
        {
            get; set;
        }  // 踏步最小宽度 b（mm）
        public double MaxRiserHeight
        {
            get; set;
        }  // 踏步最大高度 h（mm）
        public double MinRunWidth
        {
            get; set;
        }  // 梯段最小净宽（mm）
        public double MinLandingDepth
        {
            get; set;
        }  // 休息平台最小深度（mm）
        public double MinClearHeight
        {
            get; set;
        }  // 梯段净高（mm）
        public int MaxStepsPerRun
        {
            get; set;
        }  // 每跑最大级数
        public string RuleSource
        {
            get; set;
        }  // 规范条文来源
    }

    // =========================================================
    //  建筑类型枚举
    // =========================================================
    public enum BuildingType
    {
        Residential,  // 住宅
        Public,        // 一般公共建筑
        Attached,      // 附属楼梯（多层/高层）
        Supertall      // 附属楼梯（超高层）
    }
}
