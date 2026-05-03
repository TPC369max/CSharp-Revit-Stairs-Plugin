using Autodesk.Revit.DB;
using StairsPlugin.Utils;   // UnitConverter
using System;
using System.Collections.Generic;

namespace StairsPlugin.Model
{
    // =========================================================
    //  净空校验结果
    // =========================================================
    public class ClearanceCheckResult
    {
        /// <summary>梯段与平台净高均合规时为 true</summary>
        public bool IsCompliant
        {
            get; set;
        }

        /// <summary>所有踏步面中射线探测到的最小净高（mm），-1 表示上方无遮挡</summary>
        public double MinStepClearanceMm
        {
            get; set;
        }

        /// <summary>休息平台处射线探测到的最小净高（mm），-1 表示上方无遮挡</summary>
        public double MinLandingClearanceMm
        {
            get; set;
        }

        /// <summary>违规时的可读消息；合规时为 null</summary>
        public string WarningMessage
        {
            get; set;
        }
    }

    // =========================================================
    //  净空检测器（射线法）
    //
    //  算法说明
    //  ─────────────────────────────────────────────────────────
    //  对每一个踏步面及平台面取一个代表点，以该点为射线起点沿
    //  +Z 方向投射虚拟射线（ReferenceIntersector），命中最近的
    //  楼板 / 结构构件底面，计算净高差。
    //
    //  选用射线法而非三维布尔碰撞，原因：
    //    • 无需生成实体，可在 StairsEditScope 启动前完成前置校验
    //    • 遍历每级踏步，不遗漏局部低点（例如倾斜梁底）
    //    • 纯代数运算，速度比布尔法快一到两个数量级
    //
    //  规范阈值（GB55031-2022 §5.3.9）：
    //    梯段净高 ≥ 2200 mm   （沿踏步鼻端竖直量取）
    //    平台净高 ≥ 2000 mm
    //
    //  重构说明（相对上一版本）：
    //    • 移除私有 FtToMm / BuildOrigin，改用 UnitConverter / CoordinateTransform
    //    • 两处调用方（本类 + StairGlobalEventHandler）现共享同一套坐标变换定义
    // =========================================================
    internal static class ClearanceChecker
    {
        // 只碰撞楼板、结构框架（梁）和屋顶，忽略楼梯自身构件
        private static readonly ElementMulticategoryFilter _obstacleFilter =
            new ElementMulticategoryFilter(new List<BuiltInCategory>
            {
                BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_Roofs
            });

