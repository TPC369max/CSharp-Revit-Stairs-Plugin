using Autodesk.Revit.DB;

namespace StairsPlugin.Model
{
    /// <summary>
    /// 楼梯局部坐标系 ↔ Revit 世界坐标系的变换工具。
    ///
    /// 坐标系定义（与生成逻辑、净空校验保持一致）：
    ///   局部 X 轴 → P1→P2 方向（楼梯爬升轴）
    ///   局部 Y 轴 → 垂直于爬升轴（梯段侧向偏移方向）
    ///   局部 Z 轴 → 竖向（与世界坐标系 Z 重合，不参与旋转）
    ///
    /// 原先相同的变换矩阵构建语句在
    ///   StairGlobalEventHandler.Execute()
    ///   ClearanceChecker.Check()
    /// 两处各写了一遍，存在不一致风险。
    /// 集中到此处后，两个调用方共享同一套定义。
    /// </summary>
    internal static class CoordinateTransform
    {
        /// <summary>
        /// 构建楼梯局部 → 世界坐标变换矩阵。
        /// 变换顺序：先绕 Z 轴旋转 angleRad，再平移到 insertionPoint。
        /// </summary>
        /// <param name="insertionPoint">P1 插入点（世界坐标，英尺）</param>
        /// <param name="angleRad">P1→P2 方向角（弧度）</param>
        public static Transform CreateStairTransform(XYZ insertionPoint, double angleRad)
        {
            Transform rotate = Transform.CreateRotation(XYZ.BasisZ, angleRad);
            Transform translate = Transform.CreateTranslation(insertionPoint);
            // 注意：Revit 变换合成顺序是 translate × rotate，
            // 即先在局部系旋转，再平移到世界位置。
            return translate.Multiply(rotate);
        }

        /// <summary>
        /// 将局部坐标 (localX, localY) 经变换矩阵投影到世界 XY 平面，
        /// Z 轴直接赋予给定的绝对高程（英尺），不参与旋转计算。
        ///
        /// Z 轴单独处理的原因：
        ///   Revit 平面视图中拾取点的 Z 值由视图截面高程决定，
        ///   不能直接信任，必须以标高 Elevation 属性覆盖，
        ///   才能保证梯段起点严格落在用户选择的楼层面上。
        /// </summary>
        /// <param name="tf">由 CreateStairTransform 得到的变换矩阵</param>
        /// <param name="localX">局部坐标系 X（沿爬升方向，英尺）</param>
        /// <param name="localY">局部坐标系 Y（侧向偏移，英尺）</param>
        /// <param name="elevFt">绝对高程（英尺）</param>
        public static XYZ LocalToWorld(Transform tf, double localX, double localY, double elevFt)
        {
            XYZ worldXY = tf.OfPoint(new XYZ(localX, localY, 0));
            return new XYZ(worldXY.X, worldXY.Y, elevFt);
        }
    }
}