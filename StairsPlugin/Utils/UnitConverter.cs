using Autodesk.Revit.DB;

namespace StairsPlugin.Utils
{
    /// <summary>
    /// Revit 内部单位（英尺）与毫米之间的互转工具。
    ///
    /// 背景：Revit API 内部统一使用英尺作为长度单位，
    /// 而国内规范（GB55031-2022 等）全部以毫米表述，
    /// 两套单位体系频繁切换极易出错。
    /// 将换算集中到此处，改动单位精度时只需修改一处。
    ///
    /// 原先 MmToFt / FtToMm 分别散落在
    ///   StairGlobalEventHandler（底部私有方法）
    ///   ClearanceChecker（FtToMm 私有方法）
    ///   ViewModel（ToMm 私有方法）
    /// 三处各自为政，现统一到此类。
    /// </summary>
    internal static class UnitConverter
    {
        /// <summary>
        /// 毫米 → 英尺（Revit 内部单位）。
        /// 用于将用户输入的 mm 参数传入 Revit API 前的转换。
        /// </summary>
        public static double MmToFt(double mm)
            => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        /// <summary>
        /// 英尺（Revit 内部单位）→ 毫米。
        /// 用于将 Revit API 返回值转为可读的 mm 数值显示给用户。
        /// </summary>
        public static double FtToMm(double ft)
            => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
    }
}