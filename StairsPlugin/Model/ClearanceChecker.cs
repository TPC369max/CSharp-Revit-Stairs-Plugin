using Autodesk.Revit.DB;
using StairsPlugin.Utils;   // UnitConverter
using System;
using System.Collections.Generic;

namespace StairsPlugin.Model
{
    // =========================================================
    //  净空校验结果（值对象）
    //
    //  由 ClearanceChecker.Check() 返回，供调用方（StairGlobalEventHandler）
    //  根据 IsCompliant 决定是否继续生成，并将 MinStep/LandingClearanceMm
    //  展示在 TaskDialog 中供用户参考。
    // =========================================================
    public class ClearanceCheckResult
    {
        /// <summary>
        /// 梯段与平台净高均满足规范要求时为 true；任一不足则为 false。
        /// 调用方依据此值决定是否弹出警告对话框。
        /// </summary>
        public bool IsCompliant
        {
            get; set;
        }

        /// <summary>
        /// 所有踏步面中，射线探测到的最小净高（mm，四舍五入到整数）。
        /// -1 为"上方无遮挡"哨兵值，表示射线未命中任何障碍物，
        /// 此时净高视为无穷大，不触发警告。
        /// </summary>
        public double MinStepClearanceMm
        {
            get; set;
        }

        /// <summary>
        /// 休息平台中心处，射线探测到的最小净高（mm）。
        /// -1 含义同 MinStepClearanceMm（上方无遮挡）。
        /// </summary>
        public double MinLandingClearanceMm
        {
            get; set;
        }

        /// <summary>
        /// 不合规时的可读警告消息（包含具体不足项和规范要求值）；
        /// 合规时为 null，调用方可用 null 判断是否有违规。
        /// </summary>
        public string WarningMessage
        {
            get; set;
        }
    }

    // =========================================================
    //  净空检测器（射线法）
    //
    //  ── 算法说明 ────────────────────────────────────────────
    //  对每一个踏步面及平台面取一个代表点（射线起点），
    //  以该点为原点沿 +Z 方向投射虚拟射线（ReferenceIntersector），
    //  命中最近的楼板 / 结构构件底面，计算"射线起点 Z" 到
    //  "命中点 Z" 的距离差，即为该处净高。
    //
    //  ── 选用射线法而非三维布尔碰撞的原因 ───────────────────
    //    1. 无需生成实体：可在 StairsEditScope 启动前完成前置校验，
    //       不产生任何 Revit 模型修改。
    //    2. 逐级遍历：不遗漏局部低点（如倾斜梁底、坡屋顶下方）。
    //    3. 速度：纯代数射线求交比布尔实体运算快一到两个数量级，
    //       对于楼梯生成场景（几十个踏步）几乎无感知延迟。
    //
    //  ── 规范阈值（GB55031-2022 §5.3.9） ─────────────────────
    //    梯段净高 ≥ 2200 mm（沿踏步鼻端铅垂量取）
    //    休息平台净高 ≥ 2000 mm
    //
    //  ── 重构说明 ─────────────────────────────────────────────
    //    • 移除原私有方法 FtToMm / BuildOrigin，
    //      改用 UnitConverter.FtToMm 和 CoordinateTransform.LocalToWorld，
    //      与 StairGlobalEventHandler 共享同一套工具方法定义。
    //    • 消除两处坐标变换矩阵构建代码不一致的隐患。
    // =========================================================
    internal static class ClearanceChecker
    {
        // 射线只碰撞楼板、结构框架（梁）和屋顶：
        //   OST_Floors          — 常见的结构楼板和建筑楼板
        //   OST_StructuralFraming — 梁（可能成为踏步上方的低点）
        //   OST_Roofs           — 坡屋顶下方的楼梯净高校验
        // 不包含楼梯自身构件，避免射线命中梯段本身而误报。
        private static readonly ElementMulticategoryFilter _obstacleFilter =
            new ElementMulticategoryFilter(new List<BuiltInCategory>
            {
                BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_Roofs
            });

