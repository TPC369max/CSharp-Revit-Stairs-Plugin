using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using StairsPlugin.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StairsPlugin.Model
{
    public class StairTopologyExtractor
    {
        // 1. 提取楼梯（跨层连接线）
        public static List<StairTopologyNode> Extract(Document doc)
        {
            var result = new List<StairTopologyNode>();
            var stairs = new FilteredElementCollector(doc).OfClass(typeof(Stairs)).Cast<Stairs>();

            foreach (Stairs stair in stairs)
            {
                ElementId baseLevelId = stair.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId();
                ElementId topLevelId = stair.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM)?.AsElementId();

                Level baseLevel = baseLevelId != null ? doc.GetElement(baseLevelId) as Level : null;
                Level topLevel = topLevelId != null ? doc.GetElement(topLevelId) as Level : null;

                var runs = stair.GetStairsRuns().Select(id => doc.GetElement(id) as StairsRun).OrderBy(r => r.BaseElevation).ToList();
                if (runs.Count == 0)
                    continue;

                StairsRun firstRun = runs.First();
                StairsRun lastRun = runs.Last();

                Line firstLine = firstRun.GetStairsPath()?.FirstOrDefault() as Line;
                Line lastLine = lastRun.GetStairsPath()?.LastOrDefault() as Line;

                if (firstLine == null || lastLine == null)
                    continue;

                XYZ entry = firstLine.GetEndPoint(0);
                XYZ exit = lastLine.GetEndPoint(1);

                result.Add(new StairTopologyNode
                {
                    StairElementId = stair.Id.IntegerValue,
                    BottomFloorName = baseLevel?.Name ?? "Unknown",
                    TopFloorName = topLevel?.Name ?? "Unknown",
                    // Z值强制使用楼层高程
                    EntryX = UnitConverter.FtToMm(entry.X),
                    EntryY = UnitConverter.FtToMm(entry.Y),
                    EntryZ = UnitConverter.FtToMm(baseLevel?.Elevation ?? 0),
                    ExitX = UnitConverter.FtToMm(exit.X),
                    ExitY = UnitConverter.FtToMm(exit.Y),
                    ExitZ = UnitConverter.FtToMm(topLevel?.Elevation ?? 0)
                });
            }
            return result;
        }

        // 2. 提取房间（空间节点）
        public static List<SpaceNode> ExtractSpaces(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms) // 精准抓取房间
                .WhereElementIsNotElementType()
                .Cast<SpatialElement>()
                .Where(s => s.Area > 0)
                .Select(s =>
                {
                    var loc = s.Location as LocationPoint;
                    Level level = doc.GetElement(s.LevelId) as Level;
                    return new SpaceNode
                    {
                        SpaceId = s.Id.IntegerValue,
                        SpaceName = s.Name,
                        FloorName = level?.Name ?? "Unknown",
                        Area = UnitConverter.FtToMm(UnitConverter.FtToMm(s.Area)), // 平方英尺转平方毫米
                        CenterX = UnitConverter.FtToMm(loc?.Point.X ?? 0),
                        CenterY = UnitConverter.FtToMm(loc?.Point.Y ?? 0),
                        ElevMm = UnitConverter.FtToMm(level?.Elevation ?? 0) // Z值使用楼层高程
                    };
                }).ToList();
        }

        // 3. 提取分析路径（同层连通线）
        public static List<PathNode> ExtractPaths(Document doc)
        {
            var result = new List<PathNode>();
            var paths = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PathOfTravelLines)
                .WhereElementIsNotElementType()
                .ToList();

            foreach (var pathEle in paths)
            {
                Level level = doc.GetElement(pathEle.LevelId) as Level;
                double zElevMm = UnitConverter.FtToMm(level?.Elevation ?? 0);
                double lengthFt = pathEle.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0;

                var points = new List<XYZ>();
                GeometryElement geomElem = pathEle.get_Geometry(new Options());

                if (geomElem != null)
                {
                    // 必须遍历 GeometryObject，不能直接 Cast
                    foreach (GeometryObject geomObj in geomElem)
                    {
                        if (geomObj is Line line)
                        {
                            if (points.Count == 0)
                                points.Add(line.GetEndPoint(0));
                            points.Add(line.GetEndPoint(1)); // 顺序相连
                        }
                        else if (geomObj is PolyLine polyLine)
                        {
                            points.AddRange(polyLine.GetCoordinates());
                        }
                    }
                }

                if (points.Count > 1)
                {
                    result.Add(new PathNode
                    {
                        PathId = pathEle.Id.IntegerValue,
                        LevelName = level?.Name ?? "Unknown",
                        LengthMm = UnitConverter.FtToMm(lengthFt),
                        // 将原始 XYZ 转为 [X_mm, Y_mm, 强制高程Z_mm]
                        Points = points.Select(p => new double[] {
                            UnitConverter.FtToMm(p.X),
                            UnitConverter.FtToMm(p.Y),
                            zElevMm
                        }).ToList()
                    });
                }
            }
            return result;
        }
    }

    // --- 数据传输对象 ---
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
        public double EntryX, EntryY, EntryZ;
        public double ExitX, ExitY, ExitZ;
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
        public double Area
        {
            get; set;
        }
        public double CenterX, CenterY, ElevMm;
    }

    public class PathNode
    {
        public int PathId
        {
            get; set;
        }
        public string LevelName
        {
            get; set;
        }
        public double LengthMm
        {
            get; set;
        }
        public List<double[]> Points { get; set; } = new List<double[]>();
    }
}