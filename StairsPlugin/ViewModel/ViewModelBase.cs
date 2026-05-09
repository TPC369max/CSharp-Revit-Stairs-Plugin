using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin.ViewModel
{
    /// <summary>
    /// ViewModel 基类，实现 <see cref="INotifyPropertyChanged"/> 接口。
    ///
    /// ── 职责 ──────────────────────────────────────────────────────────
    /// 提供两个受保护的工具方法，供所有派生 ViewModel 使用：
    ///   • <see cref="OnPropertyChanged"/>  — 触发属性变更通知
    ///   • <see cref="SetField{T}"/>        — 防重复赋值并自动触发通知
    ///
    /// ── 使用约定 ──────────────────────────────────────────────────────
    /// 派生类的属性 setter 统一使用 SetField：
    /// <code>
    /// private int _value;
    /// public int Value
    /// {
    ///     get => _value;
    ///     set { if (SetField(ref _value, value)) DoSomethingOnChange(); }
    /// }
    /// </code>
    /// SetField 仅在新旧值不同时才触发通知，避免无效刷新导致的循环更新。
    /// 利用 [CallerMemberName] 特性，无需手动传入属性名字符串，
    /// 编译期即可捕获属性名拼写错误。
    /// </summary>
    public class ViewModelBase : INotifyPropertyChanged
    {
        /// <summary>
        /// 属性值发生变更时由 WPF 绑定引擎监听的事件。
        /// WPF 的 DataBinding 系统订阅此事件以决定何时刷新 UI 控件。
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 触发指定属性的变更通知，驱动 WPF 绑定刷新对应控件。
        ///
        /// 借助 <see cref="CallerMemberNameAttribute"/>，调用方无需传参，
        /// 编译器会自动将调用处的成员名称注入 <paramref name="name"/>。
        ///
        /// 也可显式传入其他属性名以触发关联属性的更新：
        /// <code>OnPropertyChanged(nameof(SomeOtherProperty));</code>
        /// </summary>
        /// <param name="name">要通知变更的属性名称（默认为调用成员的名称）</param>
        public void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>
        /// 对字段进行防重复赋值，仅在值发生实际变化时更新并触发通知。
        ///
        /// ── 返回值语义 ────────────────────────────────────────────────
        /// true  → 值已更新，PropertyChanged 已触发；
        ///         调用方可在 if (SetField(...)) 块中执行联动逻辑。
        /// false → 新旧值相等，未触发通知，无需任何处理。
        ///
        /// ── 相等性比较 ────────────────────────────────────────────────
        /// 使用 <see cref="object.Equals"/> 进行比较，
        /// 对值类型（int、double、bool）比较数值，
        /// 对引用类型比较引用（如 Level、XYZ），而非深度比较内容。
        /// 若需要深度比较，派生类应重写 Equals 方法。
        /// </summary>
        /// <typeparam name="T">字段类型</typeparam>
        /// <param name="field">backing field 的引用，赋值目标</param>
        /// <param name="value">新值</param>
        /// <param name="name">属性名（默认由编译器注入，无需手动传递）</param>
        /// <returns>true 表示值已变更并已通知；false 表示值未变</returns>
        public bool SetField<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            // 新旧值相同时直接返回 false，避免触发不必要的 UI 刷新
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