        // ── 公共入口 ─────────────────────────────────────────────
        /// <summary>
        /// 在楼梯生成事务启动前对参数空间进行净空预检（前置拦截）。
        ///
        /// ── 调用时机 ──────────────────────────────────────────────
        /// 由 StairGlobalEventHandler.Execute() 在 StairsEditScope 打开之前调用，
        /// 确保不合规时可直接 return，不产生任何 Revit 模型修改。
        ///
        /// ── 降级处理 ──────────────────────────────────────────────
        /// 若文档中无可用的三维视图（view3D == null），射线检测无法执行，
        /// 返回 IsCompliant=true 并在 WarningMessage 中注明已跳过，
        /// 不阻塞后续生成流程。
        ///
        /// ── 所有长度参数均使用 Revit 内部单位（英尺） ──────────────
        /// </summary>
        /// <param name="doc">当前 Revit 文档（view3D==null 时可传 null）</param>
        /// <param name="view3D">用于 ReferenceIntersector 的三维视图；为 null 则跳过检测</param>
        /// <param name="insertionPoint">P1 插入点（世界坐标，Z 已修正为绝对高程，英尺）</param>
        /// <param name="calcResult">ViewModel 已解算的踏步参数快照</param>
        /// <param name="riserHeightFt">踢面高（英尺）</param>
        /// <param name="treadDepthFt">踏步宽（英尺）</param>
        /// <param name="angleRad">P1→P2 方向角（弧度）</param>
        /// <param name="runWidthFt">梯段净宽（英尺）</param>
        /// <param name="wellWidthFt">梯井宽（英尺）</param>
        /// <param name="landingDepthFt">休息平台深度（英尺）</param>
        /// <param name="clockwise">true=右旋（顺时针），false=左旋（逆时针）</param>
        /// <param name="baseElevFt">楼梯底部绝对高程（英尺，已加底部偏移）</param>
        /// <param name="minClearStepMm">梯段净高规范阈值（mm），通常为 2200</param>
        /// <param name="minClearLandingMm">平台净高规范阈值（mm），通常为 2000</param>
        /// <returns>净空校验结果，IsCompliant=false 时包含警告文字</returns>
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
            // ── 无三维视图时降级处理：跳过检测，不阻塞生成 ──────────
            if (view3D == null)
            {
                return new ClearanceCheckResult
                {
                    IsCompliant           = true,
                    MinStepClearanceMm    = -1,
                    MinLandingClearanceMm = -1,
                    WarningMessage        = "未找到可用的三维视图，已跳过射线法净空校验。"
                };
            }

            // ── 构建射线检测器 ────────────────────────────────────────
            // FindReferencesInRevitLinks=false：不穿透链接模型，
            // 仅检测当前文档内的障碍物，避免误命中链接文件中的楼板。
            var intersector = new ReferenceIntersector(
                _obstacleFilter, FindReferenceTarget.Face, view3D)
            {
                FindReferencesInRevitLinks = false
            };

            // ── 构建坐标变换矩阵 ──────────────────────────────────────
            // 使用 CoordinateTransform.CreateStairTransform 而非内联的
            // rotate × translate 乘法，保证与 StairGlobalEventHandler 完全一致，
            // 消除因代码重复导致的坐标偏差风险。
            Transform tf = CoordinateTransform.CreateStairTransform(insertionPoint, angleRad);

            // ── 预计算两跑的几何参数 ──────────────────────────────────
            double run1Length = calcResult.Run1Steps * treadDepthFt;
            double run2Length = calcResult.Run2Steps * treadDepthFt;
            // halfY：梯段中心线到楼梯中轴线的横向距离 = (井道宽 + 梯段净宽) / 2
            double halfY  = (wellWidthFt + runWidthFt) / 2.0;
            // run1Y / run2Y：两跑中心线的局部 Y 坐标，符号由盘旋方向决定
            double run1Y  = clockwise ? -halfY : halfY;
            double run2Y  = clockwise ?  halfY : -halfY;

