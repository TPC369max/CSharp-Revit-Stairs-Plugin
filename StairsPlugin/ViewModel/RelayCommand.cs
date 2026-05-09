using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace StairsPlugin.ViewModel
{
    /// <summary>
    /// 通用 <see cref="ICommand"/> 实现，将命令逻辑以委托形式注入，
    /// 避免为每个命令单独创建实现类（MVVM 标准做法）。
    ///
    /// ── 设计说明 ──────────────────────────────────────────────────────
    /// WPF 命令系统要求 ViewModel 提供 ICommand 属性供 XAML 绑定。
    /// RelayCommand 将"执行逻辑"（Action）和"可执行判断"（Func&lt;bool&gt;）
    /// 以构造函数参数形式传入，ViewModel 只需在属性初始化处定义 Lambda，
    /// 无需为每个按钮创建独立的命令类。
    ///
    /// ── CanExecuteChanged 实现策略 ────────────────────────────────────
    /// 本实现挂钩到 <see cref="CommandManager.RequerySuggested"/>，
    /// 即 WPF 在每次 UI 交互后自动重新查询 CanExecute。
    /// 优点：无需手动触发（绑定简单）；
    /// 缺点：轻微性能开销（每次键盘/鼠标事件都会重查）。
    ///
    /// 对于需要精确控制刷新时机的场景，可调用
    /// <see cref="RaiseCanExecuteChanged"/> 手动触发，例如
    /// ViewModel 属性变更时立即更新按钮状态。
    /// </summary>
    public class RelayCommand : ICommand
    {
        // 命令执行逻辑委托（必须，不允许为 null）
        Action _execute;

        // 命令可执行判断委托（可选；为 null 时默认始终可执行）
        Func<bool> _canExecute;

        /// <summary>
        /// 构造 RelayCommand。
        /// </summary>
        /// <param name="execute">命令被触发时执行的方法（不可为 null）</param>
        /// <param name="canExecute">
        ///   决定命令是否可执行的谓词；为 null 时命令始终可用。
        ///   绑定的按钮 IsEnabled 由此谓词控制。
        /// </param>
        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute    = execute;
            _canExecute = canExecute;
        }

        /// <summary>
        /// WPF 命令系统订阅此事件以监听"可执行状态"变化。
        /// 此处委托给 <see cref="CommandManager.RequerySuggested"/>，
        /// WPF 会在每次用户交互后自动触发，无需手动管理订阅列表。
        /// </summary>
        public event EventHandler CanExecuteChanged
        {
            add    { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        /// <summary>
        /// 判断命令当前是否可执行。
        /// 若构造时未提供 <c>canExecute</c> 委托，则始终返回 true。
        /// WPF 绑定引擎会将返回值同步到按钮的 IsEnabled 属性。
        /// </summary>
        /// <param name="parameter">命令参数（本实现忽略，始终传 null）</param>
        /// <returns>true 表示可执行（按钮激活），false 表示不可执行（按钮变灰）</returns>
        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

        /// <summary>
        /// 执行命令逻辑。
        /// WPF 在用户点击绑定按钮时调用此方法（前提是 CanExecute 返回 true）。
        /// </summary>
        /// <param name="parameter">命令参数（本实现忽略）</param>
        public void Execute(object parameter) => _execute();

        /// <summary>
        /// 手动通知 WPF 重新查询 CanExecute，立即刷新按钮的 IsEnabled 状态。
        ///
        /// 用于 ViewModel 属性发生变化后主动触发状态更新，
        /// 无需等待下一次用户交互（如 TextBox 输入完成后立即更新按钮）。
        /// 内部调用 <see cref="CommandManager.InvalidateRequerySuggested"/>，
        /// 它会对当前线程上所有已注册的 RequerySuggested 订阅者发出通知。
        /// </summary>
        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }
}
