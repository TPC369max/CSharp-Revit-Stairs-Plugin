using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using StairsPlugin.Model;     // CoordinateTransform, ClearanceChecker
using StairsPlugin.Utils;
using StairsPlugin.ViewModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Data;

namespace StairsPlugin
{
    // ================================================================
    //  外部事件处理器：楼梯生成核心逻辑
    //
    //  ── 职责 ──────────────────────────────────────────────────────
    //  接收 ViewModel 的参数快照，在 Revit 合法 API 上下文中
    //  执行楼梯生成事务。
    //  由 ExternalEvent.Raise() 异步触发，彻底解除与 WPF 消息循环的耦合。
    //
    //  ── 执行时序 ──────────────────────────────────────────────────
    //  ① ViewModel.OnGenerate()
    //       将自身引用写入 Handler.ViewModel
    //       调用 ExternalEvent.Raise()
    //  ② Revit 在下一个空闲帧回调 Execute(UIApplication)
    //  ③ Execute() 内按以下顺序执行：
    //       1. 读取标高参数，修正插入点 Z 值
    //       2. 在 StairsEditScope 外创建偏移标高（独立事务）
    //       3. 净空预检（射线法，不修改模型）
    //       4. StairsEditScope 内：绘制梯段 + 草图平台（主事务）
    //       5. StairsEditScope 外：处理栏杆（独立事务）
    //       6. 整理临时标高名称（独立事务）
    //       7. 弹窗汇报生成结果
    //
    //  ── 净空校验时序说明 ──────────────────────────────────────────
    //  在 StairsEditScope 启动前执行净空预检，
    //  利用 ReferenceIntersector 射线法对推算坐标进行检测，
    //  不依赖已生成的楼梯实体，实现"前置拦截"：
    //    • 合规 → 继续生成；
    //    • 不合规 → 弹出 Yes/No 对话框，用户确认后方可继续；
    //      选"否"则直接中止，不产生任何 Revit 模型变更。
    //
    //  ── 重构说明（相对上一版本） ──────────────────────────────────
    //  • 移除底部私有方法 MmToFt / FtToMm，改用 UnitConverter 统一换算。
    //  • 坐标变换矩阵构建改用 CoordinateTransform.CreateStairTransform，
    //    与 ClearanceChecker 共享同一套定义，消除潜在坐标偏差风险。
    // ================================================================
    public class StairGlobalEventHandler : IExternalEventHandler
    {
        /// <summary>
        /// ViewModel 引用：每次 ExternalEvent.Raise() 前由 ViewModel.OnGenerate() 写入。
        /// Execute() 从此属性读取所有用户输入的参数快照。
        /// 注意：此引用在 Execute() 执行期间由 ViewModel 保证有效。
        /// </summary>
        public ViewModel.ViewModel ViewModel
        {
            get; set;
        }

        /// <summary>
        /// UIDocument 引用：在 CommandStairGenerator.Execute() 中注入，
        /// 供 Execute() 获取当前活动文档（app.ActiveUIDocument.Document）。
        /// </summary>
        public UIDocument UIDoc
        {
            get; set;
        }

        /// <summary>
        /// 返回本事件处理器的名称，用于 Revit 日志和调试标识。
        /// </summary>
        public string GetName() => "StairGeneratorEvent";

