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
                        landingDepthFt: landingDepthFt,
                        clockwise: clockwise,
                        baseElevFt: adjustedBaseLevel.Elevation,
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

                        // ── 自动生成休息平台 ──────────────────────────────────
                        // Run1 顶 = 平台面 = Run2 底，几何完全对齐时此调用成功
                        StairsLanding.CreateAutomaticLanding(doc, run1.Id, run2.Id);
                        // 在 Execute() 内临时加入，生成楼梯后立即运行
                        // ================================================================
                        //  获取 StairsLanding 全量参数 —— 五种方法完整示例
                        //  放置位置：StairsLanding.CreateAutomaticLanding() 调用之后、tx.Commit() 之前
                        // ================================================================

                        StairsLanding landing = doc.GetElement(
                            (doc.GetElement(stairsId) as Stairs).GetStairsLandings().First()
                        ) as StairsLanding;

                        StairsLandingType landingType =
                            doc.GetElement(landing.GetTypeId()) as StairsLandingType;

                        // ── 共用格式化辅助（局部函数）────────────────────────────────────
                        string FormatValue(Parameter p)
                        {
                            return p.StorageType switch
                            {
                                StorageType.Double => $"{p.AsDouble():F6}  ({UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters):F1} mm)",
                                StorageType.Integer => p.AsInteger().ToString(),
                                StorageType.String => p.AsString() ?? "(null)",
                                StorageType.ElementId => $"ElementId={p.AsElementId().IntegerValue}",
                                _ => "—"
                            };
                        }

                        // ════════════════════════════════════════════════════════════════
                        //  方法一：遍历 Element.Parameters（全量实例参数）
                        // ════════════════════════════════════════════════════════════════
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine($"=== StairsLanding {landing.Id} · 方法一：全量实例参数 ===\n");

                            foreach (Parameter p in landing.Parameters
                                .Cast<Parameter>()
                                .OrderBy(p => p.Definition.Name))
                            {
                                sb.AppendLine(
                                    $"[{p.Definition.Name}]" +
                                    $"\n    类型={p.StorageType,-12}" +
                                    $"  只读={p.IsReadOnly}" +
                                    $"\n    值  ={FormatValue(p)}\n");
                            }

                            TaskDialog.Show("方法一：全量实例参数", sb.ToString());
                        }

                        // ════════════════════════════════════════════════════════════════
                        //  方法二：区分实例参数 vs 类型参数
                        // ════════════════════════════════════════════════════════════════
                        {
                            var sb = new StringBuilder();

                            // ── 实例参数 ─────────────────────────────────────────────────
                            sb.AppendLine($"=== StairsLanding {landing.Id} · 方法二：实例参数 ===\n");
                            foreach (Parameter p in landing.Parameters
                                .Cast<Parameter>()
                                .OrderBy(p => p.Definition.Name))
                            {
                                sb.AppendLine($"[{p.Definition.Name}]  只读={p.IsReadOnly}  {FormatValue(p)}");
                            }

                            // ── 类型参数 ─────────────────────────────────────────────────
                            sb.AppendLine($"\n=== StairsLandingType {landingType?.Id} · 方法二：类型参数 ===\n");
                            if (landingType != null)
                            {
                                foreach (Parameter p in landingType.Parameters
                                    .Cast<Parameter>()
                                    .OrderBy(p => p.Definition.Name))
                                {
                                    sb.AppendLine($"[{p.Definition.Name}]  只读={p.IsReadOnly}  {FormatValue(p)}");
                                }
                            }
                            else
                            {
                                sb.AppendLine("（未找到对应的 StairsLandingType）");
                            }

                            TaskDialog.Show("方法二：实例 + 类型参数", sb.ToString());
                        }

                        // ════════════════════════════════════════════════════════════════
                        //  方法三：GetOrderedParameters()（按 UI 属性面板顺序）
                        // ════════════════════════════════════════════════════════════════
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine($"=== StairsLanding {landing.Id} · 方法三：UI 顺序参数 ===\n");

                            IList<Parameter> ordered = landing.GetOrderedParameters();
                            for (int i = 0; i < ordered.Count; i++)
                            {
                                Parameter p = ordered[i];
                                sb.AppendLine(
                                    $"#{i + 1:D2}  [{p.Definition.Name}]" +
                                    $"  只读={p.IsReadOnly}" +
                                    $"  {FormatValue(p)}");
                            }

                            TaskDialog.Show("方法三：UI 顺序参数", sb.ToString());
                        }

                        // ════════════════════════════════════════════════════════════════
                        //  方法四：筛选「可写的 Double 参数」（最直接可操作）
                        // ════════════════════════════════════════════════════════════════
                        {
                            var sb = new StringBuilder();

                            // ── 实例层可写 Double ─────────────────────────────────────────
                            sb.AppendLine($"=== StairsLanding {landing.Id} · 方法四：可写 Double 参数 ===\n");
                            var writableDoubles = landing.Parameters
                                .Cast<Parameter>()
                                .Where(p => p.StorageType == StorageType.Double && !p.IsReadOnly)
                                .OrderBy(p => p.Definition.Name)
                                .ToList();

                            if (writableDoubles.Count == 0)
                                sb.AppendLine("（实例层无可写 Double 参数）");
                            else
                                foreach (Parameter p in writableDoubles)
                                {
                                    double mm = UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters);
                                    sb.AppendLine($"[{p.Definition.Name}]  {mm:F1} mm  （内部值={p.AsDouble():F6} ft）");
                                }

                            // ── 类型层可写 Double ─────────────────────────────────────────
                            sb.AppendLine($"\n=== StairsLandingType · 方法四：类型层可写 Double 参数 ===\n");
                            if (landingType != null)
                            {
                                var typeDoubles = landingType.Parameters
                                    .Cast<Parameter>()
                                    .Where(p => p.StorageType == StorageType.Double && !p.IsReadOnly)
                                    .OrderBy(p => p.Definition.Name)
                                    .ToList();

                                if (typeDoubles.Count == 0)
                                    sb.AppendLine("（类型层无可写 Double 参数）");
                                else
                                    foreach (Parameter p in typeDoubles)
                                    {
                                        double mm = UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters);
                                        sb.AppendLine($"[{p.Definition.Name}]  {mm:F1} mm  （内部值={p.AsDouble():F6} ft）");
                                    }
                            }

                            TaskDialog.Show("方法四：可写 Double 参数", sb.ToString());
                        }

                        // ════════════════════════════════════════════════════════════════
                        //  方法五：通过 BuiltInParameter 精确访问已知参数
                        //
                        //  StairsLanding 目前无专属的 LANDING_* 枚举，
                        //  以下列出实际可命中的通用枚举，其余需通过方法一确认名称后
                        //  改用 LookupParameter("参数名") 访问。
                        // ════════════════════════════════════════════════════════════════
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine($"=== StairsLanding {landing.Id} · 方法五：BuiltInParameter 精确访问 ===\n");

                            // 已知可用于 StairsLanding 的 BuiltInParameter
                            var knownParams = new (string Label, BuiltInParameter Bip)[]
                            {
        ("所需踢面数（楼梯级）",   BuiltInParameter.STAIRS_DESIRED_NUMBER_OF_RISERS),
        ("实际踏面深度",           BuiltInParameter.STAIRS_ACTUAL_TREAD_DEPTH),
        ("实际踢面高度",           BuiltInParameter.STAIRS_ACTUAL_RISER_HEIGHT),
        ("注释",                   BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS),
        ("标记",                   BuiltInParameter.ALL_MODEL_MARK),
        ("阶段-创建",             BuiltInParameter.PHASE_CREATED),
        ("阶段-拆除",             BuiltInParameter.PHASE_DEMOLISHED),
                            };

                            foreach (var (label, bip) in knownParams)
                            {
                                Parameter p = landing.get_Parameter(bip);
                                if (p != null)
                                    sb.AppendLine($"[{label}]  {FormatValue(p)}  只读={p.IsReadOnly}");
                                else
                                    sb.AppendLine($"[{label}]  → 此元素上不存在该 BIP");
                            }

                            // ── 通过名称查找（方法一确认名称后的生产写法）────────────────
                            sb.AppendLine("\n── 按名称查找示例（替换为方法一中找到的真实参数名）──");

                            // 常见候选名称，运行后按实际输出修正
                            string[] candidateNames =
                            {
        "厚度", "宽度", "深度",
        "Thickness", "Width", "Depth",
        "结构厚度", "Structural Depth"
    };

                            foreach (string name in candidateNames)
                            {
                                Parameter p = landing.LookupParameter(name);
                                sb.AppendLine(p != null
                                    ? $"LookupParameter(\"{name}\")  → 找到！{FormatValue(p)}  只读={p.IsReadOnly}"
                                    : $"LookupParameter(\"{name}\")  → 不存在");
                            }

                            TaskDialog.Show("方法五：BuiltInParameter 精确访问", sb.ToString());
                        }
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