using BepInEx.Configuration;
using EFT;
using Oracle.Data;
using Oracle.Utils;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.ESP
{
    /// <summary>
    /// 实体愿望单战利品透视配置。
    ///
    /// 显示玩家身上与尸体上的愿望单物品（仅过滤愿望单，避免刷屏）。
    /// 这两个开关是"联立"关系 —— 与对应的实体透视互为前置：
    ///   玩家愿望单需要「玩家透视」开启，尸体愿望单需要「尸体透视」开启，
    ///   任一关闭都不显示。这样用户关掉实体透视时不会留下无主的物品标签。
    /// </summary>
    [OracleCfgOrder(4)]
    public class WishlistESPCfg : IOracleCfg
    {
        /// <summary>显示玩家身上的愿望单物品</summary>
        internal static ConfigEntry<bool> EnablePlayerWishlistESP { get; set; }

        /// <summary>显示尸体身上的愿望单物品</summary>
        internal static ConfigEntry<bool> EnableCorpseWishlistESP { get; set; }

        public void Initialize(ConfigFile config)
        {
            const string section = "3. 巡天星轨 / ESP Module";

            EnablePlayerWishlistESP = config.Bind(
                section, "显示玩家身上愿望单物品", true,
                new ConfigDescription("cfg_esp_module_wishlist_player_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_wishlist_player_name".i18n(),
                        IsAdvanced = false,
                        Order = 145
                    }));

            EnableCorpseWishlistESP = config.Bind(
                section, "显示尸体身上愿望单物品", true,
                new ConfigDescription("cfg_esp_module_wishlist_corpse_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_wishlist_corpse_name".i18n(),
                        IsAdvanced = false,
                        Order = 144
                    }));
        }
    }

    /// <summary>
    /// 实体愿望单战利品透视（OnGUI 绘制）
    /// </summary>
    public class WishlistESP : IOracleESP
    {
        /// <summary>最多显示行数，防止一个背包几十件把屏幕刷满</summary>
        private const int MaxLines = 6;

        /// <summary>行高与起始偏移</summary>
        private const float LineHeight = 18f;

        public void SubscribeEvent()
        {
            OracleEvent.OnDrawESP += OnDrawESP;
        }

        private void OnDrawESP()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            // 与玩家透视联动
            if (WishlistESPCfg.EnablePlayerWishlistESP.Value && PlayerESPCfg.EnablePlayerESP.Value)
            {
                DrawPlayerWishlist(cam, OracleRendering.EspTextStyle);
            }
            // 与尸体透视联动
            if (WishlistESPCfg.EnableCorpseWishlistESP.Value && CorpseESPCfg.EnableCorpseESP.Value)
            {
                DrawCorpseWishlist(cam, OracleRendering.EspTextStyle);
            }
        }

        /// <summary>绘制玩家身上的愿望单物品（主标签下方逐行堆叠）</summary>
        public static void DrawPlayerWishlist(Camera cam, GUIStyle textStyle)
        {
            if (textStyle == null) return;

            var cache = OracleWishlistDataManager.CachedPlayerWishlist;
            if (cache == null || cache.Count == 0) return;

            Player me = OracleGameState.LocalPlayer;
            var players = OracleGameState.CurrentGameWorld?.AllAlivePlayersList;
            int count = OracleCollections.SafeCount(players);
            if (me == null || count == 0) return;

            Vector3 myPos = me.Transform.position;
            int maxDist = PlayerESPCfg.PlayerESPMaxDistance.Value;
            textStyle.richText = true;

            for (int i = 0; i < count; i++)
            {
                Player player = OracleCollections.SafeGet(players, i);
                if (player == null || player == me || player.PlayerBones == null) continue;
                if (!OracleCommon.IsInRange(maxDist, myPos, player.Transform.position)) continue;

                if (!cache.TryGetValue(player.ProfileId, out var list) || list == null || list.Count == 0)
                    continue;

                Vector3? headPos = OraclePlayerDataManager.GetBonePos(player.PlayerBones.Head);
                if (!headPos.HasValue) continue;

                Vector3 screenPos = cam.WorldToScreenPoint(headPos.Value + new Vector3(0, 0.3f, 0));
                if (screenPos.z <= 0.01f) continue;

                // 从主标签下方开始堆叠
                float screenX = screenPos.x;
                float baseY = Screen.height - screenPos.y + 20f;

                int lines = list.Count < MaxLines ? list.Count : MaxLines;
                for (int k = 0; k < lines; k++)
                {
                    float y = baseY + k * LineHeight;
                    GUI.Label(new Rect(screenX - 100, y - 10, 200, 20), list[k].FormattedText, textStyle);
                }
            }
        }

        /// <summary>绘制尸体身上的愿望单物品（尸体标签下方逐行堆叠）</summary>
        public static void DrawCorpseWishlist(Camera cam, GUIStyle textStyle)
        {
            if (textStyle == null) return;

            var corpses = OracleCorpseDataManager.CachedCorpseList;
            if (corpses == null || corpses.Count == 0) return;

            textStyle.richText = true;

            for (int i = 0; i < corpses.Count; i++)
            {
                CorpseData corpse = corpses[i];
                var list = corpse.WishlistItems;
                if (list == null || list.Count == 0) continue;

                Vector3 screenPos = cam.WorldToScreenPoint(corpse.Position);
                if (screenPos.z <= 0.01f) continue;

                // 尸体标签自身在 -10f，这里从其下方继续
                float screenX = screenPos.x;
                float baseY = Screen.height - screenPos.y + 10f;

                int lines = list.Count < MaxLines ? list.Count : MaxLines;
                for (int k = 0; k < lines; k++)
                {
                    float y = baseY + k * LineHeight;
                    GUI.Label(new Rect(screenX - 100, y - 10, 200, 20), list[k].FormattedText, textStyle);
                }
            }
        }
    }
}
