using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin
{
    /// <summary>
    /// Revit 失败预处理器：静默忽略所有警告，允许事务正常提交。
    ///
    /// 在 <see cref="StairsEditScope.Commit"/> 时作为参数传入，
    /// 以避免 Revit 弹出"模型存在警告"对话框中断楼梯生成流程。
    ///
    /// 注意：此处仅删除警告（Warning 级别），不处理错误（Error 级别）；
    /// 若事务产生错误，Revit 仍会回滚并抛出异常。
    /// </summary>
    public class StairsFailurePreprocessor : IFailuresPreprocessor
    {
        /// <summary>
        /// 在事务提交前由 Revit 调用，用于批量处理失败/警告信息。
        /// 此处统一删除所有警告后返回 <see cref="FailureProcessingResult.Continue"/>，
        /// 告知 Revit 继续提交事务。
        /// </summary>
        /// <param name="failuresAccessor">失败信息访问器，可读取和处理当前事务的所有失败项</param>
        /// <returns>Continue 表示已处理完毕，事务可继续提交</returns>
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            // 删除所有警告；错误级别失败不受此影响，Revit 仍会回滚
            failuresAccessor.DeleteAllWarnings();
            return FailureProcessingResult.Continue;
        }
    }

    /// <summary>
    /// Revit 标高相关工具方法集合（纯静态工具类）。
    ///
    /// 职责：
    ///   • 从文档收集并按高程排序的标高列表
    ///   • 格式化标高显示字符串（带 ±/+/- 符号）
    ///   • 计算两个标高之间考虑底部偏移后的净高差
    ///
    /// 所有方法均为无副作用的纯函数，可在任意位置安全调用。
    /// </summary>
    public class RevitLevelTools
    {
        /// <summary>
        /// 从 Revit 文档中收集所有标高，并按高程从低到高排序后返回。
        ///
        /// 使用 <see cref="FilteredElementCollector"/> 过滤 Level 类，
        /// 性能优于遍历所有元素后再筛选。
        /// </summary>
        /// <param name="doc">当前 Revit 文档</param>
        /// <returns>按高程升序排列的标高列表；文档中无标高时返回空列表</returns>
        public static List<Level> GetLevels(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();
        }

        /// <summary>
        /// 格式化标高的界面显示字符串，附带 ±/+/- 高程符号。
        ///
        /// ── 符号规则 ─────────────────────────────────────────────────
        ///   高程绝对值 &lt; 0.5 mm → 显示 "±"（视为建筑零点）
        ///   高程 &gt; 0              → 显示 "+"
        ///   高程 &lt; 0              → 不加前缀（负号由数值本身携带）
        ///
        /// ── 示例输出 ─────────────────────────────────────────────────
        ///   "F1  ±0 mm"
        ///   "F2  +3000 mm"
        ///   "B1  -3600 mm"
        /// </summary>
        /// <param name="level">要格式化的标高对象</param>
        /// <returns>
        ///   格式为 "{level.Name}  {符号}{高程 mm}" 的字符串，
        ///   供 ComboBox ItemsSource 展示。
        /// </returns>
        public static string FormatLevelDisplay(Level level)
        {
            // 将 Revit 内部单位（英尺）转换为毫米用于显示
            double elevMm = UnitUtils.ConvertFromInternalUnits(
                level.Elevation, UnitTypeId.Millimeters);

            string sign;
            if (Math.Abs(elevMm) < 0.5)   // 绝对值 < 0.5 mm，视为 ±0.000
                sign = "±";
            else if (elevMm > 0)
                sign = "+";
            else
                sign = "";                 // 负数自带负号，不添加额外前缀

            return $"{level.Name}  {sign}{elevMm:F0} mm";
        }

        /// <summary>
        /// 计算顶部标高与底部标高之间、扣除底部偏移后的净高差（mm）。
        ///
        /// ── 公式 ─────────────────────────────────────────────────────
        ///   净高差 = topLevel.Elevation - baseLevel.Elevation - BaseOffsetFt
        ///
        /// 其中 BaseOffsetFt 为用户输入的 BaseOffsetMm 换算为英尺后的值。
        /// 底部偏移允许为负（表示楼梯起步点低于底部标高面）。
        ///
        /// ── 使用场景 ─────────────────────────────────────────────────
        /// ViewModel.LevelInfoRefresh() 调用此方法计算 totalMm，
        /// 再传入 StairCalculator 进行踏步解算。
        /// 若返回值 ≤ 0，说明标高选择无效（顶低于底），ViewModel 将显示警告。
        /// </summary>
        /// <param name="baseLevel">底部标高</param>
        /// <param name="topLevel">顶部标高</param>
        /// <param name="BaseOffset">底部偏移值（mm），正值表示起步高于底部标高面</param>
        /// <returns>净高差（mm）；值 ≤ 0 表示顶部标高不高于底部标高+偏移</returns>
        public static double GetHeightDifferenceMm(Level baseLevel, Level topLevel, double BaseOffset)
        {
            // 将偏移量从 mm 转换为 Revit 内部单位（英尺）再参与计算
            double BaseOffsetFt = UnitUtils.ConvertToInternalUnits(
                BaseOffset, UnitTypeId.Millimeters);
            double diffFt = topLevel.Elevation - baseLevel.Elevation - BaseOffsetFt;
            return UnitUtils.ConvertFromInternalUnits(diffFt, UnitTypeId.Millimeters);
        }
    }

    /// <summary>
    /// 独立命令：在 TaskDialog 中展示当前文档所有标高的汇总列表。
    ///
    /// 主要用于调试和快速检查项目标高，不参与楼梯生成流程。
    /// 可在 Revit 插件管理器中单独注册为按钮命令。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CommandShowLevel : IExternalCommand
    {
        /// <summary>
        /// 命令入口：收集文档标高，格式化后弹窗显示。
        /// </summary>
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                List<Level> levels = RevitLevelTools.GetLevels(doc);

                if (levels.Count == 0)
                {
                    TaskDialog.Show("提示", "当前项目中没有标高。");
                    return Result.Succeeded;
                }

                StringBuilder sb = new StringBuilder();

                // 逐行追加格式化标高字符串（已按高程升序排列）
                foreach (Level level in levels)
                {
                    sb.AppendLine(RevitLevelTools.FormatLevelDisplay(level));
                }

                TaskDialog mainDialog = new TaskDialog("标高列表");
                mainDialog.MainInstruction = "项目标高汇总（从小到大）";
                mainDialog.MainContent    = sb.ToString();
                mainDialog.CommonButtons  = TaskDialogCommonButtons.Ok;
                mainDialog.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
