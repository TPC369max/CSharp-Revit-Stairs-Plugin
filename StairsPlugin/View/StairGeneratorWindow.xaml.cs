using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Windows;
using StairsPlugin.ViewModel;

// ================================================================
//  View 层职责边界（MVVM）
//
//  ── 允许留在这里的代码（共 3 类） ────────────────────────────────
//    1. InitializeComponent() + DataContext 赋值
//       — WPF 标准初始化，无法从 XAML 侧完成。
//
//    2. PickPoint 拾取（必须调用 UIDocument.Selection，属于 Revit UI 层）
//       — Revit API 的 Selection.PickPoint 要求在 Revit 主线程上下文中
//         直接执行，不能通过数据绑定或命令路由到 ViewModel 完成，
//         因此保留在 Code-behind 中是合理且必要的。
//         拾取结果通过写入 ViewModel 属性（_vm.P1 / _vm.P2）实现层间传递。
//
//    3. 关闭窗口（纯 WPF 窗口行为）
//       — BtnCancel_Click 仅调用 Close()，无业务逻辑，
//         不需要与 ViewModel 交互。
//
//  ── 不允许留在这里的代码 ──────────────────────────────────────────
//    × 任何 FilteredElementCollector 查询        → 移至 Main.cs / ViewModel
//    × 任何 StairCodeLibrary / 规范校验逻辑      → 移至 ViewModel
//    × 任何直接操作控件属性（.Text= / .Background= 等）→ 移至 XAML DataTrigger
//    × 任何计算逻辑（踏步解算、高差计算）          → 移至 StairCalculator
//
//  ── 架构变更说明（相对旧方案） ────────────────────────────────────
//  旧方案：
//    ShowDialog 模态窗口 → PickPoint 与 WPF 消息循环冲突 → 孤立浮窗/死锁。
//    额外引入了 SetDialogResultSafe / IsModal / OnGenerateRequested 等
//    绕过模态限制的补丁代码，维护成本高。
//
//  新方案（当前）：
//    Show 非模态 + ExternalEvent 异步生成。
//    Hide()/Show() 可自由切换，无消息循环冲突；
//    生成事务在 ExternalEvent 回调中执行，与 WPF 完全解耦。
//    因此本文件移除了上述所有补丁代码，只保留最精简的 View 层逻辑。
// ================================================================

namespace StairsPlugin.Views
{
    public partial class StairGeneratorWindow : Window
    {
        // ViewModel 引用：用于接收 PickPoint 结果（写入 P1/P2 属性）
        private readonly ViewModel.ViewModel _vm;

        // UIDocument 引用：仅用于调用 Selection.PickPoint，不做其他用途
        private readonly UIDocument _uiDoc;

        // =========================================================
        //  构造函数
        //
        //  初始化 WPF 组件并设置 DataContext，使所有 XAML 绑定生效。
        //  保存 uiDoc 引用，供后续 PickPoint 调用使用。
        //  不在构造函数中执行任何业务逻辑或 Revit API 调用。
        // =========================================================
        public StairGeneratorWindow(UIDocument uiDoc, ViewModel.ViewModel vm)
        {
            InitializeComponent();

            _uiDoc      = uiDoc;
            _vm         = vm;
            DataContext = vm; // 将 ViewModel 绑定到 XAML 的所有 Binding
        }

        // =========================================================
        //  拾取 P1（楼梯插入点）
        //
        //  ── 流程 ──────────────────────────────────────────────────
        //  1. Hide()：隐藏窗口，让 Revit 视图获得焦点和鼠标事件。
        //  2. PickPoint：等待用户在平面视图中点击，阻塞直到确认或取消。
        //  3. 写入 _vm.P1：触发 ViewModel 的属性通知（CanPickP2、解算等）。
        //  4. finally 中 Show() + Activate()：恢复窗口显示并置于前台。
        //
        //  ── Hide()/Show() 为何在非模态窗口下安全 ─────────────────
        //  非模态（Show 打开的窗口）不持有模态消息循环锁，
        //  Hide() 仅设置 Visibility，不影响 Revit 主线程消息处理；
        //  Show() 恢复后窗口状态（DataContext、绑定值）完全保留。
        // =========================================================
        private void BtnPickP1_Click(object sender, RoutedEventArgs e)
        {
            this.Hide(); // 让出视野，使 Revit 视图可响应鼠标
            try
            {
                XYZ p1 = _uiDoc.Selection.PickPoint("请在平面视图中点击楼梯插入点 P1");
                // 写入 ViewModel，触发 P1Display、CanPickP2、GenerateCommand.CanExecute 等属性通知
                _vm.P1 = p1;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // 用户按 Esc 或右键取消拾取，不做任何处理，保留原有 P1 值
            }
            finally
            {
                // 无论成功还是取消，都恢复窗口显示
                this.Show();
                this.Activate(); // 将窗口带到所有窗口的最前端
            }
        }

        // =========================================================
        //  拾取 P2（方向点）
        //
        //  P2 仅用于确定楼梯爬升方向（P1→P2 的向量角），
        //  不作为楼梯的终点或长度依据。
        //  拾取成功后写入 _vm.P2，触发 ThetaDisplay 等属性更新，
        //  并在 ViewModel.Recalculate() 中启动 Phase-2 解算。
        // =========================================================
        private void BtnPickP2_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            try
            {
                XYZ p2 = _uiDoc.Selection.PickPoint("请在平面视图中点击方向点 P2");
                // 写入 ViewModel，自动触发 P2Display、ThetaDisplay、Recalculate 等
                _vm.P2 = p2;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // 用户按 Esc 取消，不修改 P2
            }
            finally
            {
                this.Show();
                this.Activate();
            }
        }

        // =========================================================
        //  取消按钮：直接关闭窗口
        //
        //  非模态窗口没有 DialogResult，Close() 即为最终操作。
        //  关闭后 ViewModel 和 Handler 的引用由 GC 回收，
        //  不会对 Revit 文档产生任何副作用。
        // =========================================================
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
