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
        // ================================================================
        //  1. 提取楼梯（跨层连接线）
        //
        //  每部楼梯输出 4 个航点，形成 U 形双跑楼梯的完整路径：
        //    P0  底层入口（Z 强制取底部标高高程）
        //    P1  第一跑顶端 / 平台入口（Z 取几何实际值）
        //    P2  第二跑底端 / 平台出口（Z 取几何实际值）
        //    P3  顶层出口（Z 强制取顶部标高高程）
        //
        //  单跑楼梯退化为 2 点（P0、P3）。
        // ================================================================
        public static List<StairTopologyNode> Extract(Document doc)
        {
            var result = new List<StairTopologyNode>();
            var stairs = new FilteredElementCollector(doc)
                .OfClass(typeof(Stairs))
                .Cast<Stairs>();

            foreach (Stairs stair in stairs)
            {
                // ── 读取起止标高 ──────────────────────────────────────────
                ElementId baseLevelId = stair.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId();
                ElementId topLevelId = stair.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM)?.AsElementId();
                Level baseLevel = baseLevelId != null ? doc.GetElement(baseLevelId) as Level : null;
                Level topLevel = topLevelId != null ? doc.GetElement(topLevelId) as Level : null;

                // ── 按底部高程排序所有梯段 ─────────────────────────────────
                var runs = stair.GetStairsRuns()
                    .Select(id => doc.GetElement(id) as StairsRun)
                    .Where(r => r != null)
                    .OrderBy(r => r.BaseElevation)
                    .ToList();

                if (runs.Count == 0)
                    continue;

                StairsRun firstRun = runs.First();
                StairsRun lastRun = runs.Last();

                // ── 从每跑路径曲线列表中取首/尾 Line ─────────────────────
                var firstPath = firstRun.GetStairsPath()?.OfType<Line>().ToList();
                var lastPath = lastRun.GetStairsPath()?.OfType<Line>().ToList();

                if (firstPath == null || firstPath.Count == 0)
                    continue;
                if (lastPath == null || lastPath.Count == 0)
                    continue;

                Line firstSegment = firstPath.First(); // 第一跑第一条线（底部）
                Line firstTopSeg = firstPath.Last();  // 第一跑最后一条线（顶部）
                Line lastBotSeg = lastPath.First();  // 最后跑第一条线（底部）
                Line lastSegment = lastPath.Last();   // 最后跑最后一条线（顶部）

                // P0：底层入口（Z 强制用底部标高）
                XYZ p0Raw = firstSegment.GetEndPoint(0);
                // P1：第一跑顶端 / 平台入口（Z 用几何实际值，因为平台没有命名标高）
                XYZ p1Raw = firstTopSeg.GetEndPoint(1);
                // P2：最后跑底端 / 平台出口（Z 用几何实际值）
                XYZ p2Raw = lastBotSeg.GetEndPoint(0);
                // P3：顶层出口（Z 强制用顶部标高）
                XYZ p3Raw = lastSegment.GetEndPoint(1);

                double baseLevelElevMm = UnitConverter.FtToMm(baseLevel?.Elevation ?? 0);
                double topLevelElevMm = UnitConverter.FtToMm(topLevel?.Elevation ?? 0);

                // ── 构造 4 点航点列表 ─────────────────────────────────────
                // [经度比例X, 纬度比例Y, 高程Z_mm]
                // 单跑楼梯 firstRun == lastRun 时 P1 == P3、P2 == P0，
                // Distinct 去重后退化为 2 点。
                var points = new List<double[]>
                {
                    new double[] { UnitConverter.FtToMm(p0Raw.X), UnitConverter.FtToMm(p0Raw.Y), baseLevelElevMm },
                    new double[] { UnitConverter.FtToMm(p1Raw.X), UnitConverter.FtToMm(p1Raw.Y), UnitConverter.FtToMm(p1Raw.Z) },
                    new double[] { UnitConverter.FtToMm(p2Raw.X), UnitConverter.FtToMm(p2Raw.Y), UnitConverter.FtToMm(p2Raw.Z) },
                    new double[] { UnitConverter.FtToMm(p3Raw.X), UnitConverter.FtToMm(p3Raw.Y), topLevelElevMm  },
                };

                // 单跑退化：若 P1 与 P0 完全重合、P2 与 P3 完全重合，只保留首尾
                points = DeduplicateWaypoints(points);

                result.Add(new StairTopologyNode
                {
                    StairElementId = stair.Id.IntegerValue,
                    BottomFloorName = baseLevel?.Name ?? "Unknown",
                    TopFloorName = topLevel?.Name ?? "Unknown",
                    Points = points
                });
            }
            return result;
        }

        // ================================================================
        //  2. 提取房间（空间节点）
        // ================================================================
        public static List<SpaceNode> ExtractSpaces(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
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
                        Area = UnitConverter.FtToMm(UnitConverter.FtToMm(s.Area)), // ft² → mm²
                        CenterX = UnitConverter.FtToMm(loc?.Point.X ?? 0),
                        CenterY = UnitConverter.FtToMm(loc?.Point.Y ?? 0),
                        ElevMm = UnitConverter.FtToMm(level?.Elevation ?? 0)
                    };
                }).ToList();
        }

        // ================================================================
        //  3. 提取分析路径（同层连通线）
        //
        //  Bug 修复说明（三处叠加缺陷）：
        //
        //  ① 仅判断 `is Line`，漏掉其他 Curve 子类（Arc、NurbSpline 等）
        //     → 改为 `is Curve`，统一取 GetEndPoint(0/1)。
        //
        //  ② `if (points.Count == 0)` 假设几何对象按行进顺序迭代：
        //     只有第一个对象的起点被收录，后续对象仅收终点。
        //     若 Revit 以非顺序返回线段，中间节点的起点被静默丢弃，
        //     导致每条路径恰好少 1 个点（= 最先被跳过的那段起点）。
        //     → 改为"收集所有线段端点对 → 按连通性重新链接"，
        //       与迭代顺序无关。
        //
        //  ③ 未处理 GeometryInstance：若路径几何被包在实例对象中，
        //     整个对象被静默跳过，points 为空，路径被丢弃。
        //     → 递归展开 GeometryInstance.GetInstanceGeometry()。
        //
        //  标高修复（沿用上一版本策略）：
        //    PathOfTravel 元素的 Element.LevelId 在部分版本返回 InvalidElementId，
        //    依次尝试 ① LevelId ② LEVEL_PARAM 参数 ③ 几何点 Z 均值兜底。
        // ================================================================
        public static List<PathNode> ExtractPaths(Document doc)
        {
            var result = new List<PathNode>();
            var paths = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PathOfTravelLines)
                .WhereElementIsNotElementType()
                .ToList();

            foreach (var pathEle in paths)
            {
                // ── 多级标高查找 ──────────────────────────────────────────
                Level level = ResolveLevel(doc, pathEle);
                double? forcedZMm = level != null
                    ? (double?)UnitConverter.FtToMm(level.Elevation)
                    : null;

                double lengthFt = pathEle.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0;

                // ── Step 1：收集所有线段端点对（与迭代顺序无关）────────────
                // 每条线段存为 (startXYZ, endXYZ) 存入两个平行列表，
                // 支持 Curve 所有子类及 PolyLine、GeometryInstance 包装。
                var segStarts = new List<XYZ>();
                var segEnds = new List<XYZ>();

                GeometryElement geomElem = pathEle.get_Geometry(new Options());
                if (geomElem != null)
                {
                    foreach (GeometryObject geomObj in geomElem)
                        CollectSegments(geomObj, segStarts, segEnds);
                }

                if (segStarts.Count == 0)
                    continue;

                // ── Step 2：按连通性链接线段，得到有序折线点列表 ─────────
                List<XYZ> points = ChainPath(segStarts, segEnds);
                if (points.Count < 2)
                    continue;

                // ── Z 兜底：标高未找到时取几何点 Z 的平均值 ───────────────
                if (forcedZMm == null)
                    forcedZMm = UnitConverter.FtToMm(points.Average(p => p.Z));

                result.Add(new PathNode
                {
                    PathId = pathEle.Id.IntegerValue,
                    LevelName = level?.Name ?? "Unknown",
                    LengthMm = UnitConverter.FtToMm(lengthFt),
                    // XY 来自几何，Z 强制为标高高程（或几何均值兜底）
                    Points = points.Select(p => new double[]
                    {
                        UnitConverter.FtToMm(p.X),
                        UnitConverter.FtToMm(p.Y),
                        forcedZMm.Value
                    }).ToList()
                });
            }
            return result;
        }

        // ----------------------------------------------------------------
        //  辅助：递归收集几何对象中的所有曲线线段端点对
        //
        //  处理：
        //    • Curve（Line/Arc/NurbSpline 等所有子类）→ 取两端点
        //    • PolyLine → 拆解为每段的端点对
        //    • GeometryInstance → 递归展开 GetInstanceGeometry()
        //  其余类型（Solid、Mesh 等）静默跳过。
        // ----------------------------------------------------------------
        private static void CollectSegments(
            GeometryObject geomObj,
            List<XYZ> starts,
            List<XYZ> ends)
        {
            if (geomObj is Curve curve)
            {
                // Line 是 Curve 子类，同样命中此分支
                starts.Add(curve.GetEndPoint(0));
                ends.Add(curve.GetEndPoint(1));
            }
            else if (geomObj is PolyLine polyLine)
            {
                IList<XYZ> coords = polyLine.GetCoordinates();
                for (int i = 0; i < coords.Count - 1; i++)
                {
                    starts.Add(coords[i]);
                    ends.Add(coords[i + 1]);
                }
            }
            else if (geomObj is GeometryInstance gi)
            {
                // 展开实例几何，递归处理每个子对象
                foreach (GeometryObject inner in gi.GetInstanceGeometry())
                    CollectSegments(inner, starts, ends);
            }
            // Solid / Mesh / 其他 → 与路径拓扑无关，跳过
        }

        // ----------------------------------------------------------------
        //  辅助：将无序线段端点对链接为有序折线顶点列表
        //
        //  算法：
        //    1. 找路径起点：在所有 starts 中，不被任何 end 覆盖的点即为起点；
        //       若无法确定（环形路径或单线段），退化为第一个线段。
        //    2. 从起点出发，每步在未使用线段中寻找 start ≈ 当前 end 的线段，
        //       依次追加终点，直到无后继为止。
        //
        //  容差：0.001 ft（≈ 0.3 mm），足以吸收浮点误差，不会误合并相邻节点。
        // ----------------------------------------------------------------
        private static List<XYZ> ChainPath(List<XYZ> starts, List<XYZ> ends)
        {
            const double tolFt = 0.001; // ≈ 0.3 mm
            int n = starts.Count;
            var used = new bool[n];

            // 找起始线段索引（其 start 不是任何其他线段的 end）
            int firstIdx = 0;
            for (int i = 0; i < n; i++)
            {
                bool coveredByEnd = false;
                for (int j = 0; j < n; j++)
                {
                    if (ends[j].DistanceTo(starts[i]) < tolFt)
                    {
                        coveredByEnd = true;
                        break;
                    }
                }
                if (!coveredByEnd)
                {
                    firstIdx = i;
                    break;
                }
            }

            // 从起始线段出发，按连通性链接
            var result = new List<XYZ>();
            int cur = firstIdx;
            while (cur >= 0 && cur < n && !used[cur])
            {
                used[cur] = true;
                if (result.Count == 0)
                    result.Add(starts[cur]); // 第一段：加起点 + 终点
                result.Add(ends[cur]);

                // 在未使用线段中找下一段（其 start ≈ 当前 end）
                int next = -1;
                for (int i = 0; i < n; i++)
                {
                    if (!used[i] && starts[i].DistanceTo(ends[cur]) < tolFt)
                    {
                        next = i;
                        break;
                    }
                }
                cur = next;
            }

            return result;
        }

        // ----------------------------------------------------------------
        //  辅助：多级标高解析
        //  ① Element.LevelId（部分元素直接持有）
        //  ② LEVEL_PARAM 内置参数（PathOfTravel 常用此参数存储关联标高）
        // ----------------------------------------------------------------
        private static Level ResolveLevel(Document doc, Element element)
        {
            // 尝试 ①：Element.LevelId
            if (element.LevelId != null && element.LevelId != ElementId.InvalidElementId)
            {
                Level lv = doc.GetElement(element.LevelId) as Level;
                if (lv != null)
                    return lv;
            }

            // 尝试 ②：LEVEL_PARAM 参数
            ElementId paramLevelId = element.get_Parameter(BuiltInParameter.LEVEL_PARAM)?.AsElementId();
            if (paramLevelId != null && paramLevelId != ElementId.InvalidElementId)
            {
                Level lv = doc.GetElement(paramLevelId) as Level;
                if (lv != null)
                    return lv;
            }

            // 未找到，返回 null，由调用方用几何 Z 兜底
            return null;
        }

        // ----------------------------------------------------------------
        //  辅助：去除连续重复航点（XY 差 < 1 mm 视为重合）
        //  用于单跑楼梯退化为 2 点的情形。
        // ----------------------------------------------------------------
        private static List<double[]> DeduplicateWaypoints(List<double[]> pts)
        {
            const double tol = 1.0; // mm
            var deduped = new List<double[]> { pts[0] };
            for (int i = 1; i < pts.Count; i++)
            {
                double[] prev = deduped[deduped.Count - 1];
                double[] cur = pts[i];
                double dx = cur[0] - prev[0];
                double dy = cur[1] - prev[1];
                if (Math.Sqrt(dx * dx + dy * dy) > tol)
                    deduped.Add(cur);
            }
            return deduped;
        }
    }

    // ====================================================================
    //  数据传输对象
    // ====================================================================

    /// <summary>
    /// 楼梯拓扑节点。
    /// Points 包含沿楼梯路径的有序航点（通常 4 点，单跑退化为 2 点）：
    ///   [0] 底层入口，Z = 底部标高高程
    ///   [1] 第一跑顶端 / 平台入口，Z = 几何实际值
    ///   [2] 第二跑底端 / 平台出口，Z = 几何实际值
    ///   [3] 顶层出口，Z = 顶部标高高程
    /// 每个元素为 double[3]：[X_mm, Y_mm, Z_mm]。
    /// </summary>
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

        /// <summary>有序航点列表，[X_mm, Y_mm, Z_mm]。</summary>
        public List<double[]> Points { get; set; } = new List<double[]>();
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