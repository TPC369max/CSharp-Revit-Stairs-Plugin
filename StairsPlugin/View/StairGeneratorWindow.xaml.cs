using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Windows;
using StairsPlugin.ViewModel;

// ================================================================
//  View 层职责边界（MVVM）
//
//  允许留在这里的代码（共 3 类）：
//    1. InitializeComponent() + DataContext 赋值
//    2. PickPoint 拾取（必须调用 UIDocument.Selection，属于 Revit UI 层）
//    3. 关闭窗口（纯 WPF 窗口行为）
//
//  不允许留在这里的代码：
//    × 任何 FilteredElementCollector 查询
//    × 任何 StairCodeLibrary / 规范校验逻辑
//    × 任何直接操作控件属性（.Text= / .Background= 等）
//    × 任何计算逻辑（踏步解算、高差计算）
//
//  ★ 架构变更说明
//    旧方案：ShowDialog 模态 → PickPoint 与 WPF 消息循环冲突 → 孤立浮窗
//    新方案：Show 非模态 + ExternalEvent → Hide/Show 自由切换，无冲突
//    因此本文件移除了 SetDialogResultSafe / IsModal / OnGenerateRequested，
//    窗口关闭由用户直接点"取消"或 X 完成；生成由 ExternalEvent 异步触发。
// ================================================================

namespace StairsPlugin.Views
{
    public partial class StairGeneratorWindow : Window
    {
        private readonly ViewModel.ViewModel _vm;
        private readonly UIDocument _uiDoc;

        // =========================================================
        //  构造函数：绑定 DataContext，保存 uiDoc 引用
        // =========================================================
        public StairGeneratorWindow(UIDocument uiDoc, ViewModel.ViewModel vm)
        {
            InitializeComponent();

            _uiDoc = uiDoc;
            _vm = vm;
            DataContext = vm;
        }

        // =========================================================
        //  拾取 P1（插入点）
        //
        //  ★ 新方案（非模态）下可以安全使用 Hide()/Show()：
        //    窗口以 Show() 打开，没有 ShowDialog 的模态阻塞，
        //    Hide() 不会触发任何消息循环误判，Show() 恢复显示也不会
        //    引起状态丢失。这是 AI对话.txt 推荐方案的核心优势。
        // =========================================================
        private void BtnPickP1_Click(object sender, RoutedEventArgs e)
        {
            this.Hide(); // 让出视野，使 Revit 视图可以响应鼠标
            try
            {
                XYZ p1 = _uiDoc.Selection.PickPoint("请在平面视图中点击楼梯插入点 P1");
                _vm.P1 = p1; // 写入 ViewModel，触发属性通知与解算
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // 用户按 Esc 取消，不做任何处理
            }
            finally
            {
                this.Show();     // 恢复显示
                this.Activate(); // 将窗口带到前台
            }
        }

        // =========================================================
        //  拾取 P2（方向点）
        // =========================================================
        private void BtnPickP2_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            try
            {
                XYZ p2 = _uiDoc.Selection.PickPoint("请在平面视图中点击方向点 P2");
                _vm.P2 = p2; // 写入 ViewModel，自动触发 ThetaDisplay 等属性更新
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // 用户按 Esc 取消，不做任何处理
            }
            finally
            {
                this.Show();
                this.Activate();
            }
        }

        // =========================================================
        //  取消按钮：直接关闭窗口，无需与 ViewModel 交互
        //  （非模态窗口无 DialogResult，关闭即为取消）
        // =========================================================
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}