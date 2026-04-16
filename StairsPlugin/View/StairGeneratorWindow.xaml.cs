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
//    3. DialogResult 关闭窗口（纯 WPF 窗口行为）
//
//  不允许留在这里的代码：
//    × 任何 FilteredElementCollector 查询
//    × 任何 StairCodeLibrary / 规范校验逻辑
//    × 任何直接操作控件属性（.Text= / .Background= 等）
//    × 任何计算逻辑（踏步解算、高差计算）
// ================================================================

namespace StairsPlugin.Views
{
    public partial class StairGeneratorWindow : Window
    {
        // ViewModel 引用：拾取完成后写入 P1/P2，其余全部通过绑定
        private readonly ViewModel.ViewModel _vm;
        private readonly UIDocument _uiDoc;

        // =========================================================
        //  构造函数：只做两件事——绑定 DataContext，保存 uiDoc 引用
        // =========================================================
        public StairGeneratorWindow(UIDocument uiDoc, ViewModel.ViewModel vm)
        {
            InitializeComponent();

            _uiDoc = uiDoc;
            _vm = vm;
            DataContext = vm;   // 所有 {Binding ...} 的数据源

            // 监听 ViewModel 的生成命令执行结果，由此关闭窗口
            // GenerateCommand.Execute 本身不关窗，窗口关闭属于 View 职责
            _vm.GenerateRequested += OnGenerateRequested;
        }

        // =========================================================
        //  拾取 P1（插入点）
        //  唯一需要调用 Revit UIDocument.Selection 的地方
        // =========================================================
        private void BtnPickP1_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                this.Hide();
                XYZ p1 = _uiDoc.Selection.PickPoint("请在平面视图中点击楼梯插入点 P1");
                this.Show();

                // 写入 ViewModel，由 ViewModel 负责计算和通知 UI 更新
                _vm.P1 = p1;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // 用户按 Esc 取消，窗口重新显示，不做任何处理
                this.Show();
            }
        }

        // =========================================================
        //  拾取 P2（方向点）
        // =========================================================
        private void BtnPickP2_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                this.Hide();
                XYZ p2 = _uiDoc.Selection.PickPoint("请在平面视图中点击方向点 P2");
                this.Show();

                // 写入 ViewModel，由 ViewModel 自动触发 ThetaDisplay 等属性更新
                _vm.P2 = p2;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                this.Show();
            }
        }

        // =========================================================
        //  取消按钮：纯窗口行为，不需要经过 ViewModel
        // =========================================================
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _vm.IsConfirmed = false;
            SetDialogResultSafe(false);
            Close();
        }

        // =========================================================
        //  ViewModel 通知"校验通过，可以生成"→ 关闭窗口
        //  这是 View 与 ViewModel 之间唯一的事件耦合点
        // =========================================================
        private void OnGenerateRequested(object sender, EventArgs e)
        {
            _vm.IsConfirmed = true;
            SetDialogResultSafe(true);
            Close();
        }

        // =========================================================
        //  安全设置 DialogResult：
        //  只有通过 ShowDialog() 打开的模态窗口才能设置此属性。
        //  在 Revit AddInManager 的调试环境下，窗口有时以非模态方式
        //  运行，此时设置 DialogResult 会抛 InvalidOperationException。
        //  通过 IsModal 检测规避此问题；调用方改为读取 vm.IsConfirmed。
        // =========================================================
        private void SetDialogResultSafe(bool result)
        {
            // WPF 内部：只有 _showingAsDialog == true 时 DialogResult 才可写。
            // 用 try/catch 做最终保障，IsModal 判断做前置过滤。
            if (!IsModal())
                return;
            try
            {
                DialogResult = result;
            }
            catch (InvalidOperationException) { /* 非模态，忽略 */ }
        }

        /// <summary>
        /// 判断当前窗口是否以模态（ShowDialog）方式打开。
        /// 通过反射读取 WPF 内部字段 _showingAsDialog。
        /// </summary>
        private bool IsModal()
        {
            try
            {
                var field = typeof(Window).GetField(
                    "_showingAsDialog",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
                return field != null && (bool)field.GetValue(this);
            }
            catch { return false; }
        }

        // 窗口关闭时解除事件订阅，防止内存泄漏
        protected override void OnClosed(EventArgs e)
        {
            _vm.GenerateRequested -= OnGenerateRequested;
            base.OnClosed(e);
        }
    }
}