using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using StairsPlugin.Model;     // CoordinateTransform, ClearanceChecker
using StairsPlugin.Utils;
using StairsPlugin.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Data;

namespace StairsPlugin
{
    // ================================================================
    //  外部事件处理器
    //
    //  职责：
    //    接收 ViewModel 的参数快照，在 Revit 上下文中执行楼梯生成事务。
    //    由 ExternalEvent.Raise() 异步触发，彻底解除与 WPF 消息循环的冲突。
    //
    //  净空校验时序：
    //    ① 在 StairsEditScope 启动前，利用 ReferenceIntersector 射线法
    //       对推算坐标进行预检（无需楼梯实体存在）。
    //    ② 不合规时弹出 Yes/No 对话框，用户确认后方可继续；
    //       选"否"则直接中止，不产生任何 Revit 模型变更。
    //
    //  重构说明（相对上一版本）：
    //    • 移除底部私有方法 MmToFt / FtToMm，改用 UnitConverter（统一换算）
    //    • 坐标变换矩阵构建改用 CoordinateTransform.CreateStairTransform，
    //      与 ClearanceChecker 共享同一套定义，消除潜在的坐标偏差风险
    // ================================================================
    public class StairGlobalEventHandler : IExternalEventHandler
    {
        /// <summary>ViewModel 引用：每次 Raise 前由 ViewModel.OnGenerate() 写入</summary>
        public ViewModel.ViewModel ViewModel
        {
            get; set;
        }

        /// <summary>UIDocument：在 CommandStairGenerator 中注入</summary>
        public UIDocument UIDoc
        {
            get; set;
        }

        public string GetName() => "StairGeneratorEvent";

