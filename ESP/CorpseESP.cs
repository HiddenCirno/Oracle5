using BepInEx.Configuration;
using EFT.Communications;
using Oracle.Data;
using Oracle.Utils;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.ESP
{
    /// <summary>
    /// 尸体透视
    ///
    /// 数据来自 OracleCorpseDataManager 的后台扫描缓存，绘制层只读缓存。
    /// 尸体的愿望单战利品由 WishlistESP 负责绘制（与尸体透视联动）。
    /// </summary>
    public class CorpseESP : IOracleESP
    {
        public void SubscribeEvent()
        {
            OracleEvent.OnDrawESP += OnDrawESP;
        }

        private void OnDrawESP()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            DrawCorpseText(cam, OracleRendering.EspTextStyle);
        }

        /// <summary>绘制尸体标签</summary>
        public static void DrawCorpseText(Camera cam, GUIStyle textStyle)
        {
            if (!CorpseESPCfg.EnableCorpseESP.Value) return;
            if (textStyle == null) return;

            var corpses = OracleCorpseDataManager.CachedCorpseList;
            if (corpses == null || corpses.Count == 0) return;

            textStyle.richText = true;
            textStyle.normal.textColor = Color.white;

            for (int i = 0; i < corpses.Count; i++)
            {
                CorpseData corpse = corpses[i];

                Vector3 screenPos = cam.WorldToScreenPoint(corpse.Position);
                if (screenPos.z <= 0.01f) continue;

                // 比玩家标签略高一点，避免和尸体愿望单首行重叠
                float screenX = screenPos.x;
                float screenY = Screen.height - screenPos.y - 10f;

                GUI.Label(new Rect(screenX - 100, screenY - 20, 200, 40), corpse.FormattedText, textStyle);
            }
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(3)]
    public class CorpseESPCfg : IOracleCfg, IOracleKeyUpdate
    {
        internal static ConfigEntry<KeyCode> CorpseESPKey { get; set; }
        internal static ConfigEntry<bool> EnableCorpseESP { get; set; }
        internal static ConfigEntry<int> CorpseESPMaxDistance { get; set; }

        public void Initialize(ConfigFile config)
        {
            const string section = "3. 巡天星轨 / ESP Module";

            EnableCorpseESP = config.Bind(
                section, "启用尸体透视", true,
                new ConfigDescription("cfg_esp_module_corpse_esp_enable_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_corpse_esp_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 150
                    }));

            CorpseESPKey = config.Bind(
                section, "尸体透视快捷键", KeyCode.F5,
                new ConfigDescription("cfg_esp_module_corpse_esp_enable_key_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_corpse_esp_enable_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 149
                    }));

            CorpseESPMaxDistance = config.Bind(
                section, "尸体透视最大距离", 200,
                new ConfigDescription("cfg_esp_module_corpse_esp_max_distance_desc".i18n(),
                    new AcceptableValueRange<int>(50, 1000),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_corpse_esp_max_distance_name".i18n(),
                        IsAdvanced = false,
                        Order = 148
                    }));
        }

        public void RegisterKeyUpdate()
        {
            OracleEvent.OnUpdate += KeyUpdate;
        }

        public static void KeyUpdate()
        {
            if (Input.GetKeyDown(CorpseESPKey.Value))
            {
                EnableCorpseESP.Value = !EnableCorpseESP.Value;
                var value = EnableCorpseESP.Value;
                OracleNotify.Message(
                    string.Format("message_esp_corpse_enable".i18n(),
                        value ? "text_enable".i18n() : "text_disable".i18n()),
                    value ? ENotificationIconType.Default : ENotificationIconType.Alert,
                    GlobalCfg.MuteNotice.Value);
            }
        }
    }
}
