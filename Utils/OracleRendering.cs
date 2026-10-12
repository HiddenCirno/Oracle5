using UnityEngine;

namespace Oracle.Utils
{
    /// <summary>
    /// 全局绘制样式
    ///
    /// IL2CPP 移植说明：
    ///   · GL 位于 UnityEngine.CoreModule（不是 IMGUIModule），已在 5.0 interop 中确认存在。
    ///   · GL.Vertex3 / GL.Begin / GL.Color 等均为原生方法，需在 EventType.Repaint 阶段调用，
    ///     否则会触发 Unity 的 "not in Repaint" 警告。
    ///   · Material / Shader / GUIStyle 均为普通托管可用类型。
    /// </summary>
    public static class OracleRendering
    {
        public static Material EspMaterial { get; private set; }
        public static GUIStyle EspTextStyle { get; private set; }

        private static bool _isInitialized;

        /// <summary>
        /// 初始化
        /// </summary>
        public static void Initialize()
        {
            if (_isInitialized) return;

            //线条材质
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                OracleLog.ErrorOnce("esp_shader_missing",
                    "[Oracle] 找不到 Hidden/Internal-Colored 着色器，ESP 骨骼绘制将不可用");
                return;
            }

            EspMaterial = new Material(shader);
            EspMaterial.hideFlags = HideFlags.HideAndDontSave;
            EspMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            EspMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            EspMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            EspMaterial.SetInt("_ZWrite", 0);
            EspMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);

