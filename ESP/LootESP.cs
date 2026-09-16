using BepInEx.Configuration;
using EFT.Communications;
using Oracle.Data;
using Oracle.Utils;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.ESP
{
    /// <summary>
    /// 物资透视
    ///
    /// 数据来源是 OracleLootDataManager 的后台扫描缓存（双缓冲的前缓冲），
    /// 绘制层只读缓存、从不触碰游戏对象 —— 这是避免主线程卡顿的关键设计，
    /// 在 IL2CPP 下尤其重要（非主线程或重负载访问游戏对象会直接崩）。
    /// </summary>
    public class LootESP : IOracleESP
    {
        public void SubscribeEvent()
        {
            OracleEvent.OnDrawESP += OnDrawESP;
        }

        private void OnDrawESP()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            DrawLootFOVCircle();
            DrawLootText(cam, OracleRendering.EspTextStyle);
        }

        /// <summary>绘制物资文本</summary>
        public static void DrawLootText(Camera cam, GUIStyle textStyle)
        {
            if (textStyle == null) return;

            var lootList = OracleLootDataManager.CachedLootList;
            if (lootList == null || lootList.Count == 0) return;

            Vector2 screenCenter = new Vector2(Screen.width / 2f, Screen.height / 2f);
            float fovRadius = LootESPCfg.LootESPFovRange.Value;

            int fovMinPrice = LootESPCfg.LootESPFovMinPrice.Value;
            int fovMinLevel = OracleLootDataManager.GetLevelByPrice(fovMinPrice);

            for (int i = 0; i < lootList.Count; i++)
            {
                LootData loot = lootList[i];

                Vector3 screenPos = cam.WorldToScreenPoint(loot.Position);
                if (screenPos.z <= 0.01f) continue;   // 背后不画

                float screenX = screenPos.x;
                // YOffset 展开容器内的多条战利品，避免文字互相覆盖
                float screenY = Screen.height - screenPos.y + loot.YOffset;

                // 约束透视：低价低级的物品只在中心 FOV 内显示
                if (LootESPCfg.EnableLootESPFov.Value)
                {
                    if (loot.Price < fovMinPrice && loot.ItemLevel < fovMinLevel)
                    {
                        float distToCenter = Vector2.Distance(screenCenter, new Vector2(screenX, screenY));
                        if (distToCenter > fovRadius) continue;
                    }
                }

                bool isContainerLoot = loot.Container != null;
                if (isContainerLoot && !LootESPCfg.EnableContainerLootESP.Value) continue;
                if (!isContainerLoot && !LootESPCfg.EnableLooseLootESP.Value) continue;

                GUI.Label(new Rect(screenX - 100, screenY - 20, 200, 40), loot.Name, textStyle);
            }
        }

        /// <summary>绘制约束范围圈</summary>
        public static void DrawLootFOVCircle()
        {
            if (!LootESPCfg.ShowLootESPFov.Value) return;

            Vector2 screenCenter = new Vector2(Screen.width / 2f, Screen.height / 2f);
            float fovRadius = LootESPCfg.LootESPFovRange.Value;

            OracleRendering.DrawCircle(screenCenter, fovRadius, OracleColorManager.LootCircle, 64);
        }
    }

    /// <summary>
    /// 配置项定义
    ///
    /// ⚠ 刻意避开枚举类型配置项：ConfigurationManager 的枚举/列表控件走
    ///   GUI.SelectionGrid → GUI.DoButtonGrid，而这两个方法在本环境的 IL2CPP
    ///   unstrip 阶段失败，一旦渲染就每帧抛异常。全部使用 bool / int / KeyCode 可规避。
    /// </summary>
    [OracleCfgOrder(3)]
    public class LootESPCfg : IOracleCfg, IOracleKeyUpdate
    {
        internal static ConfigEntry<KeyCode> LooseLootESPKey { get; set; }
        internal static ConfigEntry<KeyCode> ContainerLootESPKey { get; set; }
        internal static ConfigEntry<bool> EnableContainerLootESP { get; set; }
        internal static ConfigEntry<bool> EnableLooseLootESP { get; set; }
        internal static ConfigEntry<int> LootESPMaxDistance { get; set; }
        internal static ConfigEntry<int> LootESPMinPrice { get; set; }
        internal static ConfigEntry<bool> EnableLootESPFov { get; set; }
        internal static ConfigEntry<bool> ShowLootESPFov { get; set; }
        internal static ConfigEntry<int> LootESPFovRange { get; set; }
        internal static ConfigEntry<int> LootESPFovMinPrice { get; set; }
        internal static ConfigEntry<bool> ShowItemFullName { get; set; }
        internal static ConfigEntry<bool> HighlightWishListItem { get; set; }
        internal static ConfigEntry<bool> HighlightQuestItem { get; set; }
        internal static ConfigEntry<bool> HighlightLabyrinthSpecialItem { get; set; }
        internal static ConfigEntry<bool> HighlightBloodyKey { get; set; }

        public void Initialize(ConfigFile config)
        {
            const string section = "3. 巡天星轨 / ESP Module";

            EnableLooseLootESP = config.Bind(
                section, "启用松散物资透视", true,
                new ConfigDescription("cfg_esp_module_looseloot_esp_enable_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_looseloot_esp_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 180
                    }));

            LooseLootESPKey = config.Bind(
                section, "散落物资透视快捷键", KeyCode.F3,
                new ConfigDescription("cfg_esp_module_looseloot_esp_enable_key_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_looseloot_esp_enable_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 179
                    }));

            EnableContainerLootESP = config.Bind(
                section, "启用容器物资透视", true,
                new ConfigDescription("cfg_esp_module_staticloot_esp_enable_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_staticloot_esp_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 178
                    }));

            ContainerLootESPKey = config.Bind(
                section, "容器物资透视快捷键", KeyCode.F4,
                new ConfigDescription("cfg_esp_module_staticloot_esp_enable_key_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_staticloot_esp_enable_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 177
                    }));

            ShowItemFullName = config.Bind(
                section, "显示物品全名", false,
                new ConfigDescription("cfg_esp_module_loot_esp_show_full_name_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_loot_esp_show_full_name_name".i18n(),
                        IsAdvanced = false,
                        Order = 176
                    }));

            LootESPMaxDistance = config.Bind(
                section, "透视范围", 200,
                new ConfigDescription("cfg_esp_module_loot_esp_max_distance_desc".i18n(),
                    new AcceptableValueRange<int>(50, 1000),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_loot_esp_max_distance_name".i18n(),
                        IsAdvanced = false,
                        Order = 175
                    }));

            LootESPMinPrice = config.Bind(
                section, "价格过滤", 15000,
                new ConfigDescription("cfg_esp_module_loot_esp_min_price_desc".i18n(),
                    new AcceptableValueRange<int>(1, 1000000),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_loot_esp_min_price_name".i18n(),
                        IsAdvanced = false,
                        Order = 174
                    }));

            EnableLootESPFov = config.Bind(
                section, "启用约束透视", true,
                new ConfigDescription("cfg_esp_module_loot_esp_fov_enable_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_loot_esp_fov_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 173
                    }));

            ShowLootESPFov = config.Bind(
                section, "显示约束透视范围", true,
                new ConfigDescription("cfg_esp_module_loot_esp_show_fov_enable_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_loot_esp_show_fov_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 172
                    }));

            LootESPFovRange = config.Bind(
                section, "约束透视范围", 100,
                new ConfigDescription("cfg_esp_module_loot_esp_fov_radius_desc".i18n(),
                    new AcceptableValueRange<int>(0, 1000),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_loot_esp_fov_radius_name".i18n(),
                        IsAdvanced = false,
                        Order = 171
                    }));

            LootESPFovMinPrice = config.Bind(
                section, "约束透视白名单价格", 150000,
                new ConfigDescription("cfg_esp_module_loot_esp_fov_min_price_desc".i18n(),
                    new AcceptableValueRange<int>(1000, 10000000),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_loot_esp_fov_min_price_name".i18n(),
                        IsAdvanced = false,
                        Order = 170
                    }));

            HighlightWishListItem = config.Bind(
                section, "高亮愿望单物品", true,
                new ConfigDescription("cfg_esp_module_loot_esp_highlight_wishlist_item_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_loot_esp_highlight_wishlist_item_name".i18n(),
                        IsAdvanced = false,
                        Order = 169
                    }));

            HighlightQuestItem = config.Bind(
                section, "高亮任务物品", true,
                new ConfigDescription("cfg_esp_module_loot_esp_highlight_quest_item_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_loot_esp_highlight_quest_item_name".i18n(),
                        IsAdvanced = false,
                        Order = 168
                    }));

            HighlightLabyrinthSpecialItem = config.Bind(
                section, "透视迷宫道具", true,
                new ConfigDescription("cfg_esp_module_loot_esp_highlight_labyrinth_item_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_loot_esp_highlight_labyrinth_item_name".i18n(),
                        IsAdvanced = false,
                        Order = 167
                    }));

            HighlightBloodyKey = config.Bind(
                section, "透视血色钥匙", true,
                new ConfigDescription("cfg_esp_module_loot_esp_highlight_bloody_key_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_loot_esp_highlight_bloody_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 166
                    }));
        }

        public void RegisterKeyUpdate()
        {
            OracleEvent.OnUpdate += KeyUpdate;
        }

        public static void KeyUpdate()
        {
            if (Input.GetKeyDown(LooseLootESPKey.Value))
            {
                EnableLooseLootESP.Value = !EnableLooseLootESP.Value;
                var value = EnableLooseLootESP.Value;
                OracleNotify.Message(
                    string.Format("message_loot_esp_looseloot_enable".i18n(),
                        value ? "text_enable".i18n() : "text_disable".i18n()),
                    value ? ENotificationIconType.Default : ENotificationIconType.Alert,
                    GlobalCfg.MuteNotice.Value);
            }

            if (Input.GetKeyDown(ContainerLootESPKey.Value))
            {
                EnableContainerLootESP.Value = !EnableContainerLootESP.Value;
                var value = EnableContainerLootESP.Value;
                OracleNotify.Message(
                    string.Format("message_loot_esp_staticloot_enable".i18n(),
                        value ? "text_enable".i18n() : "text_disable".i18n()),
                    value ? ENotificationIconType.Default : ENotificationIconType.Alert,
                    GlobalCfg.MuteNotice.Value);
            }
        }
    }
}