        // ================================================================
        //  核心：楼梯生成事务（含净空预检）
        //
        //  本方法由 Revit 框架在合法 API 上下文中调用，
        //  可安全使用所有 Revit DB API（事务、元素创建等）。
        // ================================================================
        public void Execute(UIApplication app)
        {
            // ViewModel 未设置时直接返回（防御性检查，正常流程不会触发）
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
                // 平面视图中拾取的 P1，其 Z 值由视图截面高程决定，不可直接信任。
                // 以"底部标高高程 + 底部偏移"作为正确的 Z 原点，
                // 保证梯段起点严格落在用户指定的楼层面（加偏移后的高度）上。
                double baseOffsetFt = UnitConverter.MmToFt(vm.BaseOffsetMm);
                double adjustedElevFt = baseLevel.Elevation + baseOffsetFt;
                XYZ insertionPtCorrected = new XYZ(
                    insertionPt.X,
                    insertionPt.Y,
                    adjustedElevFt); // 仅覆盖 Z，XY 保持用户拾取的平面位置

                // ── 在 StairsEditScope 外创建偏移标高 ────────────────────
                // Revit 楼梯（Stairs）以标高（Level）作为起始和终止基准，
                // 若用户设置了底部偏移，需要在偏移后的高程处单独创建标高。
                // 此操作必须在 StairsEditScope 打开之前完成（StairsEditScope 不允许嵌套事务）。
                ElementId tempLevelId = ElementId.InvalidElementId;
                Level adjustedBaseLevel = null;

                using (var txLevel = new Transaction(doc, "创建偏移标高"))
                {
                    txLevel.Start();

                    // 优先复用已有误差 < 1 mm 的标高，避免重复创建垃圾标高
                    adjustedBaseLevel = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .FirstOrDefault(l =>
                            Math.Abs(l.Elevation - adjustedElevFt) < UnitConverter.MmToFt(1.0));

                    if (adjustedBaseLevel == null)
                    {
                        // 未找到可复用标高，新建临时标高并记录 ID 以便后续整理
                        adjustedBaseLevel = Level.Create(doc, adjustedElevFt);
                        adjustedBaseLevel.Name = $"_TempStairBase_{DateTime.Now:HHmmss}";
                        tempLevelId = adjustedBaseLevel.Id;
                    }

                    txLevel.Commit();
                }

                // ── 将几何参数从 mm 转换为 Revit 内部单位（英尺）──────────
                double runWidthFt = UnitConverter.MmToFt(vm.RunWidthMm);
                double treadDepthFt = UnitConverter.MmToFt((double)vm.ActualTreadDepthMm);
                double wellWidthFt = UnitConverter.MmToFt(vm.WellWidthMm);
                double landingDepthFt = UnitConverter.MmToFt(vm.LandingDepthMm);

                // ── 读取 ViewModel 已解算的踏步结果快照 ──────────────────
                // CalcResult 在 ViewModel.Recalculate() 中由 StairCalculator 计算，
                // 此处直接使用，不重复计算，保证生成参数与界面预览完全一致。
                var calcResult = vm.CalcResult;
                if (calcResult == null || calcResult.TotalSteps <= 0)
                {
                    TaskDialog.Show("错误", "踏步级数解算为零，请检查 P1P2 距离与踏步宽设置。");
                    return;
                }

                double riserFt = UnitConverter.MmToFt(calcResult.RiserHeight);

                // ════════════════════════════════════════════════════════
                //  ★ 净空预检（在任何楼梯生成事务启动前执行）
                //
                //  原理：
                //    以推算坐标（踏步面中心点、平台中心点）为射线起点，
                //    向上（+Z）投射虚拟射线，命中楼板/梁底面后计算净距。
                //    不依赖已生成的楼梯实体，支持"前置拦截"——不满足
                //    规范时可直接 return，不产生任何模型修改。
                //
                //  阈值（GB55031-2022 §5.3.9）：
                //    梯段净高 ≥ 2200 mm    平台净高 ≥ 2000 mm
                //
                //  仅当 ViewModel.EnableClearCheck 为 true 时执行。
                // ════════════════════════════════════════════════════════
                if (vm.EnableClearCheck)
                {
                    // 优先取非模板三维视图；无则传 null（ClearanceChecker 会降级处理）
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
                        minClearStepMm: 2200,  // GB55031-2022 §5.3.9 梯段净高
                        minClearLandingMm: 2000); // GB55031-2022 §5.3.9 平台净高

                    if (!clearResult.IsCompliant)
                    {
                        // 格式化探测结果（-1 表示无遮挡，以可读文字替代）
                        string stepInfo = clearResult.MinStepClearanceMm >= 0
                            ? $"梯段最小净高：{clearResult.MinStepClearanceMm:F0} mm"
                            : "梯段：上方无遮挡";
                        string landingInfo = clearResult.MinLandingClearanceMm >= 0
                            ? $"平台最小净高：{clearResult.MinLandingClearanceMm:F0} mm"
                            : "平台：上方无遮挡";

                        // 弹出 Yes/No 对话框：用户可选择强制继续或中止
                        var td = new TaskDialog("净空合规预警");
                        td.MainInstruction = "⚠ 检测到净高不满足规范要求，是否仍然生成楼梯？";
                        td.MainContent =
                            $"{clearResult.WarningMessage}\n\n" +
                            $"射线探测结果：\n  {stepInfo}\n  {landingInfo}\n\n" +
                            "选\"是\"将忽略净空预警并继续生成；\n" +
                            "选\"否\"将中止生成，请调整参数后重试。";
                        td.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                        td.DefaultButton = TaskDialogResult.No; // 默认选"否"，更安全

                        // 用户选"否"（或关闭对话框），直接中止，不产生任何模型修改
                        if (td.Show() != TaskDialogResult.Yes)
                            return;
                    }
                    else if (view3D != null)
                    {
                        // 合规时短暂弹窗告知探测结果，方便用户确认
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
                //  楼梯生成主事务（StairsEditScope + Transaction）
                //
                //  StairsEditScope 是 Revit 提供的楼梯专用编辑上下文，
                //  在其中创建的 StairsRun / StairsLanding 才能正确关联到楼梯对象。
                //  内部嵌套 Transaction 用于设置参数和创建构件。
                // ════════════════════════════════════════════════════════

                ElementId stairsId = ElementId.InvalidElementId;
                ElementId run1Id = ElementId.InvalidElementId;

                using (var scope = new StairsEditScope(doc, "自动生成双跑楼梯"))
                {
                    // scope.Start 创建楼梯对象并返回其 ElementId
                    stairsId = scope.Start(adjustedBaseLevel.Id, topLevel.Id);

                    using (var tx = new Transaction(doc, "绘制梯段与平台"))
                    {
                        tx.Start();

                        Stairs stairs = doc.GetElement(stairsId) as Stairs;

                        // ── 切换楼梯族类型（必须在设置参数之前执行）────────────
                        // ChangeTypeId 会将楼梯切换为用户在界面选中的族类型，
                        // 并重置该类型的默认参数；因此后续的参数赋值将覆盖默认值，
                        // 顺序不可颠倒。
                        // 若 selectedStairsTypeName 为空（项目中无楼梯族）则跳过，
                        // 保持 scope.Start 自动选取的默认类型。
                        string selectedStairsTypeName = vm.SelectedStairsTypeName;
                        if (!string.IsNullOrEmpty(selectedStairsTypeName))
                        {
                            StairsType targetStairsType = new FilteredElementCollector(doc)
                                .OfClass(typeof(StairsType))
                                .Cast<StairsType>()
                                .FirstOrDefault(st => st.Name == selectedStairsTypeName);

                            if (targetStairsType != null)
                                stairs.ChangeTypeId(targetStairsType.Id);
                            // targetStairsType == null：名称在文档中已不存在（族被删除），
                            // 保持默认类型，不报错，由后续生成完成汇报提示。
                        }

                        // ── 安全写入踢面数 ──────────────────────────────────────
                        // 大多数楼梯族类型此参数均可写；防御性检查以兼容特殊类型。
                        var desiredRisersParam =
                            stairs.get_Parameter(BuiltInParameter.STAIRS_DESIRED_NUMBER_OF_RISERS);
                        if (desiredRisersParam != null && !desiredRisersParam.IsReadOnly)
                            desiredRisersParam.Set(calcResult.TotalSteps + 2);

                        // ── 安全写入踏步深度 ─────────────────────────────────────
                        // STAIRS_ACTUAL_TREAD_DEPTH 在"整体浇筑楼梯"中为只读参数
                        // （踏步深度由 CreateStraightRun 的端点间距几何决定，不可单独设置）；
                        // 直接调用 .Set() 会抛出"参数只读"异常，导致事务回滚。
                        // 对可写类型（如组合楼梯）仍正常写入；只读时跳过，不影响几何精度。
                        var treadDepthParam =
                            stairs.get_Parameter(BuiltInParameter.STAIRS_ACTUAL_TREAD_DEPTH);
                        if (treadDepthParam != null && !treadDepthParam.IsReadOnly)
                            treadDepthParam.Set(treadDepthFt);

                        // ── 预计算各段几何参数 ───────────────────────────────
                        double run1HeightFt = (calcResult.Run1Steps + 1) * riserFt; // run1 爬升高度
                        double run1Length = calcResult.Run1Steps * treadDepthFt;  // run1 水平长度
                        double run2Length = calcResult.Run2Steps * treadDepthFt;  // run2 水平长度

                        // halfY：梯段中心线到楼梯整体中轴的横向半距
                        double halfY = (wellWidthFt + runWidthFt) / 2.0;
                        // 两跑的局部 Y 坐标（符号由盘旋方向决定）
                        double run1Y = clockwise ? -halfY : halfY;
                        double run2Y = clockwise ? halfY : -halfY;

                        // ── 局部坐标系中的梯段端点 ─────────────────────────────
                        // Run1：从局部原点（X=0）沿 +X 方向爬升到 run1Length，Z=0
                        // Run2：从平台端（X=run2Length）沿 -X 方向爬升到 X=0，
                        //       Z=run1HeightFt（平台顶相对局部原点的高差）
                        XYZ run1LocalStart = new XYZ(0, run1Y, 0);
                        XYZ run1LocalEnd = new XYZ(run1Length, run1Y, 0);
                        XYZ run2LocalStart = new XYZ(run2Length, run2Y, run1HeightFt);
                        XYZ run2LocalEnd = new XYZ(0, run2Y, run1HeightFt);

                        // ── 将局部坐标变换到 Revit 世界坐标 ─────────────────────
                        // 使用 CoordinateTransform.CreateStairTransform 替代原先内联的矩阵乘法，
                        // 与 ClearanceChecker 共享同一变换定义，确保净空校验坐标与生成坐标完全吻合。
                        Transform transform = CoordinateTransform.CreateStairTransform(
                            insertionPtCorrected, angleRad);

                        XYZ run1Start = transform.OfPoint(run1LocalStart);
                        XYZ run1End = transform.OfPoint(run1LocalEnd);
                        XYZ run2Start = transform.OfPoint(run2LocalStart);
                        XYZ run2End = transform.OfPoint(run2LocalEnd);

                        // ── 创建第一跑（StairsRun）──────────────────────────────
                        // CreateStraightRun 以直线中心轴创建直跑梯段，
                        // Justification.Center 表示中心线对齐（宽度向两侧等分）。
                        StairsRun run1 = StairsRun.CreateStraightRun(
                            doc, stairsId,
                            Line.CreateBound(run1Start, run1End),
                            StairsRunJustification.Center);
                        run1.ActualRunWidth = runWidthFt;
                        run1Id = run1.Id; // 保存 ID 用于生成完成后读取实际宽度

                        // ── 创建第二跑（StairsRun）──────────────────────────────
                        StairsRun run2 = StairsRun.CreateStraightRun(
                            doc, stairsId,
                            Line.CreateBound(run2Start, run2End),
                            StairsRunJustification.Center);
                        run2.ActualRunWidth = runWidthFt;

                        // Regenerate 使楼梯对象更新几何，确保后续平台计算基于最新状态
                        doc.Regenerate();

                        // ══════════════════════════════════════════════════════
                        //  草图平台生成（CreateSketchedLanding）
                        //
                        //  ── U 形双跑楼梯局部坐标系几何说明 ──────────────────
                        //
                        //  run1：X=0 → X=run1Length，  Y=run1Y（中心线）
                        //  run2：X=run2Length → X=0，  Y=run2Y（中心线）
                        //
                        //  平台位于两跑远端（X 较大处）：
                        //    X 范围：[min(run1Length, run2Length),
                        //             max(run1Length, run2Length)]
                        //    两跑等长时 X 差值为零，取 landingDepthFt 保底，
                        //    确保平台有最小深度；否则取两值之差与 landingDepthFt 的较大值。
                        //
                        //    Y 范围：从两跑外侧边缘到外侧边缘（含完整梯段宽度）：
                        //      yMin = min(run1Y, run2Y) - runWidthFt / 2
                        //      yMax = max(run1Y, run2Y) + runWidthFt / 2
                        //
                        //  平台底面高程（绝对，英尺）：
                        //      landingElevFt = (run1Steps + 1) × riserFt（相对局部零点）
                        //    注意：此处使用相对高程（不加 adjustedBaseLevel.Elevation），
                        //    因为 Revit 草图平台的高程参数是相对于所属楼梯的 base level。
                        //
                        //  CurveLoop 顶点顺序（俯视逆时针，符合 Revit 通用约定）：
                        //    c0(xMin,yMin) → c1(xMax,yMin) → c2(xMax,yMax) → c3(xMin,yMax) → c0
                        // ══════════════════════════════════════════════════════

                        // 平台相对高程（相对于 adjustedBaseLevel）
                        double landingElevFt = (calcResult.Run1Steps + 1) * riserFt;

                        // ── X 范围：以两跑长度差为基础，不足 landingDepthFt 时补齐 ──
                        double xMin = Math.Min(run1Length, run2Length);
                        double xMax = Math.Max(run1Length, run2Length);
                        if (xMax - xMin < landingDepthFt)
                            xMax = xMin + landingDepthFt;

                        // ── Y 范围：两跑中心线各加减半宽，取外侧边缘 ──────────────
                        double yMin = Math.Min(run1Y, run2Y) - runWidthFt / 2.0;
                        double yMax = Math.Max(run1Y, run2Y) + runWidthFt / 2.0;

                        // ── 四角点：局部坐标 → 世界坐标（Z 直接赋绝对高程）─────────
                        // LocalToWorld 仅对 XY 做旋转+平移，Z 直接赋值，避免旋转矩阵影响高程
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
                        
                        // ── 调试信息：打印平台可写 Double 参数（开发期辅助，生产可移除）──
                        var sb = new StringBuilder();
                        sb.AppendLine($"=== StairsLanding {landing.Id} 可写参数 ===\n");
                        sb.AppendLine($"平台底面高程：{UnitConverter.FtToMm(landingElevFt):F1} mm\n");
                        sb.AppendLine($"平台轮廓（局部坐标，mm）：");
                        sb.AppendLine($"  X [{UnitConverter.FtToMm(xMin):F1}, {UnitConverter.FtToMm(xMax):F1}]");
                        sb.AppendLine($"  Y [{UnitConverter.FtToMm(yMin):F1}, {UnitConverter.FtToMm(yMax):F1}]\n");

                        // 遍历所有可写 Double 参数（仅 Double 类型且非只读），供开发期验证
                        foreach (Parameter p in landing.Parameters.Cast<Parameter>()
                            .Where(p => p.StorageType == StorageType.Double && !p.IsReadOnly)
                            .OrderBy(p => p.Definition.Name))
                        {
                            double valueMm = UnitUtils.ConvertFromInternalUnits(
                                p.AsDouble(), UnitTypeId.Millimeters);
                            sb.AppendLine($"[{p.Definition.Name}]  {valueMm:F1} mm");
                        }

                        TaskDialog.Show("可写 Double 参数", sb.ToString());
                        
                        // 内层事务同样附加失败预处理器：
                        // 预制楼梯的组件尺寸兼容性警告发生在 tx.Commit() 而非 scope.Commit()，
                        // 若不在此处拦截，警告弹窗会打断生成流程。
                        var txFailOpts = tx.GetFailureHandlingOptions();
                        txFailOpts.SetFailuresPreprocessor(new StairsFailurePreprocessor());
                        tx.Commit(txFailOpts);
                    }

                    // scope.Commit 使用 StairsFailurePreprocessor 静默处理警告，避免弹窗中断
                    scope.Commit(new StairsFailurePreprocessor());
                }

                // ── 栏杆处理（必须在 StairsEditScope 关闭后执行）──────────
                // StairsEditScope 关闭后，楼梯才完整生成（含自动关联栏杆），
                // 此时才能获取并操作关联栏杆 ID。
                using (var txRailing = new Transaction(doc, "处理栏杆扶手"))
                {
                    txRailing.Start();

                    Stairs stairsForRailing = doc.GetElement(stairsId) as Stairs;
                    var railingIds = stairsForRailing.GetAssociatedRailings();

                    if (!vm.GenerateRailing)
                    {
                        // 用户不需要栏杆：删除自动生成的所有关联栏杆
                        foreach (ElementId rid in railingIds)
                            doc.Delete(rid);
                    }
                    else
                    {
                        // 用户需要栏杆：将自动生成的栏杆类型替换为用户选择的目标类型
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
                        // targetType == null 时保持自动生成的默认栏杆类型，不报错
                    }

                    txRailing.Commit();
                }

                // ── 整理临时标高 ────────────────────────────────────────────
                // 若此次生成新建了临时标高（tempLevelId != InvalidElementId），
                // 将其重命名为有意义的名称，并在所有平面视图中隐藏，
                // 避免影响用户的视图整洁度。
                if (tempLevelId != ElementId.InvalidElementId)
                {
                    using (var txRename = new Transaction(doc, "整理临时标高"))
                    {
                        txRename.Start();

                        Level tempLv = doc.GetElement(tempLevelId) as Level;
                        // 重命名为"{底部标高名}_偏移{偏移量}mm"，语义清晰
                        tempLv.Name = $"{baseLevel.Name}_偏移{vm.BaseOffsetMm:F0}mm";

                        // 在所有非模板平面视图中隐藏该标高，保持视图整洁
                        foreach (var view in new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewPlan))
                            .Cast<ViewPlan>()
                            .Where(v => !v.IsTemplate))
                        {
                            try
                            {
                                view.HideElements(new List<ElementId> { tempLevelId });
                            }
                            catch { /* 部分视图（如锁定视图）不支持隐藏，跳过即可 */ }
                        }

                        txRename.Commit();
                    }
                }

