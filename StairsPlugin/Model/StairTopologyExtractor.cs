using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using StairsPlugin.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin.Model
{
    // StairTopologyExtractor.cs
    // 职责：将已生成的楼梯构件转换为拓扑节点描述，
    //       输出 JSON 供 Python 侧构建跨层图。
    public class StairTopologyExtractor
    {
        /// <summary>
        /// 从 Revit 文档中提取所有楼梯的连通节点，
        /// 生成 { stairId, bottomFloor, topFloor, entryXYZ, exitXYZ } 列表。
        /// </summary>
        public static List<StairTopologyNode> Extract(Document doc)
        {
            var result = new List<StairTopologyNode>();

            var stairs = new FilteredElementCollector(doc)
                .OfClass(typeof(Stairs))
                .Cast<Stairs>();

            foreach (Stairs stair in stairs)
            {
                // 通过 Parameters 获取底部和顶部标高的 ElementId
                ElementId baseLevelId = stair.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)
                                             ?.AsElementId();
                ElementId topLevelId = stair.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM)
                                             ?.AsElementId();

                Level baseLevel = baseLevelId != null ? doc.GetElement(baseLevelId) as Level : null;
                Level topLevel = topLevelId != null ? doc.GetElement(topLevelId) as Level : null;

                // 获取所有梯段，取第一跑的起点作为入口节点
                var runs = stair.GetStairsRuns()
                                .Select(id => doc.GetElement(id) as StairsRun)
                                .OrderBy(r => r.BaseElevation)
                                .ToList();

                if (runs.Count == 0)
                    continue;

                StairsRun firstRun = runs.First();
                StairsRun lastRun = runs.Last();

                CurveLoop firstPath = firstRun.GetStairsPath();
                CurveLoop lastPath = lastRun.GetStairsPath();

                if (firstPath == null || !firstPath.Any() ||
                    lastPath == null || !lastPath.Any())
                    continue;

                // CurveLoop 是 IEnumerable<Curve>，用 LINQ 取首尾
                Line firstLine = firstPath.First() as Line;
                Line lastLine = lastPath.Last() as Line;

                if (firstLine == null || lastLine == null)
                    continue;

                XYZ entry = firstLine.GetEndPoint(0);  // 第一跑起点
                XYZ exit = lastLine.GetEndPoint(1);   // 最后一跑终点

                double baseElevMm = UnitConverter.FtToMm(firstRun.BaseElevation);
                double topElevMm = UnitConverter.FtToMm(lastRun.TopElevation);

                result.Add(new StairTopologyNode
                {
                    StairElementId = stair.Id.IntegerValue,
                    BottomFloorName = baseLevel?.Name ?? "Unknown",
                    TopFloorName = topLevel?.Name ?? "Unknown",
                    BottomElevMm = UnitConverter.FtToMm(baseLevel?.Elevation ?? 0),
                    TopElevMm = UnitConverter.FtToMm(topLevel?.Elevation ?? 0),
                    // 坐标统一转换为 mm，供 Python 侧使用
                    EntryX = UnitConverter.FtToMm(entry.X),
                    EntryY = UnitConverter.FtToMm(entry.Y),
                    EntryZ = UnitConverter.FtToMm(entry.Z),
                    ExitX = UnitConverter.FtToMm(exit.X),
                    ExitY = UnitConverter.FtToMm(exit.Y),
                    ExitZ = UnitConverter.FtToMm(topElevMm),
                    // 楼梯段的总路径长度（走行距离，mm）
                    PathLengthMm = UnitConverter.FtToMm(
                        runs.Sum(r => (r.Location as LocationCurve)?.Curve.Length ?? 0))
                });
            }
            return result;
        }

        /// <summary>
        /// 同时提取所有房间的几何中心，作为水平层的空间节点。
        /// </summary>
        public static List<SpaceNode> ExtractSpaces(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(SpatialElement))
                .WhereElementIsNotElementType()
                .Cast<SpatialElement>()
                .Where(s => s.Area > 0) // 过滤未放置的空间
                .Select(s =>
                {
                    var loc = s.Location as LocationPoint;
                    return new SpaceNode
                    {
                        SpaceId = s.Id.IntegerValue,
                        SpaceName = s.Name,
                        FloorName = (doc.GetElement(s.LevelId) as Level)?.Name ?? "Unknown",
                        CenterX = UnitConverter.FtToMm(loc?.Point.X ?? 0),
                        CenterY = UnitConverter.FtToMm(loc?.Point.Y ?? 0),
                        ElevMm = UnitConverter.FtToMm(
                            (doc.GetElement(s.LevelId) as Level)?.Elevation ?? 0)
                    };
                }).ToList();
        }
    }

    // 数据传输对象（序列化为 JSON 后传给 Python）
    public class StairTopologyNode
    {
        public int StairElementId
        {
            get; set;
        }
        public string BottomFloorName
        {
            get; set;
        }
        public string TopFloorName
        {
            get; set;
        }
        public double BottomElevMm
        {
            get; set;
        }
        public double TopElevMm
        {
            get; set;
        }
        public double EntryX, EntryY, EntryZ;
        public double ExitX, ExitY, ExitZ;
        public double PathLengthMm
        {
            get; set;
        }
    }

    public class SpaceNode
    {
        public int SpaceId
        {
            get; set;
        }
        public string SpaceName
        {
            get; set;
        }
        public string FloorName
        {
            get; set;
        }
        public double CenterX, CenterY, ElevMm;
    }
}