            // 以 double.MaxValue 作为"尚未命中任何障碍"的初始哨兵值
            double minStepClearFt    = double.MaxValue;
            double minLandingClearFt = double.MaxValue;

            // ── 遍历第一跑每级踏步，取踏步面中心点向上发射射线 ────────
            // i=0 对应第一个踏步面（localX=0），以此类推
            for (int i = 0; i < calcResult.Run1Steps; i++)
            {
                // 踏步面起点的局部 X（踏步前沿位置）
                double localX  = i * treadDepthFt;
                // 该踏步面的高程：baseElev + (i+1) 个踢面（含起步踢面）
                double stepElev = baseElevFt + (i + 1) * riserHeightFt;

                XYZ origin = CoordinateTransform.LocalToWorld(tf, localX, run1Y, stepElev);
                double clearFt = CastRayUp(intersector, origin);
                // 保留所有踏步中最小净高值，用于最终合规判定
                if (clearFt > 0 && clearFt < minStepClearFt)
                    minStepClearFt = clearFt;
            }

            // ── 遍历第二跑每级踏步 ────────────────────────────────────
            // 第二跑从平台端（localX=run2Length）向插入点方向逐步递减。
            // 循环条件为 j <= Run2Steps（含等号），共 Run2Steps+1 次，
            // 比第一跑（i < Run1Steps，Run1Steps 次）多一次迭代。
            // 额外的 j=Run2Steps 对应 localX=0（第二跑末端，顶层出口前的最后一踏步），
            // 用于检测紧邻顶部楼板处的净高，确保全跑无检测盲区。
            for (int j = 0; j <= calcResult.Run2Steps; j++)
            {
                double localX  = run2Length - j * treadDepthFt;
                // 第二跑高程从 (run1Steps+2) 个踢面开始累加
                double stepElev = baseElevFt + (calcResult.Run1Steps + j + 2) * riserHeightFt;

                XYZ origin = CoordinateTransform.LocalToWorld(tf, localX, run2Y, stepElev);
                double clearFt = CastRayUp(intersector, origin);
                if (clearFt > 0 && clearFt < minStepClearFt)
                    minStepClearFt = clearFt;
            }

            // ── 休息平台中心点净空检测 ────────────────────────────────
            // 检测点取平台 X 中线：run1Length + landingDepthFt/2。
            //   当两跑等长（run1Length == run2Length）时，此值精确落在平台中心；
            //   当 run1Length < run2Length 时，xMin=run1Length，中心同为 run1Length+depth/2，结果仍正确；
            //   当 run1Length > run2Length 时（本插件不会出现此情形，
            //   因 ViewModel 强制 totalSteps 为偶数，两跑差 ≤1 步），公式存在轻微偏移，可接受。
            // Y 方向取两跑中轴线的中点（localY=0）
            {
                double landingLocalX = (run1Length) + landingDepthFt / 2.0;
                double landingLocalY = 0.0;
                // 平台底面高程 = baseElev + (run1Steps+1) 个踢面
                double landingElev   = baseElevFt + (calcResult.Run1Steps + 1) * riserHeightFt;

                XYZ origin = CoordinateTransform.LocalToWorld(tf, landingLocalX, landingLocalY, landingElev);
                minLandingClearFt = CastRayUp(intersector, origin);
            }

            // ── 将英尺净高转换为 mm 并进行合规判定 ───────────────────
            // -1（无遮挡哨兵）由 RoundFtToMm 原样透传，不参与换算
            double minStepMm    = RoundFtToMm(minStepClearFt);
            double minLandingMm = RoundFtToMm(minLandingClearFt);

            // 值为 -1（无遮挡）时视为合规；有遮挡时与规范阈值比较
            bool stepOk    = minStepMm    < 0 || minStepMm    >= minClearStepMm;
            bool landingOk = minLandingMm < 0 || minLandingMm >= minClearLandingMm;

