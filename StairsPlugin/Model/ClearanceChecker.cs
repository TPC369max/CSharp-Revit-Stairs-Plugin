using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin.Model
{
    public class ClearanceCheckResult
    {
        public bool IsCompliant
        {
            get; set;
        }
        public double MinClearance
        {
            get; set;
        }  // 最小净空（mm）
        public string WarningMessage
        {
            get; set;
        }
    }

    internal static class ClearanceChecker
    {
        public static ClearanceCheckResult Check(
            XYZ insertionPoint,
            int totalSteps,
            double riserHeightFt,   // Revit 内部单位（英尺）
            double treadDepthFt,
            double angleRad,
            double floorBottomElevFt,
            double minClearHeightMm)
        {
            double minClearMm=double.MaxValue;
            for (int i = 0; i < totalSteps; i++)
            {
                // 第 i 级踏步面的绝对标高（英尺）
                double stepSurfaceElev = insertionPoint.Z + (i + 1) * riserHeightFt;

                // 与上方楼板底部标高做差（转换为 mm）
                double clearFt = floorBottomElevFt - stepSurfaceElev;
                double clearMm = UnitUtils.ConvertFromInternalUnits(clearFt,
                                     UnitTypeId.Millimeters);

                if (clearMm < minClearMm)
                    minClearMm = clearMm;
            }
            bool ok = minClearMm >= minClearHeightMm;
            return new ClearanceCheckResult
            {
                IsCompliant = ok,
                MinClearance = System.Math.Round(minClearMm, 0),
                WarningMessage = ok ? null
                    : $"净空不足！最小净空 {minClearMm:F0} mm，规范要求 ≥ {minClearHeightMm} mm"
            };
        }

    }
}
