using Autodesk.Revit.DB;

namespace StairsPlugin.Model
{
    /// <summary>
    /// 楼梯局部坐标系 ↔ Revit 世界坐标系的变换工具类。
    ///
    /// ── 坐标系定义 ────────────────────────────────────────────────────
    /// 本插件为双跑平行楼梯定义了一套"局部坐标系"，以 P1（插入点）为原点：
    ///
    ///   局部 X 轴 → P1→P2 方向，即楼梯的爬升水平轴。
    ///               梯段沿该轴铺展，run1 从 X=0 走向 X=run1Length，
    ///               run2 从 X=run2Length 走向 X=0（反向）。
    ///
    ///   局部 Y 轴 → 垂直于爬升轴（水平面内），控制两跑的横向偏移。
    ///               右旋（顺时针）时 run1 在 Y<0 侧，run2 在 Y>0 侧；
    ///               左旋（逆时针）时相反。
    ///
    ///   局部 Z 轴 → 竖向，与世界坐标系 Z 轴重合，不参与旋转计算。
    ///               Z 值直接赋予绝对高程（英尺），由调用方传入。
    ///
    /// ── Z 轴单独处理的原因 ────────────────────────────────────────────
    /// Revit 平面视图中拾取点的 Z 值由视图截面高程决定，并不等于楼层标高，
    /// 若直接参与矩阵变换会导致梯段起点飘离楼层面。
    /// 因此 <see cref="LocalToWorld"/> 对 X/Y 走旋转+平移变换，
    /// 对 Z 则直接赋予外部传入的绝对高程，两者完全解耦。
    ///
    /// ── 重构动机 ──────────────────────────────────────────────────────
    /// 原先相同的变换矩阵构建语句（rotate × translate）在以下两处各写了一遍：
    ///   • StairGlobalEventHandler.Execute()   ← 生成梯段端点时使用
    ///   • ClearanceChecker.Check()            ← 净空射线起点计算时使用
    ///
    /// 两处代码若因复制粘贴产生微小差异（如旋转轴写错、乘法顺序颠倒），
    /// 射线起点与实际梯段位置将出现偏差，导致净空校验误报。
    /// 集中到此静态类后，两个调用方共享同一套定义，彻底消除该风险。
    /// </summary>
    internal static class CoordinateTransform
    {
        /// <summary>
        /// 构建楼梯局部坐标系 → Revit 世界坐标系的仿射变换矩阵。
        ///
        /// ── 变换顺序 ──────────────────────────────────────────────────
        /// 1. 绕世界 Z 轴旋转 angleRad（将局部 X 轴对齐到 P1→P2 方向）
        /// 2. 平移到 insertionPoint（将局部原点移到 P1 的世界位置）
        ///
        /// Revit <see cref="Transform"/> 的合成遵循"先左后右"规则：
        ///   translate.Multiply(rotate)  表示"先旋转，再平移"，
        ///   等价于数学上的 T × R，即对任意点 p：result = T(R(p))。
        ///
        /// ── 为什么不用 Transform.Identity 逐步叠加 ────────────────────
        /// 逐步叠加会引入浮点累积误差，且代码可读性差；
        /// 直接构造旋转和平移再相乘，语义清晰、数值精确。
        /// </summary>
        /// <param name="insertionPoint">
        ///   P1 插入点的世界坐标（Revit 内部单位，英尺）。
        ///   作为平移向量的终点，也是局部坐标系的世界原点。
        /// </param>
        /// <param name="angleRad">
        ///   P1→P2 方向相对于世界 X 轴的角度（弧度）。
        ///   由 Math.Atan2(P2.Y - P1.Y, P2.X - P1.X) 计算得到。
        /// </param>
        /// <returns>
        ///   可将局部坐标直接变换到 Revit 世界坐标的 <see cref="Transform"/> 对象。
        /// </returns>
        public static Transform CreateStairTransform(XYZ insertionPoint, double angleRad)
        {
            Transform rotate = Transform.CreateRotation(XYZ.BasisZ, angleRad);
            Transform translate = Transform.CreateTranslation(insertionPoint);
            // Revit 变换合成语义：translate.Multiply(rotate) = T × R
            // 即对局部点 p：世界坐标 = translate( rotate(p) )
            // 顺序必须正确：先旋转对齐方向，再平移到插入点位置。
            return translate.Multiply(rotate);
        }

        /// <summary>
        /// 将局部坐标 (localX, localY) 通过变换矩阵投影到世界 XY 平面，
        /// Z 轴直接赋予调用方提供的绝对高程（英尺），不参与旋转计算。
        ///
        /// ── 调用方式示例 ──────────────────────────────────────────────
        /// <code>
        /// Transform tf = CoordinateTransform.CreateStairTransform(p1, angleRad);
        /// // 第一跑第 i 步踏步面中心点
        /// XYZ origin = CoordinateTransform.LocalToWorld(tf, i * treadFt, run1Y, stepElevFt);
        /// </code>
        ///
        /// ── Z 轴为何不经过 tf.OfPoint ─────────────────────────────────
        /// <see cref="Transform.OfPoint"/> 会对 XYZ 三个分量同时施加旋转+平移，
        /// 但楼梯的 Z（高程）是由标高和踢面高累加而来的绝对值，
        /// 不应随水平旋转矩阵变形。因此仅对 (localX, localY, 0) 做变换，
        /// 取结果的 X/Y 分量，再单独附加外部传入的 elevFt。
        /// </summary>
        /// <param name="tf">
        ///   由 <see cref="CreateStairTransform"/> 得到的变换矩阵。
        /// </param>
        /// <param name="localX">
        ///   局部坐标系 X 分量（沿爬升方向，英尺）。
        ///   对 run1 通常为 i * treadDepthFt；对 run2 为从远端向近端递减。
        /// </param>
        /// <param name="localY">
        ///   局部坐标系 Y 分量（梯段侧向偏移，英尺）。
        ///   正负值由顺/逆时针盘旋方向决定。
        /// </param>
        /// <param name="elevFt">
        ///   该点的绝对高程（英尺）。
        ///   通常为 baseLevel.Elevation + n * riserFt，n 为踢面计数。
        /// </param>
        /// <returns>
        ///   Revit 世界坐标系中的三维点，XY 经过旋转+平移，Z 直接赋值。
        /// </returns>
        public static XYZ LocalToWorld(Transform tf, double localX, double localY, double elevFt)
        {
            // 仅对水平分量做变换，Z 传 0 以避免旋转矩阵影响高程
            XYZ worldXY = tf.OfPoint(new XYZ(localX, localY, 0));
            // 用绝对高程覆盖变换后的 Z 分量（变换结果的 Z 无实际意义）
            return new XYZ(worldXY.X, worldXY.Y, elevFt);
        }
    }
}
