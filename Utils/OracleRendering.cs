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
        /// 画圆方法
        /// </summary>
        public static void DrawCircle(Vector2 center, float radius, Color color, int segments = 64)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (!IsReady) return;

            EspMaterial.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            GL.Begin(GL.LINES);
            GL.Color(color);
            float angleStep = 2f * Mathf.PI / segments;
            for (int i = 0; i < segments; i++)
            {
                float angle1 = i * angleStep;
                float angle2 = (i + 1) * angleStep;
                GL.Vertex3(center.x + Mathf.Cos(angle1) * radius, center.y + Mathf.Sin(angle1) * radius, 0);
                GL.Vertex3(center.x + Mathf.Cos(angle2) * radius, center.y + Mathf.Sin(angle2) * radius, 0);
            }
            GL.End();
            GL.PopMatrix();
        }

        /// <summary>
        /// 画线方法
        /// </summary>
        public static void DrawLine(Vector2 start, Vector2 end, Color color)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (!IsReady) return;

            EspMaterial.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            GL.Begin(GL.LINES);
            GL.Color(color);
            GL.Vertex3(start.x, start.y, 0);
            GL.Vertex3(end.x, end.y, 0);
            GL.End();
            GL.PopMatrix();
        }
    }
}
