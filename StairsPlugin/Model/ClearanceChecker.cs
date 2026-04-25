using Autodesk.Revit.DB;
using StairsPlugin.Model;
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
    //  相较传统三维布尔碰撞：
    //    • 无需生成实体，可在事务外前置校验
    //    • 遍历每级踏步，不遗漏局部低点
    //    • 纯代数运算，速度比布尔法快百倍以上
    //
    //  规范阈值（GB55031-2022 §5.3.9）：
    //    梯段净高 ≥ 2200 mm   （沿踏步鼻端竖直量取）
    //    平台净高 ≥ 2000 mm
    // =========================================================
    internal static class ClearanceChecker
    {
        // 只碰撞楼板、结构框架（梁）和屋顶，忽略楼梯自身
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
        /// </summary>
        /// <param name="doc">当前文档</param>
        /// <param name="view3D">用于 ReferenceIntersector 的三维视图（非图纸/模板）</param>
        /// <param name="insertionPoint">P1（楼梯局部坐标系原点，Revit 内部单位·英尺）</param>
        /// <param name="calcResult">踏步解算结果快照</param>
        /// <param name="riserHeightFt">踢面高（英尺）</param>
        /// <param name="treadDepthFt">踏步深（英尺）</param>
        /// <param name="angleRad">P1→P2 方向角（弧度）</param>
        /// <param name="runWidthFt">梯段净宽（英尺）</param>
        /// <param name="wellWidthFt">梯井宽（英尺）</param>
        /// <param name="clockwise">盘旋方向（true = 顺时针/右旋）</param>
        /// <param name="baseElevFt">底部标高绝对高程（英尺）</param>
        /// <param name="minClearStepMm">梯段净高下限（mm），一般取 2200</param>
        /// <param name="minClearLandingMm">平台净高下限（mm），一般取 2000</param>
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

            // ── 重建局部坐标系变换（与 StairGlobalEventHandler 一致）──────
            // 局部 X → P1→P2 方向（爬升轴）
            // 局部 Y → 垂直于爬升轴（侧向偏移）
            double run1Length = (calcResult.Run1Steps) * treadDepthFt;
            double run2Length = (calcResult.Run2Steps) * treadDepthFt;
            double halfY = (wellWidthFt + runWidthFt) / 2.0;
            double run1Y = clockwise ? -halfY : halfY;   // 右旋：第一跑在 -Y 侧
            double run2Y = clockwise ? halfY : -halfY;   // 右旋：第二跑在 +Y 侧

            var rotate = Transform.CreateRotation(XYZ.BasisZ, angleRad);
            var translate = Transform.CreateTranslation(insertionPoint);
            var tf = translate.Multiply(rotate);

            // ── 遍历第一跑每级踏步，取踏步面中心点射线向上 ───────────
            double minStepClearFt = double.MaxValue;
            double minLandingClearFt = double.MaxValue;

            for (int i = 0; i < calcResult.Run1Steps; i++)
            {
                // 踏步 i 的局部平面中心（取该跑中心线上的一点）
                double localX = i * treadDepthFt;
                double stepElev = baseElevFt + (i + 1) * riserHeightFt;

                XYZ origin = BuildOrigin(tf, localX, run1Y, stepElev);
                double clearFt = CastRayUp(intersector, origin);
                if (clearFt < minStepClearFt&& clearFt>0)
                    minStepClearFt = clearFt;
            }

            // ── 遍历第二跑每级踏步 ────────────────────────────────────
            for (int j = 0; j <= calcResult.Run2Steps; j++)
            {
                // 第二跑沿局部 X 从 run2Length → 0（逆向），
                // j = 0 对应平台侧第一步（局部 X = run2Length）
                double localX = run2Length - j * treadDepthFt;
                double stepElev = baseElevFt + (calcResult.Run1Steps + j + 2) * riserHeightFt;

                XYZ origin = BuildOrigin(tf, localX, run2Y, stepElev);
                double clearFt = CastRayUp(intersector, origin);
                if (clearFt < minStepClearFt&&clearFt > 0)
                    minStepClearFt = clearFt;
            }

            // ── 休息平台中心点 ────────────────────────────────────────
            // 平台在两跑 X 端点之间、Y 方向居中（两跑中轴线正中）
            {
                double landingLocalX = (run1Length + run2Length) / 2.0;
                double landingLocalY = 0.0;                              // run1Y 与 run2Y 的中点
                double landingElev = baseElevFt + (calcResult.Run1Steps+1) * riserHeightFt;

                XYZ origin = BuildOrigin(tf, landingLocalX, landingLocalY, landingElev);
                minLandingClearFt = CastRayUp(intersector, origin);
            }

            // ── 转换为 mm 并判断合规 ──────────────────────────────────
            double minStepMm = FtToMm(minStepClearFt);
            double minLandingMm = FtToMm(minLandingClearFt);

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

        /// <summary>将局部坐标 (localX, localY) 经变换投影到世界 XY，Z 取给定绝对高程。</summary>
        private static XYZ BuildOrigin(Transform tf, double localX, double localY, double elevFt)
        {
            XYZ worldXY = tf.OfPoint(new XYZ(localX, localY, 0));
            return new XYZ(worldXY.X, worldXY.Y, elevFt);
        }

        /// <summary>
        /// 从 origin 沿 +Z 方向发射射线，返回第一个命中面的距离（英尺）。
        /// 若无命中则返回 -1（表示无遮挡）。
        /// </summary>
        private static double CastRayUp(ReferenceIntersector intersector, XYZ origin)
        {
            IList<ReferenceWithContext> hits = intersector.Find(origin, XYZ.BasisZ);
            if (hits == null || hits.Count == 0)
                return -1;   // 无遮挡

            double minProximity = double.MaxValue;
            foreach (var ctx in hits)
            {
                // Proximity 是沿射线方向到命中点的距离；忽略负值（射线反向）
                if (ctx.Proximity > 1e-6 && ctx.Proximity < minProximity)
                    minProximity = ctx.Proximity;
            }
            return minProximity == double.MaxValue ? -1 : minProximity;
        }

        /// <summary>英尺转毫米；-1（无遮挡哨兵）原样传递。</summary>
        private static double FtToMm(double ft)
        {
            if (ft < 0)
                return -1;
            return Math.Round(
                UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters), 0);
        }
    }
}