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
    public class StairsFailurePreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            failuresAccessor.DeleteAllWarnings();
            return FailureProcessingResult.Continue;
        }
    }

    public class RevitLevelTools
    {
        public static List<Level> GetLevels(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l=>l.Elevation)
                .ToList();
        }

        public static string FormatLevelDisplay(Level level) {
            double elevMm = UnitUtils.ConvertFromInternalUnits(
                level.Elevation, UnitTypeId.Millimeters);

            string sign;
            if (Math.Abs(elevMm) < 0.5)        // 视为 ±0
                sign = "±";
            else if (elevMm > 0)
                sign = "+";
            else
                sign = "";                      // 负数自带负号

            return $"{level.Name}  {sign}{elevMm:F0} mm";
        }

        public static double GetHeightDifferenceMm(Level baseLevel, Level topLevel,double BaseOffset)
        {
            double BaseOffsetFt = UnitUtils.ConvertToInternalUnits(
                BaseOffset, UnitTypeId.Millimeters);
            double diffFt = topLevel.Elevation - baseLevel.Elevation- BaseOffsetFt;
            return UnitUtils.ConvertFromInternalUnits(diffFt, UnitTypeId.Millimeters);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class CommandShowLevel : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc=commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                List<Level> levels = RevitLevelTools.GetLevels(doc);

                if (levels.Count == 0)
                {
                    TaskDialog.Show("提示", "当前项目中没有标高。");
                    return Result.Succeeded;
                }

                StringBuilder sb=new StringBuilder();

                foreach (Level level in levels)
                {
                    sb.AppendLine(RevitLevelTools.FormatLevelDisplay(level));
                }

                TaskDialog mainDialog = new TaskDialog("标高列表");
                mainDialog.MainInstruction = "项目标高汇总（从小到大）";
                mainDialog.MainContent = sb.ToString();
                mainDialog.CommonButtons= TaskDialogCommonButtons.Ok; 
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
