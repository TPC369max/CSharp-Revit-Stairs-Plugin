using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using StairsPlugin.Model;
using StairsPlugin.ViewModel;
using System;
using System.Linq;

namespace StairsPlugin
{
    // ================================================================
    //  外部事件处理器
    //
    //  职责：
    //    接收 ViewModel 的参数快照，在 Revit 上下文中执行楼梯生成事务。
    //    由 ExternalEvent.Raise() 异步触发，彻底解除与 WPF 消息循环的冲突。
    //
    //  净空校验时序（2025 重构）：
    //    ① 在 StairsEditScope 启动前，利用 ReferenceIntersector 射线法
    //      对推算坐标进行预检（无需实体存在）。
    //    ② 不合规时弹出 Yes/No 对话框，用户确认后方可继续生成；
    //      选"否"则直接中止，不产生任何 Revit 模型变更。
    //    ③ 生成完成后不再重复校验（原尾部调用已移除）。
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
        //  核心：生成事务（含预检）
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

                // ── 应用旋转 + 平移变换 ───────────────────────────────────
                // P1 在平面视图中拾取，其 Z 值取决于视图高程，不可直接使用。
                // 以底部标高绝对高程 + 底部偏移作为正确的 Z 原点，
                // 保证梯段起点严格落在用户指定的楼层面上。
                double baseOffsetFt = MmToFt(vm.BaseOffsetMm);
                XYZ insertionPtCorrected = new XYZ(
                    insertionPt.X,
                    insertionPt.Y,
                    baseLevel.Elevation+ baseOffsetFt);   // 绝对高程（英尺）

                double runWidthFt = MmToFt(vm.RunWidthMm);
                double treadDepthFt = MmToFt((double)vm.ActualTreadDepthMm);
                double wellWidthFt = MmToFt(vm.WellWidthMm);

                // ── 读取 ViewModel 已解算的踏步结果快照 ──────────────────
                var calcResult = vm.CalcResult;
                if (calcResult == null || calcResult.TotalSteps <= 0)
                {
                    TaskDialog.Show("错误", "踏步级数解算为零，请检查 P1P2 距离与踏步宽设置。");
                    return;
                }

                double riserFt = MmToFt(calcResult.RiserHeight);

                // ════════════════════════════════════════════════════════
                //  ★ 净空预检（在任何事务启动前执行）
                //
                //  原理：以推算坐标为射线起点，向上投射射线命中楼板/梁底面，
                //  纯代数做差，不依赖已生成的楼梯实体，支持"前置拦截"。
                //
                //  阈值（GB55031-2022 §5.3.9）：
                //    梯段净高 ≥ 2200 mm
                //    平台净高 ≥ 2000 mm
                // ════════════════════════════════════════════════════════
                if (vm.EnableClearCheck)
                {
                    // 查找项目中第一个可用的非模板三维视图（ReferenceIntersector 必需）
                    View3D view3D = new FilteredElementCollector(doc)
                        .OfClass(typeof(View3D))
                        .Cast<View3D>()
                        .FirstOrDefault(v => !v.IsTemplate);

                    var clearResult = ClearanceChecker.Check(
                        doc: view3D != null ? doc : null,
                        view3D: view3D,
                        insertionPoint: insertionPtCorrected,
                        calcResult: calcResult,
                        riserHeightFt: riserFt,
                        treadDepthFt: treadDepthFt,
                        angleRad: angleRad,
                        runWidthFt: runWidthFt,
                        wellWidthFt: wellWidthFt,
                        clockwise: clockwise,
                        baseElevFt: baseLevel.Elevation,
                        minClearStepMm: 2200,   // 梯段净高下限（GB55031-2022）
                        minClearLandingMm: 2000);  // 平台净高下限

                    if (!clearResult.IsCompliant)
                    {
                        // ── 弹窗提示，由用户决定是否强制生成 ────────────────
                        string stepInfo = clearResult.MinStepClearanceMm >= 0
                            ? $"梯段最小净高：{clearResult.MinStepClearanceMm:F0} mm"
                            : "梯段：上方无遮挡";
                        string landingInfo = clearResult.MinLandingClearanceMm >= 0
                            ? $"平台最小净高：{clearResult.MinLandingClearanceMm:F0} mm"
                            : "平台：上方无遮挡";

                        var td = new TaskDialog("净空合规预警");
                        td.MainInstruction = "⚠ 检测到净高不满足规范要求，是否仍然生成楼梯？";
                        td.MainContent =
                            $"{clearResult.WarningMessage}\n\n" +
                            $"射线探测结果：\n  {stepInfo}\n  {landingInfo}\n\n" +
                            "选\"是\"将忽略净空预警并继续生成；\n选\"否\"将中止生成，请调整参数后重试。";
                        td.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                        td.DefaultButton = TaskDialogResult.No;

                        if (td.Show() != TaskDialogResult.Yes)
                            return;   // 用户选择中止，不产生任何模型变更
                    }
                    else if (view3D != null)
                    {
                        // 净空合规时也给出简短确认（可根据需求删去）
                        string stepInfo = clearResult.MinStepClearanceMm >= 0
                            ? $"{clearResult.MinStepClearanceMm:F0} mm"
                            : "无遮挡";
                        string landingInfo = clearResult.MinLandingClearanceMm >= 0
                            ? $"{clearResult.MinLandingClearanceMm:F0} mm"
                            : "无遮挡";

                        TaskDialog.Show("净空校验通过",
                            $"✓ 梯段最小净高：{stepInfo}（≥ 2200 mm）\n" +
                            $"✓ 平台最小净高：{landingInfo}（≥ 2000 mm）\n\n" +
                            "净空满足规范要求，继续生成。");
                    }
                }
                // ════════════════════════════════════════════════════════
                //  净空预检完毕，以下执行楼梯生成事务
                // ════════════════════════════════════════════════════════

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
                              .Set(calcResult.TotalSteps + 2);
                        stairs.get_Parameter(BuiltInParameter.STAIRS_ACTUAL_TREAD_DEPTH)
                              .Set(treadDepthFt);


