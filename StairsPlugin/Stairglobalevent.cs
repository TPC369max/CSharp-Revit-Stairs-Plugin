using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using StairsPlugin.Model;
using StairsPlugin.ViewModel;
using System;

namespace StairsPlugin
{
    // ================================================================
    //  外部事件处理器
    //
    //  职责：
    //    接收 ViewModel 的参数快照，在 Revit 上下文中执行楼梯生成事务。
    //    由 ExternalEvent.Raise() 异步触发，彻底解除与 WPF 消息循环的冲突。
    //
    //  生命周期：
    //    由 CommandStairGenerator.Execute() 一次性创建，随后注入 ViewModel。
    //    窗口关闭后，ExternalEvent 对象会被 GC，Handler 随之释放。
    // ================================================================
    public class StairGlobalEventHandler : IExternalEventHandler
    {
        // ViewModel 引用：每次 Raise 前由 ViewModel.OnGenerate() 写入自身
        public ViewModel.ViewModel ViewModel
        {
            get; set;
        }

        // UIDocument：在 CommandStairGenerator 中注入，供拾取视图使用
        public UIDocument UIDoc
        {
            get; set;
        }

        public string GetName() => "StairGeneratorEvent";

        // ================================================================
        //  核心：生成事务（原 CommandStairGenerator 步骤 3-6）
        // ================================================================
        public void Execute(UIApplication app)
        {
            if (ViewModel == null)
                return;

            var vm = ViewModel;
            Document doc = app.ActiveUIDocument.Document;

            try
            {
                // ── 读取生成参数 ──────────────────────────────────────────
                Level baseLevel = vm.SelectedBaseLevel;
                Level topLevel = vm.SelectedTopLevel;

                if (baseLevel == null || topLevel == null)
                {
                    TaskDialog.Show("错误", "标高参数无效，请重新选择。");
                    return;
                }

                XYZ insertionPt = vm.P1;
                double angleRad = vm.DirectionAngleRad;
                bool clockwise = vm.IsClockwise;

                double runWidthFt = MmToFt(vm.RunWidthMm);
                double treadDepthFt = MmToFt(vm.TreadDepthMm);
                double wellWidthFt = MmToFt(vm.WellWidthMm);
                double baseOffsetFt = MmToFt(vm.BaseOffsetMm);

                double totalHeightFt = topLevel.Elevation - baseLevel.Elevation + baseOffsetFt;
                double totalHeightMm = FtToMm(totalHeightFt);

                // ── 由水平约束推导踏步级数（与 ViewModel.Recalculate 保持一致）──
                // P1→P2 距离 = TotalSteps × TreadDepth + LandingDepth
                double p1p2Mm = FtToMm(vm.P1.DistanceTo(vm.P2));
                int totalSteps = vm.TreadDepthMm > 0
                    ? (int)Math.Floor((p1p2Mm - vm.LandingDepthMm)*2 / vm.TreadDepthMm)
                    : 0;
                // 双跑楼梯每跑整数级，总步数必须为偶数
                if (totalSteps % 2 != 0)
                    totalSteps -= 1;

                if (totalSteps <= 0)
                {
                    TaskDialog.Show("错误", "踏步级数解算为零，请检查 P1P2 距离与踏步宽设置。");
                    return;
                }

                // 踏步解算：级数由水平约束固定，踢面高由垂直高度推导
                var calcResult = StairCalculator.Calculate(totalHeightMm, totalSteps, vm.CurrentRule);

                double riserFt = MmToFt(calcResult.RiserHeight);

                // ── 楼梯生成事务 ──────────────────────────────────────────
                ElementId stairsId = ElementId.InvalidElementId;
                ElementId run1Id = ElementId.InvalidElementId;

                using (var scope = new StairsEditScope(doc, "自动生成双跑楼梯"))
                {
                    stairsId = scope.Start(baseLevel.Id, topLevel.Id);

                    using (var tx = new Transaction(doc, "绘制梯段与平台"))
                    {
                        tx.Start();

                        Stairs stairs = doc.GetElement(stairsId) as Stairs;

                        stairs.get_Parameter(BuiltInParameter.STAIRS_DESIRED_NUMBER_OF_RISERS)
                              .Set(calcResult.TotalSteps);
                        stairs.get_Parameter(BuiltInParameter.STAIRS_ACTUAL_TREAD_DEPTH)
                              .Set(treadDepthFt);

                        // ── 局部坐标系定义 ────────────────────────────────────────
                        //   angleRad = Atan2(P2.Y-P1.Y, P2.X-P1.X)，即 P1→P2 与世界 X 轴的夹角。
                        //   CreateRotation(Z, angleRad) 作用后：
                        //     局部 X 轴 → 世界 P1→P2 方向   ← 对称轴 / 爬升方向
                        //     局部 Y 轴 → 世界垂直于 P1→P2  ← 梯段侧向偏移方向
                        //   原点 = P1（insertionPt）
                        //
                        //   两跑中心线到对称轴（局部 X 轴）的 Y 向距离均为：
                        //     halfY = (梯井宽 + 梯段净宽) / 2
                        //
                        //   右旋（顺时针）：站在 P1 朝 P2 看，第一跑在右侧（局部 -Y），
                        //                   第二跑在左侧（局部 +Y），方向相反（局部 -X 走）。
                        //   左旋取反。
                        //
                        //   验证：P1(0,0) P2(0,6)，angleRad=π/2，右旋，halfY=(well+run)/2
                        //     局部(-Y) 经旋转 π/2 → 世界 +X  ✓（run1 偏向世界 +X 侧）
                        //     run1 沿局部 X 从 0→run1Length，旋转后沿世界 Y 爬升 ✓

                        double run1Length = (calcResult.Run1Steps - 1) * treadDepthFt;
                        double run2Length = (calcResult.Run2Steps - 1) * treadDepthFt;

                        // 每跑中心线到对称轴（局部 X 轴）的 Y 向偏移
                        double halfY = (wellWidthFt + runWidthFt) / 2.0;

                        // 右旋：run1 在 -Y 侧（右侧），run2 在 +Y 侧（左侧）；左旋取反
                        double run1Y = clockwise ? -halfY : halfY;
                        double run2Y = clockwise ? halfY : -halfY;

                        // 第一跑：沿局部 X 轴从 P1 端爬升至休息平台端
                        XYZ run1LocalStart = new XYZ(0, run1Y, 0);
                        XYZ run1LocalEnd = new XYZ(run1Length, run1Y, 0);

                        // 第二跑：End 与run1LocalEnd对齐（）
                        XYZ run2LocalStart = new XYZ(run2Length, run2Y, 0);
                        XYZ run2LocalEnd = new XYZ(0, run2Y, 0);

                        // ── 应用旋转变换（P1→P2 向量法算出的 angleRad）──
                        var rotate = Transform.CreateRotation(XYZ.BasisZ, angleRad);
                        var translate = Transform.CreateTranslation(insertionPt);
                        var transform = translate.Multiply(rotate);

                        XYZ run1Start = transform.OfPoint(run1LocalStart);
                        XYZ run1End = transform.OfPoint(run1LocalEnd);
                        XYZ run2Start = transform.OfPoint(run2LocalStart);
                        XYZ run2End = transform.OfPoint(run2LocalEnd);

                        // ── 创建第一跑 ──
                        StairsRun run1 = StairsRun.CreateStraightRun(
                            doc, stairsId,
                            Line.CreateBound(run1Start, run1End),
                            StairsRunJustification.Center);
                        run1.ActualRunWidth = runWidthFt;
                        run1Id = run1.Id;

                        // ── 创建第二跑 ──
                        StairsRun run2 = StairsRun.CreateStraightRun(
                            doc, stairsId,
                            Line.CreateBound(run2Start, run2End),
                            StairsRunJustification.Center);
                        run2.ActualRunWidth = runWidthFt;

                        double run1HeightFt = calcResult.Run1Steps * riserFt;
                        run2.get_Parameter(BuiltInParameter.STAIRS_RUN_TOP_ELEVATION)
                            .Set(totalHeightFt);
                        run2.get_Parameter(BuiltInParameter.STAIRS_RUN_BOTTOM_ELEVATION)
                            .Set(run1HeightFt);

                        doc.Regenerate();

                        // ── 自动生成休息平台 ──
                        StairsLanding.CreateAutomaticLanding(doc, run1.Id, run2.Id);

                        tx.Commit();
                    }

                    scope.Commit(new StairsFailurePreprocessor());
                }

                // ── 净空合规校验（可选）────────────────────────────────────
                if (vm.EnableClearCheck == true)
                {
                    RunClearanceCheck(insertionPt, calcResult, riserFt, treadDepthFt,
                        angleRad, topLevel.Elevation, vm.CurrentRule.MinClearHeight);
                }

                // ── 生成完成提示 ────────────────────────────────────────────
                Stairs newStairs = doc.GetElement(stairsId) as Stairs;
                StairsRun finalRun = doc.GetElement(run1Id) as StairsRun;

                TaskDialog.Show("生成完成",
                    $"楼梯 ID：{newStairs.Id.IntegerValue}\n" +
                    $"起始标高：{baseLevel.Name}  终止标高：{topLevel.Name}\n" +
                    $"总踏步数：{newStairs.ActualRisersNumber} 级\n" +
                    $"踢面高：{FtToMm(newStairs.ActualRiserHeight):F1} mm" +
                    $"梯段净宽：{FtToMm(finalRun.ActualRunWidth):F0} mm" +
                    $"方向角 θ = {angleRad * 180 / Math.PI:F1}°");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("生成失败", ex.Message);
            }
        }

        // ================================================================
        //  净空校验（调用 Model 层 ClearanceChecker）
        // ================================================================
        private void RunClearanceCheck(
            XYZ insertionPt,
            StairCalculationResult calcResult,
            double riserFt,
            double treadDepthFt,
            double angleRad,
            double topLevelElevFt,
            double minClearHeightMm)
        {
            var result = ClearanceChecker.Check(
                insertionPt,
                calcResult.TotalSteps,
                riserFt,
                treadDepthFt,
                angleRad,
                topLevelElevFt,
                minClearHeightMm);

            if (!result.IsCompliant)
            {
                TaskDialog.Show("净空预警",
                    $"⚠ {result.WarningMessage}\n\n" +
                    "建议调整层高或踏步参数后重新生成。");
            }
        }

        // ================================================================
        //  单位换算辅助
        // ================================================================
        private static double MmToFt(double mm)
            => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        private static double FtToMm(double ft)
            => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
    }
}