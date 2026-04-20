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
    /// 职责（精简后）：
    ///   1. 从 Revit 文档读取项目数据（标高、楼梯族、栏杆族）
    ///   2. 创建 ExternalEvent + Handler，注入 ViewModel
    ///   3. 以非模态（Show）方式弹出 WPF 窗口后立即返回 Succeeded
    ///
    /// 生成事务已迁移至 StairGlobalEventHandler.Execute()，
    /// 由用户点击"生成"后通过 ExternalEvent.Raise() 异步触发。
    /// 这彻底解决了 PickPoint 与 ShowDialog 的消息循环冲突。
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
                // ── 步骤 1：创建外部事件 + 处理器 ────────────────────────────
                // Handler 持有生成逻辑，ViewModel 在"生成"时将自身引用写入 Handler
                var handler = new StairGlobalEventHandler { UIDoc = uiDoc };
                ExternalEvent externalEvent = ExternalEvent.Create(handler);

                // ── 步骤 2：构建 ViewModel 并注入项目数据 ──────────────────
                var vm = new ViewModel.ViewModel(externalEvent, handler);

                // 标高列表
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

                // ── 步骤 3：非模态显示 WPF 窗口 ──────────────────────────────
                // 使用 Show() 而非 ShowDialog()：
                //   - WPF 消息循环不再阻塞 Revit 主线程
                //   - BtnPickP1/P2 可以安全地 Hide()/Show()
                //   - 生成逻辑由 ExternalEvent 异步执行，不依赖 Execute() 上下文
                var win = new StairGeneratorWindow(uiDoc, vm);

                // 设置 Owner 保证窗口层级正确（始终显示在 Revit 主窗口之上）
                var helper = new System.Windows.Interop.WindowInteropHelper(win);
                helper.Owner = commandData.Application.MainWindowHandle;

                win.Show(); // 非模态：立即返回，窗口独立存活
                // ── 步骤 4：立即返回，控制权交还 Revit ───────────────────────
                // Execute() 在此结束。窗口由 WPF 自身管理生命周期。
                // 生成逻辑将在用户点击"生成"后由 ExternalEvent 触发执行。
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}