                        // ── 局部坐标系定义 ────────────────────────────────────────
                        //   angleRad = Atan2(P2.Y-P1.Y, P2.X-P1.X)，即 P1→P2 与世界 X 轴的夹角。
                        //   局部 X 轴 → P1→P2 方向（爬升轴）
                        //   局部 Y 轴 → 垂直于爬升轴（侧向偏移）
                        //
                        //   右旋（顺时针）：第一跑在局部 -Y 侧，第二跑在局部 +Y 侧（方向反向）。
                        double run1Length = (calcResult.Run1Steps) * treadDepthFt;
                        double run2Length = (calcResult.Run2Steps) * treadDepthFt;

                        double halfY = (wellWidthFt + runWidthFt) / 2.0;
                        double run1Y = clockwise ? -halfY : halfY;
                        double run2Y = clockwise ? halfY : -halfY;
                        double run1Elev = (baseLevel.Elevation + topLevel.Elevation)/2.0;
                        double run1HeightFt = (calcResult.Run1Steps + 1) * riserFt + run1Elev;
                        XYZ run1LocalStart = new XYZ(0, run1Y, 0);
                        XYZ run1LocalEnd = new XYZ(run1Length, run1Y, 0);
                        XYZ run2LocalStart = new XYZ(run2Length, run2Y, run1Elev);
                        XYZ run2LocalEnd = new XYZ(0, run2Y, run1Elev);

                        var rotate = Transform.CreateRotation(XYZ.BasisZ, angleRad);
                        var translate = Transform.CreateTranslation(insertionPtCorrected);
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

                        /*
                        run1.get_Parameter(BuiltInParameter.STAIRS_RUN_TOP_ELEVATION)
                            .Set(run1HeightFt);
                        run1.get_Parameter(BuiltInParameter.STAIRS_RUN_BOTTOM_ELEVATION)
                            .Set(run1Elev);
                        */
                        // ── 创建第二跑 ──
                        StairsRun run2 = StairsRun.CreateStraightRun(
                            doc, stairsId,
                            Line.CreateBound(run2Start, run2End),
                            StairsRunJustification.Center);
                        run2.ActualRunWidth = runWidthFt;
                        /*
                        run2.get_Parameter(BuiltInParameter.STAIRS_RUN_TOP_ELEVATION)
                            .Set(topLevel.Elevation);
                        run2.get_Parameter(BuiltInParameter.STAIRS_RUN_BOTTOM_ELEVATION)
                            .Set(run1HeightFt);
                        */
                        doc.Regenerate();

                        // ── 自动生成休息平台 ──
                        //StairsLanding.CreateAutomaticLanding(doc, run1.Id, run2.Id);


                        // ★ 必须最先设置 BaseOffset，否则后续梯段端点与 Revit
                        //内部期望高度（3600mm）不一致，CreateAutomaticLanding 会报错
                        //stairs.get_Parameter(BuiltInParameter.STAIRS_BASE_OFFSET)
                        //      ?.Set(baseOffsetFt);   // 设置后 Revit 期望高 = 3600 - 50 = 3550mm ✓
                        tx.Commit();
                    }

                    scope.Commit(new StairsFailurePreprocessor());
                }

                // ── 栏杆处理（必须在 StairsEditScope 关闭后执行）──────────
                using (var txRailing = new Transaction(doc, "处理栏杆扶手"))
                {
                    txRailing.Start();

                    Stairs stairsForRailing = doc.GetElement(stairsId) as Stairs;
                    var railingIds = stairsForRailing.GetAssociatedRailings();

                    if (!vm.GenerateRailing)
                    {
                        foreach (ElementId rid in railingIds)
                            doc.Delete(rid);
                    }
                    else
                    {
                        string targetName = vm.SelectedRailingTypeName;
                        ElementType targetType = new FilteredElementCollector(doc)
                            .OfClass(typeof(ElementType))
                            .Where(e => e.GetType().Name == "RailingType"
                                     && e.Name == targetName)
                            .FirstOrDefault() as ElementType;

                        if (targetType != null)
                        {
                            foreach (ElementId rid in railingIds)
                                doc.GetElement(rid).ChangeTypeId(targetType.Id);
                        }
                    }

                    txRailing.Commit();
                }

                // ── 生成完成提示 ────────────────────────────────────────────
                Stairs newStairs = doc.GetElement(stairsId) as Stairs;
                StairsRun finalRun = doc.GetElement(run1Id) as StairsRun;

                TaskDialog.Show("生成完成",
                    $"楼梯 ID：{newStairs.Id.IntegerValue}\n" +
                    $"起始标高：{baseLevel.Name}  终止标高：{topLevel.Name}\n" +
                    $"总踏步数：{newStairs.ActualRisersNumber} 级\n" +
                    $"踢面高：{FtToMm(newStairs.ActualRiserHeight):F1} mm\n" +
                    $"梯段净宽：{FtToMm(finalRun.ActualRunWidth):F0} mm\n" +
                    $"方向角 θ = {angleRad * 180 / Math.PI:F1}°");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("生成失败", ex.Message);
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