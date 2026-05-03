using System.Collections.Generic;
using System.Linq;

namespace StairsPlugin.ViewModel
{
    /// <summary>
    /// 规范违规信息的聚合值对象。
    ///
    /// 设计动机：
    ///   原 Recalculate() 方法中，violations.Add() / HasViolation / ViolationDetail
    ///   三者的逻辑内聚在一起却散落于方法各处，违规消息的拼接格式也硬编码在方法体内，
    ///   修改格式时需要在多个 return 路径上逐一核对。
    ///
    ///   将这部分职责提取为独立类后：
    ///     • Recalculate() 只需调用 collector.Add(msg) 登记违规
    ///     • HasViolation / Detail 两个属性集中计算，格式统一
    ///     • 每次 Recalculate() 开始时 Clear() 重置，语义清晰
    /// </summary>
    internal sealed class ViolationCollector
    {
        private readonly List<string> _items = new List<string>();

        /// <summary>登记一条违规消息。</summary>
        public void Add(string message) => _items.Add(message);

        /// <summary>是否存在至少一条违规记录。</summary>
        public bool HasViolation => _items.Any();

        /// <summary>
        /// 供界面绑定的违规详情文字。
        /// 合规时返回空字符串，不合规时拼接所有违规项。
        /// </summary>
        public string Detail => HasViolation
            ? "规范预警：\n" + string.Join("；\n", _items) + "。\n请修正后再生成。"
            : string.Empty;

        /// <summary>每次解算开始前调用，清空上一轮结果。</summary>
        public void Clear() => _items.Clear();
    }
}