        // ── 公共入口 ─────────────────────────────────────────────
        /// <summary>
        /// 在楼梯生成事务启动前对参数空间进行净空预检。
        /// 所有长度参数均使用 Revit 内部单位（英尺）。
        /// </summary>
        public static ClearanceCheckResult Check(
            Document doc,
            View3D view3D,
            XYZ insertionPoint,
            StairCalculationResult calcResult,
            double riserHeightFt,
            double treadDepthFt,
            double angleRad,
            double runWidthFt,
            double wellWidthFt,
            double landingDepthFt,
            bool clockwise,
            double baseElevFt,
            double minClearStepMm,
            double minClearLandingMm)
        {
            // ── 无三维视图时降级处理（不阻塞生成） ────────────────────
            if (view3D == null)
            {
                return new ClearanceCheckResult
                {
                    IsCompliant = true,
                    MinStepClearanceMm = -1,
                    MinLandingClearanceMm = -1,
                    WarningMessage = "未找到可用的三维视图，已跳过射线法净空校验。"
                };
            }

            // ── 构建射线检测器 ────────────────────────────────────────
            var intersector = new ReferenceIntersector(
                _obstacleFilter, FindReferenceTarget.Face, view3D)
            {
                FindReferencesInRevitLinks = false
            };

            // ── 构建坐标变换矩阵（与 StairGlobalEventHandler 共享同一定义）──
            // 使用 CoordinateTransform.CreateStairTransform 替代原先在此处内联的
            // rotate × translate 乘法，保证两处变换完全一致，消除潜在的坐标偏差。
            Transform tf = CoordinateTransform.CreateStairTransform(insertionPoint, angleRad);

            double run1Length = calcResult.Run1Steps * treadDepthFt;
            double run2Length = calcResult.Run2Steps * treadDepthFt;
            double halfY = (wellWidthFt + runWidthFt) / 2.0;
            double run1Y = clockwise ? -halfY : halfY;
            double run2Y = clockwise ? halfY : -halfY;

            double minStepClearFt = double.MaxValue;
            double minLandingClearFt = double.MaxValue;

            // ── 遍历第一跑每级踏步，取踏步面中心点射线向上 ───────────
            for (int i = 0; i < calcResult.Run1Steps; i++)
            {
                double localX = i * treadDepthFt;
                double stepElev = baseElevFt + (i + 1) * riserHeightFt;

                XYZ origin = CoordinateTransform.LocalToWorld(tf, localX, run1Y, stepElev);
                double clearFt = CastRayUp(intersector, origin);
                if (clearFt > 0 && clearFt < minStepClearFt)
                    minStepClearFt = clearFt;
            }

            // ── 遍历第二跑每级踏步 ────────────────────────────────────
            for (int j = 0; j <= calcResult.Run2Steps; j++)
            {
                // 第二跑沿局部 X 从平台端（run2Length）向插入点方向递减
                double localX = run2Length - j * treadDepthFt;
                double stepElev = baseElevFt + (calcResult.Run1Steps + j + 2) * riserHeightFt;

                XYZ origin = CoordinateTransform.LocalToWorld(tf, localX, run2Y, stepElev);
                double clearFt = CastRayUp(intersector, origin);
                if (clearFt > 0 && clearFt < minStepClearFt)
                    minStepClearFt = clearFt;
            }

            // ── 休息平台中心点 ────────────────────────────────────────
            // 平台位于两跑 X 端点正中、Y 方向居中（两跑中轴线的中点）
            {
                double landingLocalX = (run1Length)* 2.0+landingDepthFt/2.0;
                double landingLocalY = 0.0;
                double landingElev = baseElevFt + (calcResult.Run1Steps + 1) * riserHeightFt;

                XYZ origin = CoordinateTransform.LocalToWorld(tf, landingLocalX, landingLocalY, landingElev);
                minLandingClearFt = CastRayUp(intersector, origin);
            }

            // ── 转换为 mm 并判断合规 ──────────────────────────────────
            // -1 为"无遮挡"哨兵值，FtToMm 会原样透传
            double minStepMm = RoundFtToMm(minStepClearFt);
            double minLandingMm = RoundFtToMm(minLandingClearFt);

            bool stepOk = minStepMm < 0 || minStepMm >= minClearStepMm;
            bool landingOk = minLandingMm < 0 || minLandingMm >= minClearLandingMm;

            var msgs = new List<string>();
            if (!stepOk)
                msgs.Add($"梯段最小净高 {minStepMm:F0} mm，规范要求 ≥ {minClearStepMm:F0} mm");
            if (!landingOk)
                msgs.Add($"休息平台最小净高 {minLandingMm:F0} mm，规范要求 ≥ {minClearLandingMm:F0} mm");

            return new ClearanceCheckResult
            {
                IsCompliant = stepOk && landingOk,
                MinStepClearanceMm = minStepMm,
                MinLandingClearanceMm = minLandingMm,
                WarningMessage = msgs.Count > 0
                    ? "净空不足：\n" + string.Join("\n", msgs)
                    : null
            };
        }

        // ── 内部辅助 ─────────────────────────────────────────────

        /// <summary>
        /// 从 origin 沿 +Z 方向发射射线，返回第一个命中面的距离（英尺）。
        /// 若无命中则返回 -1（表示上方无遮挡）。
        /// 忽略距离 ≤ 1e-6 ft 的"自命中"（射线起点恰好贴在面上的情况）。
        /// </summary>
        private static double CastRayUp(ReferenceIntersector intersector, XYZ origin)
        {
            IList<ReferenceWithContext> hits = intersector.Find(origin, XYZ.BasisZ);
            if (hits == null || hits.Count == 0)
                return -1;

            double minProximity = double.MaxValue;
            foreach (var ctx in hits)
            {
                if (ctx.Proximity > 1e-6 && ctx.Proximity < minProximity)
                    minProximity = ctx.Proximity;
            }
            return minProximity == double.MaxValue ? -1 : minProximity;
        }

        /// <summary>
        /// 英尺转毫米并四舍五入到整数。
        /// -1（无遮挡哨兵）原样传递，不参与换算。
        /// 使用 UnitConverter 替代原先内联的 UnitUtils 调用。
        /// </summary>
        private static double RoundFtToMm(double ft)
        {
            if (ft < 0)
                return -1;
            return Math.Round(UnitConverter.FtToMm(ft), 0);
        }
    }
}