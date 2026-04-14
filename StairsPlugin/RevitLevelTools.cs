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
    [Transaction(TransactionMode.Manual)]
    public class CommandShowLevel : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc=commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                List<Level> levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l=>l.Elevation)
                    .ToList();

                if (levels.Count == 0)
                {
                    TaskDialog.Show("提示", "当前项目中没有标高。");
                    return Result.Succeeded;
                }

                StringBuilder sb=new StringBuilder();

                foreach (Level level in levels)
                {
                    double elevationFeet = level.Elevation;
                    double elevationMeter = UnitUtils.ConvertFromInternalUnits(elevationFeet, UnitTypeId.Meters);

                    string sign = "";
                    if (Math.Abs(elevationMeter) < 0.0001)
                    {
                        sign = "±";
                    }
                    else if (elevationMeter > 0)
                    {
                        sign = "+";
                    }
                    string formattedElev = sign + elevationMeter.ToString("0.000");

                    sb.AppendLine($"{level.Name}--{formattedElev}");
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
