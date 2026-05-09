using System.Collections.Generic;
using System.Linq;

namespace StairsPlugin.ViewModel
{
    /// <summary>
    /// 规范违规信息的聚合值对象（Value Object）。
    ///
    /// ── 设计动机 ──────────────────────────────────────────────────────
    /// 在重构前，ViewModel.Recalculate() 方法中存在以下问题：
    ///   • violations.Add("...") 语句散落在方法各处；
    ///   • HasViolation 和 ViolationDetail 的赋值逻辑内聚却分离；
    ///   • 多个 return 分支上的拼接格式不一致（有的加句号，有的不加）；
    ///   • 修改展示格式时需要在方法体内反复搜索相关语句。
    ///
    /// 将违规聚合职责提取为独立类后：
    ///   • Recalculate() 只需调用 collector.Add(msg) 登记违规消息；
    ///   • HasViolation / Detail 由本类统一计算，格式固定在一处；
    ///   • 每次 Recalculate() 开始时直接 new 一个实例，语义等价于 Clear()，
    ///     更加简洁，无需担心忘记重置状态。
    ///
    /// ── 使用示例 ──────────────────────────────────────────────────────
    /// <code>
    /// var collector = new ViolationCollector();
    /// if (riserHeight > rule.MaxRiserHeight)
    ///     collector.Add($"踢面高 {riserHeight:F1} mm 超过上限 {rule.MaxRiserHeight} mm");
    /// HasViolation  = collector.HasViolation;
    /// ViolationDetail = collector.Detail;
    /// </code>
    ///
    /// ── 线程安全性 ────────────────────────────────────────────────────
    /// 本类不是线程安全的。但 ViewModel.Recalculate() 仅在 UI 线程调用，
    /// 无需额外的同步措施。
    /// </summary>
    internal sealed class ViolationCollector
    {
        // 存储所有违规消息的内部列表；每次实例化即为空列表，无需显式 Clear。
        private readonly List<string> _items = new List<string>();

        /// <summary>
        /// 登记一条违规消息。
        ///
        /// 消息内容应包含：违规项名称、实测值、规范要求值，
        /// 例如："实际踏步宽 240.0 mm 低于规范下限 260 mm"。
        /// 多条消息最终由 <see cref="Detail"/> 用"；\n"分隔拼接。
        /// </summary>
        /// <param name="message">描述违规情况的可读字符串</param>
        public void Add(string message) => _items.Add(message);

        /// <summary>
        /// 是否存在至少一条违规记录。
        ///
        /// 供 ViewModel 将此值赋给 HasViolation 属性，
        /// 进而控制"生成"按钮的 CanExecute 状态和违规警告区域的 Visibility。
        /// </summary>
        public bool HasViolation => _items.Any();

        /// <summary>
        /// 供界面绑定的违规详情完整文字。
        ///
        /// ── 格式 ─────────────────────────────────────────────────────
        /// 合规时返回 <see cref="string.Empty"/>（不显示警告区域）。
        /// 违规时返回多行文字，格式：
        ///
        ///   规范预警：
        ///   [消息1]；
        ///   [消息2]；
        ///   ...。
        ///   请修正后再生成。
        ///
        /// 末尾的句号由本属性统一添加，各条 Add() 时无需自行添加标点。
        /// </summary>
        public string Detail => HasViolation
            ? "规范预警：\n" + string.Join("；\n", _items) + "。\n请修正后再生成。"
            : string.Empty;

        /// <summary>
        /// 清空所有已登记的违规记录，回到初始状态。
        ///
        /// 注意：由于 Recalculate() 每次都 new 新实例，
        /// 实践中此方法通常不需要显式调用；
        /// 保留此方法是为了在可能的复用场景下提供语义完整性。
        /// </summary>
        public void Clear() => _items.Clear();
    }
}
