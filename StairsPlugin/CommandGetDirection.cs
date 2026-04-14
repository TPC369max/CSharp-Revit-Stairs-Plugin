using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin
{
    [Transaction(TransactionMode.Manual)]
    internal class CommandGetDirection : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                TaskDialog.Show("提示", "请拾取起点 P1");
                XYZ p1 = uiDoc.Selection.PickPoint("请选择起点 (P1)");

                TaskDialog.Show("提示", "请拾取起点 P2");
                XYZ p2 = uiDoc.Selection.PickPoint("请选择起点 (P2)");

                double dx = p2.X - p1.X;
                double dy = p2.Y - p1.Y;

                double angleRad = Math.Atan2(dy, dx);

                double angleDeg = angleRad * (180.0 / Math.PI);
                if (angleDeg < 0)
                    angleDeg += 360;
                double p1X_m = UnitUtils.ConvertFromInternalUnits(p1.X, UnitTypeId.Meters);
                double p1Y_m = UnitUtils.ConvertFromInternalUnits(p1.Y, UnitTypeId.Meters);
                double p2X_m = UnitUtils.ConvertFromInternalUnits(p2.X, UnitTypeId.Meters);
                double p2Y_m = UnitUtils.ConvertFromInternalUnits(p2.Y, UnitTypeId.Meters);

                string info = $"P1 坐标: ({p1X_m:F3}, {p1Y_m:F3})\n" +
                              $"P2 坐标: ({p2X_m:F3}, {p2Y_m:F3})\n" +
                              "------------------------------\n" +
                              $"向量方向: ({dx:F3}, {dy:F3})\n" +
                              $"平面朝向 θ = {angleDeg:F1}°\n";
                string compass = GetCompassDirection(angleDeg);
                info += $"大致方位: {compass}";

                TaskDialog td = new TaskDialog("两点向量法定位");
                td.MainInstruction = $"朝向角度: {angleDeg:F1}°";
                td.MainContent = info;
                td.FooterText = "注：0° 为正东方向 (+X)，逆时针旋转";
                td.Show();

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        string GetCompassDirection(double degrees)
        {
            if (degrees > 337.5 || degrees <= 22.5)
                return "正东 (East)";
            if (degrees > 22.5 && degrees <= 67.5)
                return "东北 (North-East)";
            if (degrees > 67.5 && degrees <= 112.5)
                return "正北 (North)";
            if (degrees > 112.5 && degrees <= 157.5)
                return "西北 (North-West)";
            if (degrees > 157.5 && degrees <= 202.5)
                return "正西 (West)";
            if (degrees > 202.5 && degrees <= 247.5)
                return "西南 (South-West)";
            if (degrees > 247.5 && degrees <= 292.5)
                return "正南 (South)";
            if (degrees > 292.5 && degrees <= 337.5)
                return "东南 (South-East)";
            return "未知";
        }
    }
}
