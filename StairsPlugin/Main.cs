using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using StairsPlugin.Model;
using StairsPlugin.Views;
using System;
using System.Linq;

namespace StairsPlugin
{
    /// <summary>
    /// 双跑平行楼梯自动生成插件的主入口。
    ///
    /// 职责：
    ///   1. 从 Revit 文档读取项目数据（标高、楼梯族、栏杆族）
    ///   2. 注入 ViewModel，弹出 WPF 窗口
    ///   3. 用户确认后，读取 ViewModel 参数，执行楼梯生成事务
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CommandStairGenerator : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                // ── 步骤 1：构建 ViewModel 并注入项目数据 ──────────────────
                var vm = new ViewModel.ViewModel();

                // 标高列表（使用 RevitLevelTools 工具方法）
                vm.LoadLevels(RevitLevelTools.GetLevels(doc));

                // 楼梯系统族类型名称
                var stairsTypeNames = new FilteredElementCollector(doc)
                    .OfClass(typeof(StairsType))
                    .Cast<StairsType>()
                    .Select(st => st.Name)
                    .ToList();
                vm.LoadStairsTypes(stairsTypeNames);

                // 栏杆扶手族类型名称
                var railingTypeNames = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElementType))
                    .Where(e => e.GetType().Name == "RailingType")
                    .Select(e => e.Name)
                    .ToList();
                vm.LoadRailingTypes(railingTypeNames);

                // ── 步骤 2：弹出窗口，等待用户操作 ─────────────────────────
                var win = new StairGeneratorWindow(uiDoc, vm);

                // ShowDialog() 在正式 Ribbon 按钮调用时返回 bool?
                // AddInManager 调试时可能以非模态运行，此时返回值不可靠。
                // 统一改为读取 vm.IsConfirmed（由 View 层在关窗前写入）。
                win.ShowDialog();

                if (!vm.IsConfirmed)
                    return Result.Cancelled;

                // ── 步骤 3：从 ViewModel 读取所有生成参数 ────────────────────
                Level baseLevel = vm.SelectedBaseLevel;
                Level topLevel = vm.SelectedTopLevel;
                XYZ insertionPt = vm.P1;          // 平面插入点（Revit内部单位）
                double angleRad = vm.DirectionAngleRad; // 旋转角（弧度）
                bool clockwise = vm.IsClockwise;

                // 几何参数（mm → 英尺，Revit内部单位）
                double runWidthFt = MmToFt(vm.RunWidthMm);
                double treadDepthFt = MmToFt(vm.TreadDepthMm);
                double wellWidthFt = MmToFt(vm.WellWidthMm);
                double landingDepthFt = MmToFt(vm.LandingDepthMm);
                double baseOffsetFt = MmToFt(vm.BaseOffsetMm);

                // 总高（英尺）
                double totalHeightFt = topLevel.Elevation - baseLevel.Elevation + baseOffsetFt;

                // 踏步解算结果（直接用 ViewModel 缓存，已在校验时解算）
                var calcResult = StairCalculator.Calculate(
                    vm.CurrentRule.MaxRiserHeight > 0
                        ? FtToMm(totalHeightFt)
                        : 3600,
                    vm.CurrentRule);

                double riserFt = MmToFt(calcResult.RiserHeight);

                // ── 步骤 4：楼梯生成事务 ─────────────────────────────────────
                ElementId stairsId = ElementId.InvalidElementId;
                ElementId run1Id = ElementId.InvalidElementId;

                using (var scope = new StairsEditScope(doc, "自动生成双跑楼梯"))
                {
                    stairsId = scope.Start(baseLevel.Id, topLevel.Id);

                    using (var tx = new Transaction(doc, "绘制梯段与平台"))
                    {
                        tx.Start();

                        Stairs stairs = doc.GetElement(stairsId) as Stairs;

                        // 设置总踏步数与踏步深度
                        stairs.get_Parameter(BuiltInParameter.STAIRS_DESIRED_NUMBER_OF_RISERS)
                              .Set(calcResult.TotalSteps);
                        stairs.get_Parameter(BuiltInParameter.STAIRS_ACTUAL_TREAD_DEPTH)
                              .Set(treadDepthFt);

                        // ── 计算两跑的轮廓线（局部坐标）──
                        // 局部坐标：以 insertionPt 为原点，Y 轴为爬升方向
                        double run1Length = (calcResult.Run1Steps - 1) * treadDepthFt;
                        double run2Length = (calcResult.Run2Steps - 1) * treadDepthFt;

                        // 右旋（顺时针）：第一跑朝 +Y，第二跑朝 -Y，横向偏移 runWidth + wellWidth
                        double lateralOffset = clockwise
                            ? (runWidthFt + wellWidthFt)
                            : -(runWidthFt + wellWidthFt);

                        XYZ run1LocalStart = new XYZ(0, 0, 0);
                        XYZ run1LocalEnd = new XYZ(0, run1Length, 0);
                        XYZ run2LocalStart = new XYZ(lateralOffset, run1Length, 0);
                        XYZ run2LocalEnd = new XYZ(lateralOffset, 0, 0);

                        // ── 应用旋转变换（两点向量法算出的 angleRad）──
                        // 旋转轴：Z 轴（绕铅垂轴旋转平面）
                        XYZ axis = XYZ.BasisZ;
                        var rotate = Transform.CreateRotation(axis, angleRad);
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

                        // ── 创建第二跑（设置起止标高）──
                        StairsRun run2 = StairsRun.CreateStraightRun(
                            doc, stairsId,
                            Line.CreateBound(run2Start, run2End),
                            StairsRunJustification.Center);
                        run2.ActualRunWidth = runWidthFt;

                        double run1HeightFt = calcResult.Run1Steps * riserFt;
                        // 关键：先设顶部标高，再设底部标高
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

                // ── 步骤 5：净空合规校验（可选）────────────────────────────
                if (vm.EnableClearCheck)
                {
                    RunClearanceCheck(doc, insertionPt, calcResult,
                                      riserFt, treadDepthFt, angleRad,
                                      topLevel.Elevation, vm.CurrentRule.MinClearHeight);
                }

                // ── 步骤 6：生成完成提示 ────────────────────────────────────
                Stairs newStairs = doc.GetElement(stairsId) as Stairs;
                StairsRun finalRun = doc.GetElement(run1Id) as StairsRun;

                TaskDialog.Show("生成完成",
                    $"楼梯 ID：{newStairs.Id.IntegerValue}\n" +
                    $"起始标高：{baseLevel.Name}  终止标高：{topLevel.Name}\n" +
                    $"总踏步数：{newStairs.ActualRiserHeight} 级\n" +
                    $"踢面高：{FtToMm(newStairs.ActualRiserHeight):F1} mm\n" +
                    $"梯段净宽：{FtToMm(finalRun.ActualRunWidth):F0} mm\n" +
                    $"方向角 θ = {angleRad * 180 / Math.PI:F1}°");

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // =============================================================
        //  净空校验（调用 Model 层 ClearanceChecker）
        // =============================================================
        private void RunClearanceCheck(
            Document doc,
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

        // =============================================================
        //  单位换算辅助
        // =============================================================
        private static double MmToFt(double mm)
            => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        private static double FtToMm(double ft)
            => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
    }
}