        // ================================================================
        //  核心：楼梯生成事务（含净空预检）
        // ================================================================
        public void Execute(UIApplication app)
        {
            if (ViewModel == null)
                return;

            var vm = ViewModel;
            Document doc = app.ActiveUIDocument.Document;

            try
            {
                // ── 读取标高参数 ──────────────────────────────────────────
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

                // ── 修正插入点的 Z 值 ──────────────────────────────────────
                // 平面视图中拾取的 P1 其 Z 值由视图截面高程决定，不可直接使用。
                // 以底部标高高程 + 底部偏移作为正确的 Z 原点，
                // 保证梯段起点严格落在用户指定的楼层面上。
                double baseOffsetFt = UnitConverter.MmToFt(vm.BaseOffsetMm);
                double adjustedElevFt = baseLevel.Elevation + baseOffsetFt;
                XYZ insertionPtCorrected = new XYZ(
                    insertionPt.X,
                    insertionPt.Y,
                    adjustedElevFt);

                // ── 在 StairsEditScope 外创建偏移标高 ────────────────────
                ElementId tempLevelId = ElementId.InvalidElementId;
                Level adjustedBaseLevel = null;

                using (var txLevel = new Transaction(doc, "创建偏移标高"))
                {
                    txLevel.Start();

                    // 若已有误差 < 1mm 的标高则复用，避免重复创建
                    adjustedBaseLevel = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .FirstOrDefault(l =>
                            Math.Abs(l.Elevation - adjustedElevFt) < UnitConverter.MmToFt(1.0));

                    if (adjustedBaseLevel == null)
                    {
                        adjustedBaseLevel = Level.Create(doc, adjustedElevFt);
                        adjustedBaseLevel.Name = $"_TempStairBase_{DateTime.Now:HHmmss}";
                        tempLevelId = adjustedBaseLevel.Id;
                    }

                    txLevel.Commit();
                }

                double runWidthFt = UnitConverter.MmToFt(vm.RunWidthMm);
                double treadDepthFt = UnitConverter.MmToFt((double)vm.ActualTreadDepthMm);
                double wellWidthFt = UnitConverter.MmToFt(vm.WellWidthMm);
                double landingDepthFt = UnitConverter.MmToFt(vm.LandingDepthMm);


                // ── 读取 ViewModel 已解算的踏步结果快照 ──────────────────
                var calcResult = vm.CalcResult;
                if (calcResult == null || calcResult.TotalSteps <= 0)
                {
                    TaskDialog.Show("错误", "踏步级数解算为零，请检查 P1P2 距离与踏步宽设置。");
                    return;
                }

                double riserFt = UnitConverter.MmToFt(calcResult.RiserHeight);

                // ════════════════════════════════════════════════════════
                //  ★ 净空预检（在任何事务启动前执行）
                //
                //  以推算坐标为射线起点向上投射，命中楼板/梁底面后做差，
                //  不依赖已生成的楼梯实体，支持"前置拦截"。
                //
                //  阈值（GB55031-2022 §5.3.9）：
                //    梯段净高 ≥ 2200 mm    平台净高 ≥ 2000 mm
                // ════════════════════════════════════════════════════════
                if (vm.EnableClearCheck)
                {
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
                        baseElevFt: adjustedBaseLevel.Elevation,
                        landingDepthFt: landingDepthFt,
                        minClearStepMm: 2200,
                        minClearLandingMm: 2000);

                    if (!clearResult.IsCompliant)
                    {
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
                            "选\"是\"将忽略净空预警并继续生成；\n" +
                            "选\"否\"将中止生成，请调整参数后重试。";
                        td.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                        td.DefaultButton = TaskDialogResult.No;

                        if (td.Show() != TaskDialogResult.Yes)
                            return;
                    }
                    else if (view3D != null)
                    {
                        string stepInfo = clearResult.MinStepClearanceMm >= 0
                            ? $"{clearResult.MinStepClearanceMm:F0} mm" : "无遮挡";
                        string landingInfo = clearResult.MinLandingClearanceMm >= 0
                            ? $"{clearResult.MinLandingClearanceMm:F0} mm" : "无遮挡";

                        TaskDialog.Show("净空校验通过",
                            $"✓ 梯段最小净高：{stepInfo}（≥ 2200 mm）\n" +
                            $"✓ 平台最小净高：{landingInfo}（≥ 2000 mm）\n\n" +
                            "净空满足规范要求，继续生成。");
                    }
                }

                // ════════════════════════════════════════════════════════
                //  以下执行楼梯生成事务
                // ════════════════════════════════════════════════════════

                ElementId stairsId = ElementId.InvalidElementId;
                ElementId run1Id = ElementId.InvalidElementId;

                using (var scope = new StairsEditScope(doc, "自动生成双跑楼梯"))
                {
                    stairsId = scope.Start(adjustedBaseLevel.Id, topLevel.Id);

                    using (var tx = new Transaction(doc, "绘制梯段与平台"))
                    {
                        tx.Start();

                        Stairs stairs = doc.GetElement(stairsId) as Stairs;
                        stairs.get_Parameter(BuiltInParameter.STAIRS_DESIRED_NUMBER_OF_RISERS)
                              .Set(calcResult.TotalSteps + 2);
                        stairs.get_Parameter(BuiltInParameter.STAIRS_ACTUAL_TREAD_DEPTH)
                              .Set(treadDepthFt);

                        double run1HeightFt = (calcResult.Run1Steps + 1) * riserFt;
                        double run1Length = calcResult.Run1Steps * treadDepthFt;
                        double run2Length = calcResult.Run2Steps * treadDepthFt;

                        double halfY = (wellWidthFt + runWidthFt) / 2.0;
                        double run1Y = clockwise ? -halfY : halfY;
                        double run2Y = clockwise ? halfY : -halfY;

                        // ── 局部坐标系中的梯段端点 ─────────────────────────────
                        // Run1：从局部原点沿 +X 方向爬升，Z=0（相对插入点高程）
                        // Run2：从平台端（局部 X = run2Length）向插入点方向逆行，
                        //       Z = run1HeightFt（平台顶高程，相对局部原点）
                        XYZ run1LocalStart = new XYZ(0, run1Y, 0);
                        XYZ run1LocalEnd = new XYZ(run1Length, run1Y, 0);
                        XYZ run2LocalStart = new XYZ(run2Length, run2Y, run1HeightFt);
                        XYZ run2LocalEnd = new XYZ(0, run2Y, run1HeightFt);

                        // ── 变换到世界坐标 ────────────────────────────────────
                        // 使用 CoordinateTransform 替代原先内联的 rotate × translate 乘法
                        Transform transform = CoordinateTransform.CreateStairTransform(
                            insertionPtCorrected, angleRad);

                        XYZ run1Start = transform.OfPoint(run1LocalStart);
                        XYZ run1End = transform.OfPoint(run1LocalEnd);
                        XYZ run2Start = transform.OfPoint(run2LocalStart);
                        XYZ run2End = transform.OfPoint(run2LocalEnd);

                        // ── 创建第一跑 ──────────────────────────────────────────
                        StairsRun run1 = StairsRun.CreateStraightRun(
                            doc, stairsId,
                            Line.CreateBound(run1Start, run1End),
                            StairsRunJustification.Center);
                        run1.ActualRunWidth = runWidthFt;
                        run1Id = run1.Id;

                        // ── 创建第二跑 ──────────────────────────────────────────
                        StairsRun run2 = StairsRun.CreateStraightRun(
                            doc, stairsId,
                            Line.CreateBound(run2Start, run2End),
                            StairsRunJustification.Center);
                        run2.ActualRunWidth = runWidthFt;

                        doc.Regenerate();

                        // ══════════════════════════════════════════════════════
                        //  草图平台生成（CreateSketchedLanding）
                        //
                        //  几何说明（U 形双跑楼梯局部坐标系）：
                        //
                        //    run1：从 X=0 沿 +X 爬升至 X=run1Length，Y=run1Y（中心线）
                        //    run2：从 X=run2Length 沿 -X 爬升至 X=0，  Y=run2Y（中心线）
                        //
                        //    平台位于两跑远端（X 方向）：
                        //      X 范围 = [min(run1Length,run2Length),
                        //                max(run1Length,run2Length)]
                        //      当两跑等长时 X 差值为零，取 landingDepthFt 保底；
                        //      否则取实际差值与 landingDepthFt 中的较大值。
                        //
                        //      Y 范围 = 梯段外边缘到外边缘（含两侧梯段净宽）
                        //        yMin = min(run1Y, run2Y) − runWidthFt / 2
                        //        yMax = max(run1Y, run2Y) + runWidthFt / 2
                        //
                        //    平台底面高程（绝对，英尺）：
                        //      landingElevFt = adjustedBaseLevel.Elevation
                        //                    + (run1Steps + 1) × riserFt
                        //      （Run1 共 run1Steps+1 个踢面，+1 来自楼梯整体首尾各加一级）
                        //
                        //  CurveLoop 顶点顺序（俯视逆时针）：
                        //    c0(xMin,yMin) → c1(xMax,yMin) → c2(xMax,yMax) → c3(xMin,yMax)
                        //  Revit 对草图平台不强制绕向，保持逆时针符合 Revit 通用约定。
                        // ══════════════════════════════════════════════════════

                        double landingElevFt =  (calcResult.Run1Steps + 1) * riserFt;

                        // ── X 范围：以两跑远端差值为基础，保证不小于 landingDepthFt ──
                        double xMin = Math.Min(run1Length, run2Length);
                        double xMax = Math.Max(run1Length, run2Length);
                        if (xMax - xMin < landingDepthFt)
                            xMax = xMin + landingDepthFt;

                        // ── Y 范围：梯段中心线 ± 半宽，取两跑外侧边缘 ──────────────
                        double yMin = Math.Min(run1Y, run2Y) - runWidthFt / 2.0;
                        double yMax = Math.Max(run1Y, run2Y) + runWidthFt / 2.0;

                        // ── 四角点：局部坐标 → 世界坐标（Z 直接赋绝对高程）─────────
                        XYZ c0 = CoordinateTransform.LocalToWorld(transform, xMin, yMin, landingElevFt);
                        XYZ c1 = CoordinateTransform.LocalToWorld(transform, xMax, yMin, landingElevFt);
                        XYZ c2 = CoordinateTransform.LocalToWorld(transform, xMax, yMax, landingElevFt);
                        XYZ c3 = CoordinateTransform.LocalToWorld(transform, xMin, yMax, landingElevFt);

                        // ── 构建闭合 CurveLoop（逆时针，俯视）──────────────────────
                        var landingLoop = new CurveLoop();
                        landingLoop.Append(Line.CreateBound(c0, c1));
                        landingLoop.Append(Line.CreateBound(c1, c2));
                        landingLoop.Append(Line.CreateBound(c2, c3));
                        landingLoop.Append(Line.CreateBound(c3, c0));

                        // ── 创建草图平台 ─────────────────────────────────────────
                        StairsLanding landing = StairsLanding.CreateSketchedLanding(
                            doc,
                            stairsId,
                            landingLoop,
                            landingElevFt);

                        // ── 调试：打印平台可写 Double 参数 ──────────────────────
                        var sb = new StringBuilder();
                        sb.AppendLine($"=== StairsLanding {landing.Id} 可写参数 ===\n");
                        sb.AppendLine($"平台底面高程：{UnitConverter.FtToMm(landingElevFt):F1} mm\n");
                        sb.AppendLine($"平台轮廓（局部坐标，mm）：");
                        sb.AppendLine($"  X [{UnitConverter.FtToMm(xMin):F1}, {UnitConverter.FtToMm(xMax):F1}]");
                        sb.AppendLine($"  Y [{UnitConverter.FtToMm(yMin):F1}, {UnitConverter.FtToMm(yMax):F1}]\n");

                        foreach (Parameter p in landing.Parameters.Cast<Parameter>()
                            .Where(p => p.StorageType == StorageType.Double && !p.IsReadOnly)
                            .OrderBy(p => p.Definition.Name))
                        {
                            double valueMm = UnitUtils.ConvertFromInternalUnits(
                                p.AsDouble(), UnitTypeId.Millimeters);
                            sb.AppendLine($"[{p.Definition.Name}]  {valueMm:F1} mm");
                        }

                        TaskDialog.Show("可写 Double 参数", sb.ToString());
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

                // ── 整理临时标高 ────────────────────────────────────────────
                if (tempLevelId != ElementId.InvalidElementId)
                {
                    using (var txRename = new Transaction(doc, "整理临时标高"))
                    {
                        txRename.Start();

                        Level tempLv = doc.GetElement(tempLevelId) as Level;
                        tempLv.Name = $"{baseLevel.Name}_偏移{vm.BaseOffsetMm:F0}mm";

                        foreach (var view in new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewPlan))
                            .Cast<ViewPlan>()
                            .Where(v => !v.IsTemplate))
                        {
                            try
                            {
                                view.HideElements(new List<ElementId> { tempLevelId });
                            }
                            catch { /* 部分视图不支持隐藏，跳过 */ }
                        }

                        txRename.Commit();
                    }
                }

                // ── 生成完成提示 ────────────────────────────────────────────
                Stairs newStairs = doc.GetElement(stairsId) as Stairs;
                StairsRun finalRun = doc.GetElement(run1Id) as StairsRun;

                TaskDialog.Show("生成完成",
                    $"楼梯 ID：{newStairs.Id.IntegerValue}\n" +
                    $"起始标高：{baseLevel.Name}  终止标高：{topLevel.Name}\n" +
                    $"总踏步数：{newStairs.ActualRisersNumber} 级\n" +
                    $"踢面高：{UnitConverter.FtToMm(newStairs.ActualRiserHeight):F1} mm\n" +
                    $"梯段净宽：{UnitConverter.FtToMm(finalRun.ActualRunWidth):F0} mm\n" +
                    $"方向角 θ = {angleRad * 180 / Math.PI:F1}°");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("生成失败", ex.Message);
            }
        }
    }
}