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
    /// 双跑平行楼梯自动生成插件的主命令入口。
    ///
    /// ── 职责（精简后）────────────────────────────────────────────────
    ///   1. 从 Revit 文档读取项目数据（标高、楼梯族类型、栏杆族类型）。
    ///   2. 创建 ExternalEvent + Handler，构建 ViewModel 并注入数据。
    ///   3. 以非模态（Show）方式弹出 WPF 窗口，然后立即返回 Succeeded。
    ///   生成事务已迁移至 StairGlobalEventHandler.Execute()，
    ///   由用户点击"生成"后通过 ExternalEvent.Raise() 异步触发。
    ///
    /// ── 架构演进说明 ─────────────────────────────────────────────────
    /// 旧方案：
    ///   用 ShowDialog 打开模态 WPF 窗口，在 Execute() 上下文中直接执行生成事务。
    ///   问题：PickPoint 调用需要让出消息循环，而 ShowDialog 持有模态锁，
    ///         导致 PickPoint 与 WPF 消息循环冲突，产生孤立浮窗或死锁。
    ///
    /// 新方案：
    ///   ① Show() 打开非模态窗口，Execute() 立即返回，Revit 主线程恢复。
    ///   ② 用户拾取 P1/P2 时，Code-behind 通过 Hide()/Show() 切换窗口可见性，
    ///      与 Revit 视图交互无冲突。
    ///   ③ 用户点击"生成"时，ViewModel.OnGenerate() 调用 ExternalEvent.Raise()，
    ///      Revit 在下一个空闲帧回调 StairGlobalEventHandler.Execute()，
    ///      在合法的 API 上下文中执行事务，彻底解除与 WPF 的耦合。
    ///
    /// ── Transaction 模式 ─────────────────────────────────────────────
    /// 本命令标记为 Manual，但 Execute() 本身不开启事务；
    /// 实际事务均在 StairGlobalEventHandler 中管理。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CommandStairGenerator : IExternalCommand
    {
        /// <summary>
        /// Revit 命令入口，由 Revit 在用户触发命令时调用。
        /// 本方法仅负责初始化和窗口启动，不执行任何事务。
        /// </summary>
        /// <param name="commandData">Revit 提供的命令上下文（含 UIApplication）</param>
        /// <param name="message">失败时的错误消息（由框架展示给用户）</param>
        /// <param name="elements">失败时高亮的元素集合（本插件不使用）</param>
        /// <returns>Succeeded 表示命令启动成功（窗口已弹出）；Failed 表示初始化异常</returns>
        public Result Execute(ExternalCommandData commandData,
                              ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document   doc   = uiDoc.Document;

            try
            {
                // ── 步骤 1：创建外部事件 + 处理器 ────────────────────────
                // Handler 持有楼梯生成逻辑，ViewModel 在"生成"时将自身引用写入 Handler。
                // ExternalEvent 是 Revit 提供的异步执行机制，
                // 确保 Handler.Execute() 始终在合法的 Revit API 上下文中运行。
                var handler      = new StairGlobalEventHandler { UIDoc = uiDoc };
                ExternalEvent externalEvent = ExternalEvent.Create(handler);

                // ── 步骤 2：构建 ViewModel 并注入项目数据 ─────────────────
                // ViewModel 持有 ExternalEvent 引用，用于"生成"按钮触发异步事务。
                var vm = new ViewModel.ViewModel(externalEvent, handler);

                // 注入标高列表（已按高程升序排列）
                vm.LoadLevels(RevitLevelTools.GetLevels(doc));

                // 注入楼梯系统族类型名称列表（供 ComboBox 选择楼梯族）
                var stairsTypeNames = new FilteredElementCollector(doc)
                    .OfClass(typeof(StairsType))
                    .Cast<StairsType>()
                    .Select(st => st.Name)
                    .ToList();
                vm.LoadStairsTypes(stairsTypeNames);

                // 注入栏杆扶手族类型名称列表
                // 注意：RailingType 没有对应的强类型 Revit 类，
                // 使用 ElementType 并以 GetType().Name 字符串过滤
                var railingTypeNames = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElementType))
                    .Where(e => e.GetType().Name == "RailingType")
                    .Select(e => e.Name)
                    .ToList();
                vm.LoadRailingTypes(railingTypeNames);

                // ── 步骤 3：以非模态方式弹出 WPF 窗口 ───────────────────
                // 关键：使用 Show() 而非 ShowDialog()：
                //   • WPF 消息循环不阻塞 Revit 主线程，Execute() 可立即返回；
                //   • Code-behind 中的 BtnPickP1/P2 可以安全地 Hide()/Show() 切换；
                //   • 生成逻辑由 ExternalEvent 异步执行，不依赖本 Execute() 的上下文。
                var win = new StairGeneratorWindow(uiDoc, vm);

                // 设置 Owner 保证窗口层级正确（始终浮于 Revit 主窗口之上，不被遮挡）
                var helper = new System.Windows.Interop.WindowInteropHelper(win);
                helper.Owner = commandData.Application.MainWindowHandle;

                win.Show(); // 非模态打开：立即返回，窗口由 WPF 自身管理生命周期

                // ── 步骤 4：立即返回，控制权交还 Revit ──────────────────
                // Execute() 在此结束。窗口将持续存活直到用户点击"取消"或关闭。
                // 后续所有模型修改均通过 ExternalEvent 在独立回调中完成。
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // 初始化阶段（创建事件、读取标高等）若出现异常，
                // 通过 message 将错误信息反馈给 Revit 框架
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
