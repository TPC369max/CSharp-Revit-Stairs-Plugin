using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using StairsPlugin.Model;
using StairsPlugin.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Documents;

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


                ElementId stairsId = ElementId.InvalidElementId;
                ElementId run1Id = ElementId.InvalidElementId;

                // ── 在 StairsEditScope 外，先用事务创建偏移标高 ────────────
                ElementId tempLevelId = ElementId.InvalidElementId;
                Level adjustedBaseLevel = null;


                // ── 应用旋转 + 平移变换 ───────────────────────────────────
                // P1 在平面视图中拾取，其 Z 值取决于视图高程，不可直接使用。
                // 以底部标高绝对高程 + 底部偏移作为正确的 Z 原点，
                // 保证梯段起点严格落在用户指定的楼层面上。
                double baseOffsetFt = MmToFt(vm.BaseOffsetMm);
                double adjustedElevFt = baseLevel.Elevation + baseOffsetFt;
                XYZ insertionPtCorrected = new XYZ(
                    insertionPt.X,
                    insertionPt.Y,
                    adjustedElevFt);   // 绝对高程（英尺）

                using (var txLevel = new Transaction(doc, "创建偏移标高"))
                {
                    txLevel.Start();

                    // 检查是否已存在误差 < 1mm 的标高，避免重复创建
                    adjustedBaseLevel = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .FirstOrDefault(l => Math.Abs(l.Elevation - adjustedElevFt)
                                             < MmToFt(1.0));

                    if (adjustedBaseLevel == null)
                    {
                        // 动态创建标高（不设置视图，避免干扰项目）
                        adjustedBaseLevel = Level.Create(doc, adjustedElevFt);
                        adjustedBaseLevel.Name = $"_TempStairBase_{DateTime.Now:HHmmss}";
                        tempLevelId = adjustedBaseLevel.Id;  // 记录 ID 以便事后删除
                    }

                    txLevel.Commit();
                }

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
    baseElevFt: adjustedBaseLevel.Elevation,  // ★ 改为偏移后的标高，与实际梯段起点一致
    minClearStepMm: 2200,
    minClearLandingMm: 2000);

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


                // ── 使用偏移标高创建楼梯 ─────────────────────────────────────
                using (var scope = new StairsEditScope(doc, "自动生成双跑楼梯"))
                {
                    // 底部用偏移标高，顶部标高不变 → 有效高 = topLevel - adjustedBase
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

                        double run1Length = (calcResult.Run1Steps) * treadDepthFt;
                        double run2Length = (calcResult.Run2Steps) * treadDepthFt;

                        // ★ 平台高度 = 第一跑踢面数 × 踢面高（相对于 insertionPtCorrected）
                        double run1Elev = (adjustedBaseLevel.Elevation + topLevel.Elevation) / 2.0;
                        double halfY = (wellWidthFt + runWidthFt) / 2.0;
                        double run1Y = clockwise ? -halfY : halfY;
                        double run2Y = clockwise ? halfY : -halfY;

                        // ★ Run1：Z=0（相对局部原点），Revit 从 insertionPtCorrected.Z 开始爬升
                        XYZ run1LocalStart = new XYZ(0, run1Y, 0);
                        XYZ run1LocalEnd = new XYZ(run1Length, run1Y, 0);

                        // ★ Run2：Z=run1HeightFt（平台顶高程，相对局部原点）
                        XYZ run2LocalStart = new XYZ(run2Length, run2Y, run1HeightFt);
                        XYZ run2LocalEnd = new XYZ(0, run2Y, run1HeightFt);

                        var rotate = Transform.CreateRotation(XYZ.BasisZ, angleRad);
                        var translate = Transform.CreateTranslation(insertionPtCorrected);
                        var transform = translate.Multiply(rotate);

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

                        // ★ Run1 顶与平台顶齐平：EndsWithRiser = False
                        //   最后一个元素是踏面，Run1顶 = 平台面，CreateAutomaticLanding 可识别
                        //var ewrParam = run1.get_Parameter(BuiltInParameter.STAIRS_RUN_END_WITH_RISER);
                        //if (ewrParam != null && !ewrParam.IsReadOnly)
                        //    ewrParam.Set(0);  // 0 = False

                        // ── 创建第二跑 ──────────────────────────────────────────
                        StairsRun run2 = StairsRun.CreateStraightRun(
                            doc, stairsId,
                            Line.CreateBound(run2Start, run2End),
                            StairsRunJustification.Center);
                        run2.ActualRunWidth = runWidthFt;
                        // Run2 保持 EndsWithRiser = True（默认），衔接顶部楼层

                        doc.Regenerate();

                        // ── 自动生成休息平台 ──────────────────────────────────
                        // Run1顶 == 平台顶 == Run2底，几何完全对齐，此调用可成功

                        StairsLanding.CreateAutomaticLanding(doc, run1.Id, run2.Id);
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

                // 不删除，改为有意义的名称，并隐藏平面视图
                if (tempLevelId != ElementId.InvalidElementId)
                {
                    using (var txRename = new Transaction(doc, "整理临时标高"))
                    {
                        txRename.Start();

                        Level tempLv = doc.GetElement(tempLevelId) as Level;
                        // 重命名为有意义的标高，而不是删除
                        tempLv.Name = $"{baseLevel.Name}_偏移{vm.BaseOffsetMm:F0}mm";

                        // 在所有平面视图中隐藏该标高线（减少干扰）
                        foreach (var view in new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewPlan))
                            .Cast<ViewPlan>()
                            .Where(v => !v.IsTemplate))
                        {
                            try
                            {
                                view.HideElements(new[] { tempLevelId }
                                    .ToList()
                                    .Select(id => id)
                                    .ToList()
                                    .Concat(new List<ElementId>())
                                    .ToList());
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