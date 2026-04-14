using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin
{
    internal class Main : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var win = new StairGeneratorWindow(commandData.Application.ActiveUIDocument);
            if (win.ShowDialog() != true)
                return Result.Cancelled;

            // 从窗口读取所有参数
            Level baseLevel = win.SelectedBaseLevel;
            Level topLevel = win.SelectedTopLevel;
            XYZ insertionPoint = win.PickedP1;
            double angleRad = win.DirectionAngleRad;
            bool clockwise = win.IsClockwise;
            double runWidthFt = UnitUtils.ConvertToInternalUnits(win.RunWidthMm, UnitTypeId.Millimeters);
            // ... 传给 StairsEditScope 生成逻辑
            return Result.Succeeded;
        }
    }
}
