using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin
{
    [Transaction(TransactionMode.Manual)]
    public class FixedParaStairs : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

            Level level1 = new FilteredElementCollector(doc).OfClass(typeof(Level))
                                .Cast<Level>().FirstOrDefault(l => l.Name.Equals("标高 1"));
            Level level2 = new FilteredElementCollector(doc).OfClass(typeof(Level))
                                .Cast<Level>().FirstOrDefault(l => l.Name.Equals("标高 2"));

            if (level1 == null || level2 == null)
                return Result.Failed;

            double treadDepth = UnitUtils.ConvertToInternalUnits(0.25, UnitTypeId.Meters); // 250mm
            double runWidth = UnitUtils.ConvertToInternalUnits(1.2, UnitTypeId.Meters);  // 1.2m
            double gap = UnitUtils.ConvertToInternalUnits(0.2, UnitTypeId.Meters);  // 0.2m

            // 计算高度
            double totalHeight = level2.Elevation - level1.Elevation; // 楼梯总高
            double run1Height = totalHeight / 2.0;                   // 第一段(12个踢面)的高度

            double runLineLength = 11 * treadDepth;

            XYZ run1Start = new XYZ(0, 0, level1.Elevation);
            XYZ run1End = new XYZ(0, runLineLength, level1.Elevation);

            XYZ run2Start = new XYZ(runWidth + gap, runLineLength, level1.Elevation);
            XYZ run2End = new XYZ(runWidth + gap, 0, level1.Elevation);

            ElementId stairsId = ElementId.InvalidElementId;
            ElementId run1Id = ElementId.InvalidElementId;
            using (StairsEditScope stairsScope = new StairsEditScope(doc, "创建楼梯"))
            {
                stairsId = stairsScope.Start(level1.Id, level2.Id);
                Stairs stairs = doc.GetElement(stairsId) as Stairs;

                using (Transaction tx = new Transaction(doc, "绘制梯段与平台"))
                {
                    tx.Start();

                    stairs.get_Parameter(BuiltInParameter.STAIRS_DESIRED_NUMBER_OF_RISERS).Set(24);
                    stairs.get_Parameter(BuiltInParameter.STAIRS_ACTUAL_TREAD_DEPTH).Set(treadDepth);

                    StairsRun run1 = StairsRun.CreateStraightRun(doc, stairsId,
                        Line.CreateBound(run1Start, run1End),
                        StairsRunJustification.Center);
                    run1.ActualRunWidth = runWidth;

                    run1Id = run1.Id;

                    StairsRun run2 = StairsRun.CreateStraightRun(doc, stairsId,
                        Line.CreateBound(run2Start, run2End),
                        StairsRunJustification.Center);
                    run2.ActualRunWidth = runWidth;

                    // 核心修复：顺序至关重要（先设顶，后设底）
                    run2.get_Parameter(BuiltInParameter.STAIRS_RUN_TOP_ELEVATION).Set(totalHeight);
                    run2.get_Parameter(BuiltInParameter.STAIRS_RUN_BOTTOM_ELEVATION).Set(run1Height);

                    doc.Regenerate();

                    StairsLanding.CreateAutomaticLanding(doc, run1.Id, run2.Id);

                    tx.Commit();
                }

                stairsScope.Commit(new StairsFailurePreprocessor());
            }
            // 在事务外获取新创建的楼梯和梯段元素
            Stairs newStairs = doc.GetElement(stairsId) as Stairs;
            StairsRun finalRun = doc.GetElement(run1Id) as StairsRun; // 用于获取跑道宽度

            // 显示楼梯信息：ID、起始/终止标高、踏步数、踏步高度、总高度、跑道宽度
            string info = string.Format("楼梯 ID: {0}\n起始标高: {1}, 终止标高: {2}\n踏步数: {3}\n踏步高度: {4:F3} 米\n总高度: {5:F3} 米\n跑道宽度: {6:F3} 米",
                newStairs.Id.IntegerValue,
                level1.Name, level2.Name,
                newStairs.ActualRisersNumber,
                UnitUtils.ConvertFromInternalUnits(newStairs.ActualRiserHeight, UnitTypeId.Meters),
                UnitUtils.ConvertFromInternalUnits(newStairs.Height, UnitTypeId.Meters),
                UnitUtils.ConvertFromInternalUnits(finalRun.ActualRunWidth, UnitTypeId.Meters));

            TaskDialog.Show("楼梯信息", info);
            return Result.Succeeded;
        }
    }

    public class StairsFailurePreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            failuresAccessor.DeleteAllWarnings();
            return FailureProcessingResult.Continue;
        }
    }
}