            // ── 组装警告消息 ──────────────────────────────────────────
            var msgs = new List<string>();
            if (!stepOk)
                msgs.Add($"梯段最小净高 {minStepMm:F0} mm，规范要求 ≥ {minClearStepMm:F0} mm");
            if (!landingOk)
                msgs.Add($"休息平台最小净高 {minLandingMm:F0} mm，规范要求 ≥ {minClearLandingMm:F0} mm");

            return new ClearanceCheckResult
            {
                IsCompliant           = stepOk && landingOk,
                MinStepClearanceMm    = minStepMm,
                MinLandingClearanceMm = minLandingMm,
                // 合规时 WarningMessage 为 null，便于调用方用 null 判断
                WarningMessage        = msgs.Count > 0
                    ? "净空不足：\n" + string.Join("\n", msgs)
                    : null
            };
        }

        // ── 内部辅助方法 ─────────────────────────────────────────────

        /// <summary>
        /// 从 <paramref name="origin"/> 沿 +Z 方向发射射线，
        /// 返回第一个命中面的距离（英尺）。
        ///
        /// ── 返回值约定 ────────────────────────────────────────────────
        /// -1  → 无命中（上方无遮挡，净高视为无穷大）
        /// >0  → 距离，单位英尺，即为该点的净高
        ///
        /// ── 自命中过滤 ────────────────────────────────────────────────
        /// 射线起点可能恰好位于踏步面上（Proximity ≈ 0），
        /// 忽略 Proximity ≤ 1e-6 ft 的命中，避免"踏步面命中自身"导致净高为 0。
        ///
        /// ── 为何只取最小 Proximity ────────────────────────────────────
        /// ReferenceIntersector 可能返回多个命中（如楼板底面和顶面均命中），
        /// 取最小 Proximity 即为最近障碍物，对应实际净高。
        /// </summary>
        /// <param name="intersector">已配置好类别过滤器的射线检测器</param>
        /// <param name="origin">射线起点（世界坐标，英尺）</param>
        /// <returns>最近命中距离（英尺）；无命中时返回 -1</returns>
        private static double CastRayUp(ReferenceIntersector intersector, XYZ origin)
        {
            IList<ReferenceWithContext> hits = intersector.Find(origin, XYZ.BasisZ);
            if (hits == null || hits.Count == 0)
                return -1; // 上方无遮挡

            double minProximity = double.MaxValue;
            foreach (var ctx in hits)
            {
                // 过滤自命中（Proximity 极小）；保留真实障碍物
                if (ctx.Proximity > 1e-6 && ctx.Proximity < minProximity)
                    minProximity = ctx.Proximity;
            }
            // 若所有命中都被过滤掉（全为自命中），返回 -1 表示无遮挡
            return minProximity == double.MaxValue ? -1 : minProximity;
        }

        /// <summary>
        /// 将英尺净高转换为毫米并四舍五入到整数，
        /// 以便与规范毫米阈值直接比较。
        ///
        /// ── 哨兵值透传 ────────────────────────────────────────────────
        /// ft &lt; 0 时（即 CastRayUp 返回 -1，表示无遮挡）直接返回 -1，
        /// 不做任何换算，调用方以负值判断"无遮挡"语义。
        ///
        /// 使用 <see cref="UnitConverter.FtToMm"/> 替代原先内联的
        /// <see cref="UnitUtils.ConvertFromInternalUnits"/> 调用，保持全局统一。
        /// </summary>
        /// <param name="ft">英尺净高；负值表示无遮挡哨兵</param>
        /// <returns>整数毫米净高；负值原样返回</returns>
        private static double RoundFtToMm(double ft)
        {
            if (ft < 0)
                return -1; // 哨兵值透传，不参与单位换算
            return Math.Round(UnitConverter.FtToMm(ft), 0);
        }
    }
}