                // ── 生成完成汇报 ────────────────────────────────────────────
                // 从 Revit 模型读取最终生成结果（含 Revit 自动修正后的实际值）
                Stairs newStairs = doc.GetElement(stairsId) as Stairs;
                StairsRun finalRun = doc.GetElement(run1Id) as StairsRun;

                // 读取最终生效的楼梯族类型名称（ChangeTypeId 成功时与用户选择一致；
                // 失败时为 scope.Start 默认类型，名称可能与 vm.SelectedStairsTypeName 不同）
                string actualStairsTypeName = (doc.GetElement(newStairs.GetTypeId()) as StairsType)?.Name
                                              ?? "（未知）";

                TaskDialog.Show("生成完成",
                    $"楼梯 ID：{newStairs.Id.IntegerValue}\n" +
                    $"楼梯族类型：{actualStairsTypeName}\n" +
                    $"起始标高：{baseLevel.Name}  终止标高：{topLevel.Name}\n" +
                    $"总踏步数：{newStairs.ActualRisersNumber} 级\n" +
                    $"踢面高：{UnitConverter.FtToMm(newStairs.ActualRiserHeight):F1} mm\n" +
                    $"梯段净宽：{UnitConverter.FtToMm(finalRun.ActualRunWidth):F0} mm\n" +
                    $"方向角 θ = {angleRad * 180 / Math.PI:F1}°");
                // ... (保留你原有的 Execute() 前半部分代码不变) ...

