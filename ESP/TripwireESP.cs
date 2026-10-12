using BepInEx.Configuration;
using EFT;
using Oracle.Data;
using Oracle.Utils;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.ESP
{
    /// <summary>
    /// 绊雷透视
    ///
    /// IL2CPP 移植要点：GL 绘制必须整体处于 EventType.Repaint 阶段，
    /// 否则 Unity 会拒绝且可能破坏 IMGUI 的 clip 栈平衡。
    /// 因此这里把 Begin/End 整段包在重绘判断内，而不是只在其中某几条指令上判断。
    /// </summary>
    public class TripwireESP : IOracleESP
    {
        public void SubscribeEvent()
        {
            OracleEvent.OnDrawESP += OnDrawESP;
        }

        private void OnDrawESP()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            DrawTripwireESP(cam, OracleRendering.EspTextStyle);
        }

        /// <summary>
        /// 绘制绊雷连线与距离信息
        /// </summary>
        public static void DrawTripwireESP(Camera cam, GUIStyle textStyle)
        {
            if (!TripwireESPCfg.EnableTripwireESP.Value) return;

            var tripwires = OracleTripwireManager.CachedTripwires;
            if (tripwires == null || tripwires.Count == 0) return;

            Player me = OracleGameState.LocalPlayer;
            if (me == null) return;

            Vector3 playerPos = me.Transform.position;
            const int maxDistance = 25;   // 绊雷只在近距离有意义，沿用旧版硬编码值

            // ── 连线（仅重绘阶段）──
            if (Event.current.type == EventType.Repaint && OracleRendering.IsReady)
            {
                OracleRendering.EspMaterial.SetPass(0);
                GL.PushMatrix();
                GL.Begin(GL.LINES);
                GL.Color(OracleColorManager.Tripwire);

                for (int i = 0; i < tripwires.Count; i++)
                {
                    TripwireData trap = tripwires[i];
                    if (!OracleCommon.IsInRange(maxDistance, playerPos, trap.CenterPos)) continue;

                    Vector3 a = OracleRendering.WorldToScreen(cam, trap.StartPos);
                    Vector3 b = OracleRendering.WorldToScreen(cam, trap.EndPos);
                    if (a.z <= 0.01f || b.z <= 0.01f) continue;

                    // ⚠ 不要用 GL.LoadPixelMatrix()。它的坐标系挂在【相机视口】上，
                    //   开 DLSS 时视口只有屏幕的 0.588，画出来的线会被放大 1.7 倍、
                    //   锚到左下角那块窄区域里（自瞄 FOV 圈踩过同一个坑，
                    //   表现为右上角一段红弧，详见 OracleRendering.DrawCircle 的注释）。
                    //   这里继承 OnGUI 的环境 GUI 空间（铺满整屏、原点左上、Y 向下），
                    //   与 OracleRendering.WorldToScreen 的左下原点约定差一个 Y 翻转。
                    GL.Vertex3(a.x, Screen.height - a.y, 0);
                    GL.Vertex3(b.x, Screen.height - b.y, 0);
                }

                GL.End();
                GL.PopMatrix();
            }

            // ── 文字 ──
            if (textStyle == null) return;
            textStyle.richText = true;

            for (int i = 0; i < tripwires.Count; i++)
            {
                TripwireData trap = tripwires[i];
                if (!OracleCommon.IsInRange(maxDistance, playerPos, trap.CenterPos)) continue;

                Vector3 center = OracleRendering.WorldToScreen(cam, trap.CenterPos);
                if (center.z <= 0.01f) continue;

                int dist = Mathf.RoundToInt(Vector3.Distance(playerPos, trap.CenterPos));
                string text = string.Format("text_esp_tripwire".i18n(),
                    OracleColorManager.Tripwire, OracleColorManager.Distance, dist);

                GUI.Label(new Rect(center.x - 50, Screen.height - center.y - 20, 100, 40), text, textStyle);
            }
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(3)]
    public class TripwireESPCfg : IOracleCfg, IOracleKeyUpdate
    {
        internal static ConfigEntry<bool> EnableTripwireESP { get; set; }

        public void Initialize(ConfigFile config)
        {
            EnableTripwireESP = config.Bind(
                "3. 巡天星轨 / ESP Module",
                "启用绊雷透视",
                true,
                new ConfigDescription(
                    "cfg_esp_module_tripwire_esp_enable_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_tripwire_esp_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 140
                    }
                )
            );
        }

        public void RegisterKeyUpdate()
        {
            // 绊雷透视无快捷键（沿用旧版设计）
        }
    }
}