            //文本样式
            EspTextStyle = new GUIStyle
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                richText = true
            };

            _isInitialized = true;
        }

        /// <summary>是否已完成初始化（材质可用）</summary>
        public static bool IsReady => _isInitialized && EspMaterial != null;

        /// <summary>
        /// 世界坐标 → 屏幕像素。原点在左下、Y 轴向上（与 UnityEngine 惯例一致），
        /// z 为沿相机正前方的距离（&lt;= 0 表示在身后）。调用方自行决定是否翻 Y 成
        /// IMGUI/GDI 的左上原点。
        ///
        /// ══════════ 为什么必须【绕开】cam.rect（实测结论）══════════
        ///
        /// 开 DLSS（或任何 render scale != 1 的分辨率缩放）时，游戏把世界
        /// **按 render scale 渲染到渲染目标的左下角**，再放大铺满屏幕。
        /// 实机日志（2560x1440 + DLSS Balanced）：
        ///
        ///     Screen=2560x1440
        ///     FPS Camera  rect=(0,0,0.588,0.588)  pixel=1506x847  fov=50
        ///
        /// 即相机只画进目标的左下角 1506x847（0.588 = 1506/2560 = 847/1440），
        /// 这块区域随后被等比放大到整个屏幕。
        ///
        /// 陷阱在于 `cam.rect` 描述的是【渲染视口】，不是【最终屏幕位置】：
        ///   · Camera.WorldToScreenPoint 返回的是渲染视口内的像素，
        ///     于是相机正前方（准星方向）算出的是 (753, 423.5)，
        ///     而它必须落在屏幕正中 (1280, 720) —— 恰好是 58.8%。
        ///   · 若照着"我要用真正上屏的尺寸"再乘一遍 rect，得到的公式
        ///     (r.x + vp.x*r.width)*Screen.width 展开后 **就是** WorldToScreenPoint，
        ///     两者逐位相同（实测差值 = 0.000），改了等于没改。
        ///
        /// 后果：整幅绘制被压缩到左下角的 58.8%，
        /// **误差 = (1 - 0.588) × 该点离左下角的距离**。
        ///   · 左下角附近几乎不偏 —— 所以贴脸时（目标在你脚下，viewport.y 甚至为负）
        ///     看着是"贴合"的；
        ///   · 越远越偏 —— 远处目标贴近地平线，viewport.y→0.5，
        ///     纵向误差最大到 0.412×0.5×1440 ≈ 297px；
        ///   · 越靠屏幕右上偏得越多 —— 右上角最大到 (1054, 593)px。
        /// 文字、血条、骨架共用这套投影，所以会【一起】偏。
        ///
        /// 正确做法：viewport（0~1，与渲染分辨率、render scale 都无关）
        /// 直接乘【上屏尺寸】。相机渲染到目标子区域再被放大上屏时，
        /// 子区域里画的整幅画面是等比铺满屏幕的，rect 在此处必须忽略。
        ///
        /// 注意 rect=(0,0,1,1) 时本式与 WorldToScreenPoint 完全等价 ——
        /// 也就是说关掉 DLSS 后行为不变，这个修法在两种显示模式下都成立。
        /// （唯一会需要 rect 的场景是"真·子视口且不放大"：分屏、瞄准镜黑边那种，
        ///   本工程不存在，真遇到再在此处加回 rect 分支。）
        /// </summary>
        public static Vector3 WorldToScreen(Camera cam, Vector3 world)
        {
            Vector3 vp = cam.WorldToViewportPoint(world);
            return new Vector3(
                vp.x * Screen.width,
                vp.y * Screen.height,
                vp.z);
        }

        /// <summary>
        /// 画圆方法。入参 center/radius 是**屏幕像素、原点在左下、Y 轴向上**
        /// （与 WorldToScreen 同一套约定）。
        ///
        /// ⚠ 这里刻意【不用】GL.LoadPixelMatrix()（旧实现用过，实机表现为
        ///   "自瞄识别范围圈歪到右上角、只剩一段红弧"）。它的坐标系挂在
        ///   **当前相机的视口**上，而不是屏幕上 —— Unity 文档原话：
        ///   "The coordinate (0,0) is at the bottom left corner of
        ///    current camera's viewport"。开 DLSS 时相机视口只有屏幕的 0.588
        ///   （见 WorldToScreen 处的实测），于是圆会被放大 1/0.588≈1.7 倍、
        ///   锚到左下角那块窄区域、并被裁掉大半 —— 屏幕坐标 (1280,720) 的圆心
        ///   落到"渲染视口"的右上角外侧，看上去就是右上角一条红线。
        ///   ⚠ 注意文档没说错，错的是"视口 = 屏幕"这个前提在开 DLSS 时不成立。
        ///
        ///   骨架线走的是环境矩阵（继承 OnGUI 的 GUI 空间：铺满整屏、原点左上、
        ///   Y 轴向下），已被实机验证正确；本方法统一到同一套空间，只在此处翻 Y。
        /// </summary>
        public static void DrawCircle(Vector2 center, float radius, Color color, int segments = 64)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (!IsReady) return;

            EspMaterial.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            GL.Color(color);
            float angleStep = 2f * Mathf.PI / segments;
            for (int i = 0; i < segments; i++)
            {
                float angle1 = i * angleStep;
                float angle2 = (i + 1) * angleStep;
                // GUI 空间是 Y 向下，故 sin 分量取反（等价于 Screen.height - y）
                GL.Vertex3(center.x + Mathf.Cos(angle1) * radius,
                           Screen.height - (center.y + Mathf.Sin(angle1) * radius), 0);
                GL.Vertex3(center.x + Mathf.Cos(angle2) * radius,
                           Screen.height - (center.y + Mathf.Sin(angle2) * radius), 0);
            }
            GL.End();
            GL.PopMatrix();
        }

        /// <summary>
        /// 画线方法。入参同样是**屏幕像素、原点在左下、Y 轴向上**。
        /// 空间选择与翻 Y 的理由见 DrawCircle。
        /// </summary>
        public static void DrawLine(Vector2 start, Vector2 end, Color color)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (!IsReady) return;

            EspMaterial.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            GL.Color(color);
            GL.Vertex3(start.x, Screen.height - start.y, 0);
            GL.Vertex3(end.x, Screen.height - end.y, 0);
            GL.End();
            GL.PopMatrix();
        }
    }
}