                // ==== 替换 Execute() 最后的导出代码 ====
                var stairsTopo = StairTopologyExtractor.Extract(doc);
                var spacesTopo = StairTopologyExtractor.ExtractSpaces(doc);
                var pathsTopo = StairTopologyExtractor.ExtractPaths(doc);

                string geoJson = BuildGeoJson(stairsTopo, spacesTopo, pathsTopo);

                string outputPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "indoor_network.geojson"); // 必须使用 .geojson 后缀
                File.WriteAllText(outputPath, geoJson, Encoding.UTF8);
                TaskDialog.Show("导出完成", $"室内网络 GeoJSON 已导出：\n{outputPath}\n请直接拖入 kepler.gl 查看。");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("生成失败", ex.Message);
            }
        }

        /// <summary>
        /// 手动构建符合 kepler.gl 要求的 GeoJSON 字符串
        /// </summary>
        /// <summary>
        /// 手动构建符合 kepler.gl 要求的 GeoJSON 字符串 (兼容 C# 7.3 及更低版本)
        /// </summary>
        private static string BuildGeoJson(
            List<StairTopologyNode> stairs,
            List<SpaceNode> spaces,
            List<PathNode> paths)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"type\": \"FeatureCollection\",");
            sb.AppendLine("  \"features\": [");

            List<string> featureJsons = new List<string>();

            // 1. 房间节点 (Point)
            foreach (var sp in spaces)
            {
                double pseudoLon = sp.CenterX * 0.00001;
                double pseudoLat = sp.CenterY * 0.00001;
                // 使用常规 $"" 插值，花括号用 {{ 和 }} 转义，双引号用 \" 转义
                string feat = $"{{\n" +
                              $"  \"type\": \"Feature\",\n" +
                              $"  \"geometry\": {{ \"type\": \"Point\", \"coordinates\": [{pseudoLon:F6}, {pseudoLat:F6}, {sp.ElevMm:F1}] }},\n" +
                              $"  \"properties\": {{ \"type\": \"room\", \"name\": \"{Escape(sp.SpaceName)}\", \"level\": \"{Escape(sp.FloorName)}\", \"area\": {sp.Area:F1} }}\n" +
                              $"}}";
                featureJsons.Add(feat);
            }

            // 2. 同层路径线 (LineString)
            foreach (var pt in paths)
            {
                var coords = string.Join(", ", pt.Points.Select(p => $"[{p[0] * 0.00001:F6}, {p[1] * 0.00001:F6}, {p[2]:F1}]"));
                string feat = $"{{\n" +
                              $"  \"type\": \"Feature\",\n" +
                              $"  \"geometry\": {{ \"type\": \"LineString\", \"coordinates\": [{coords}] }},\n" +
                              $"  \"properties\": {{ \"type\": \"path\", \"level\": \"{Escape(pt.LevelName)}\", \"length\": {pt.LengthMm:F1} }}\n" +
                              $"}}";
                featureJsons.Add(feat);
            }

            // 3. 楼梯跨层连接线 (LineString)
            foreach (var st in stairs)
            {
                string coords = $"[{st.EntryX * 0.00001:F6}, {st.EntryY * 0.00001:F6}, {st.EntryZ:F1}], [{st.ExitX * 0.00001:F6}, {st.ExitY * 0.00001:F6}, {st.ExitZ:F1}]";
                string feat = $"{{\n" +
                              $"  \"type\": \"Feature\",\n" +
                              $"  \"geometry\": {{ \"type\": \"LineString\", \"coordinates\": [{coords}] }},\n" +
                              $"  \"properties\": {{ \"type\": \"stair\", \"from\": \"{Escape(st.BottomFloorName)}\", \"to\": \"{Escape(st.TopFloorName)}\" }}\n" +
                              $"}}";
                featureJsons.Add(feat);
            }

            sb.Append(string.Join(",\n", featureJsons));
            sb.AppendLine("\n  ]\n}");
            return sb.ToString();
        }

        private static string Escape(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
