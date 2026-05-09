using Autodesk.Revit.DB;

namespace StairsPlugin.Utils
{
    /// <summary>
    /// Revit 内部单位（英尺）与毫米之间的互转工具类。
    ///
    /// ── 背景 ──────────────────────────────────────────────────────────
    /// Revit API 在所有几何运算中统一使用"英尺"作为长度单位（内部单位）。
    /// 国内规范（GB55031-2022、GB55038-2025 等）全部以"毫米"表述阈值，
    /// 例如踢面高 ≤ 175 mm、梯段净高 ≥ 2200 mm。
    ///
    /// 两套单位体系之间频繁切换极易出错（差值约 304.8 倍），
    /// 若散落在各处内联换算，一旦需要修改精度或切换 API 版本，
    /// 需要在多个文件中逐一排查，维护成本极高。
    ///
    /// ── 重构前的状态 ──────────────────────────────────────────────────
    /// 原先相同的换算逻辑分散在三处：
    ///   • StairGlobalEventHandler 底部私有方法 MmToFt / FtToMm
    ///   • ClearanceChecker 内部私有方法 FtToMm（仅单向）
    ///   • ViewModel 私有方法 ToMm()
    /// 各自为政，命名不统一，且均为私有，无法跨类复用。
    ///
    /// ── 重构后的收益 ──────────────────────────────────────────────────
    /// 将换算集中到此 internal static 类后：
    ///   1. 所有调用方共享同一实现，精度一致；
    ///   2. 改动（如切换到更高精度的 UnitTypeId）只需修改此处；
    ///   3. 方法名语义明确（MmToFt / FtToMm），不易混淆方向。
    ///
    /// ── 使用约定 ──────────────────────────────────────────────────────
    ///   向 Revit API 传参前：MmToFt(用户输入的 mm 值)
    ///   从 Revit API 读取后：FtToMm(API 返回的英尺值)
    /// </summary>
    internal static class UnitConverter
    {
        /// <summary>
        /// 毫米 → 英尺（Revit 内部单位）。
        ///
        /// 用于将用户在界面输入的毫米参数（如踏步宽、梯段净宽）
        /// 传入 Revit API 之前做单位转换。
        ///
        /// 内部调用 <see cref="UnitUtils.ConvertToInternalUnits"/>，
        /// 以 <see cref="UnitTypeId.Millimeters"/> 作为源单位。
        /// </summary>
        /// <param name="mm">以毫米为单位的长度值</param>
        /// <returns>以英尺（Revit 内部单位）表示的等价长度</returns>
        public static double MmToFt(double mm)
            => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        /// <summary>
        /// 英尺（Revit 内部单位）→ 毫米。
        ///
        /// 用于将 Revit API 返回的英尺值转换为毫米，
        /// 以便在界面上以可读的 mm 形式展示给用户，
        /// 或与规范毫米阈值进行合规比较。
        ///
        /// 内部调用 <see cref="UnitUtils.ConvertFromInternalUnits"/>，
        /// 以 <see cref="UnitTypeId.Millimeters"/> 作为目标单位。
        /// </summary>
        /// <param name="ft">以英尺（Revit 内部单位）表示的长度值</param>
        /// <returns>以毫米为单位的等价长度</returns>
        public static double FtToMm(double ft)
            => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
    }
}
