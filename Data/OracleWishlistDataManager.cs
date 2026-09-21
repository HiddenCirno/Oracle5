using EFT;
using EFT.InventoryLogic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Oracle.ESP;
using Oracle.Utils;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Oracle.Data
{
    /// <summary>
    /// 实体（玩家）身上的愿望单战利品数据总线。
    ///
    /// 职责：每 2 秒扫描所有存活玩家的装备栏，过滤出愿望单物品，
    /// 按 ProfileId 缓存，供 WishlistESP 绘制。
    /// 尸体侧的愿望单由 OracleCorpseDataManager 直接填充 CorpseData.WishlistItems。
    ///
    /// IL2CPP 移植要点：
    ///   · equipment.Slots 是 Il2CppReferenceArray&lt;Slot&gt;，用 Length + 索引访问。
    ///   · contained.GetAllItems() 返回 Il2Cpp IEnumerable，需物化后再遍历。
    ///   · 未实现的功能：愿望单查询走 Profile.WishlistManager.IsInWishlist（已在 LootDataManager 中封装）。
    /// </summary>
    public static class OracleWishlistDataManager
    {
        /// <summary>玩家身上的愿望单战利品缓存（key = player.ProfileId）</summary>
        public static Dictionary<string, List<WishlistItemData>> CachedPlayerWishlist =
            new Dictionary<string, List<WishlistItemData>>();

        private const float ScanInterval = 2f;
        private const int BatchSize = 30;

        /// <summary>
        /// 相位偏移 —— 与其它三个扫描器错开，避免尖峰叠加。
        /// 详见 `OracleLootDataManager.PhaseOffset` 的完整说明。
        /// </summary>
        private const float PhaseOffset = 1.11f;

        /// <summary>愿望单扫描协程</summary>
        public static IEnumerator WishlistScannerCoroutine()
        {
            var frontBuffer = new Dictionary<string, List<WishlistItemData>>();
            var backBuffer = new Dictionary<string, List<WishlistItemData>>();
            CachedPlayerWishlist = frontBuffer;

            int batchCounter = 0;

            while (true)
            {
                yield return new WaitForSeconds(ScanInterval + PhaseOffset);

                if (!OracleGameState.InRaid || !WishlistESPCfg.EnablePlayerWishlistESP.Value)
                {
                    backBuffer.Clear();
                    Swap(ref frontBuffer, ref backBuffer);
                    CachedPlayerWishlist = frontBuffer;
                    continue;
                }

                Player me = OracleGameState.LocalPlayer;
                GameWorld world = OracleGameState.CurrentGameWorld;
                if (me == null || world == null) continue;

                var players = world.AllAlivePlayersList;
                int count = OracleCollections.SafeCount(players);
                if (count == 0) continue;

                backBuffer.Clear();

                Vector3 myPos = me.Transform.position;
                int maxDist = PlayerESPCfg.PlayerESPMaxDistance.Value;

                for (int i = 0; i < count; i++)
                {
                    Player player = OracleCollections.SafeGet(players, i);
                    if (player == null || player == me) continue;
                    if (!OracleCommon.IsInRange(maxDist, myPos, player.Transform.position)) continue;

                    var inventory = player.Inventory;
                    if (inventory == null) continue;
                    var equipment = inventory.Equipment;
                    if (equipment == null) continue;

                    // 锚点取头顶，与玩家文字标签一致
                    Vector3 anchorPos = player.Transform.position;
                    if (player.PlayerBones?.Head?.Original != null)
                    {
                        anchorPos = player.PlayerBones.Head.Original.position;
                    }

                    // 用修正后的阵营：PMC bot 的 profile.Info.Side 被 bots/base.json
                    // 固定为 Savage，只有 Role 能分辨（见 OraclePlayerDataManager.GetEffectiveSide）
                    EPlayerSide side = EPlayerSide.Savage;
                    var profile = player.Profile;
                    if (profile?.Info != null) side = OraclePlayerDataManager.GetEffectiveSide(profile.Info);

                    var list = CollectWishlistItems(equipment, side, anchorPos, maxDist, myPos,
                        ref batchCounter, BatchSize, null);

                    if (list != null && list.Count > 0)
                    {
                        backBuffer[player.ProfileId] = list;
                    }

                    if (batchCounter >= BatchSize) { batchCounter = 0; yield return null; }
                }

                Swap(ref frontBuffer, ref backBuffer);
                CachedPlayerWishlist = frontBuffer;
            }
        }

        private static void Swap(ref Dictionary<string, List<WishlistItemData>> a,
                                 ref Dictionary<string, List<WishlistItemData>> b)
        {
            var t = a; a = b; b = t;
        }

        /// <summary>
        /// 遍历装备栏收集愿望单物品。
        ///
        /// ⚠ 两处刻意过滤：
        ///   · 安全箱（SecuredContainer）—— 打死了也拿不到，显示无意义
        ///   · PMC 的刀鞘（Scabbard）—— 近战武器不可拾取；Scav 无此限制
        /// </summary>
        public static List<WishlistItemData> CollectWishlistItems(
            InventoryEquipment equipment,
            EPlayerSide side,
            Vector3 anchorPos,
            int maxDist,
            Vector3 playerPos,
            ref int batchCounter,
            int batchSize,
            System.Action<WishlistItemData> collector)
        {
            if (equipment == null) return null;

            List<WishlistItemData> result = collector == null ? new List<WishlistItemData>() : null;

            bool isPmc = side == EPlayerSide.Usec || side == EPlayerSide.Bear;

            var slots = equipment.Slots;
            if (slots == null) return result;

            int slotCount = slots.Length;
            for (int s = 0; s < slotCount; s++)
            {
                var slot = slots[s];
                if (slot == null) continue;

                string slotId = slot.ID;
                if (slotId == "SecuredContainer") continue;
                if (isPmc && slotId == "Scabbard") continue;

                var contained = slot.ContainedItem;
                if (contained == null) continue;

                // 递归取槽位内全部物品（含容器内容、武器改装件）
                var items = OracleCollections.ToManagedList(contained.GetAllItems());
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null) continue;

                    string templateId = item.TemplateId;
                    if (string.IsNullOrEmpty(templateId)) continue;
                    if (!OracleLootDataManager.IsWishlistItem(item.TemplateId)) continue;

                    int dist = Mathf.RoundToInt(Vector3.Distance(playerPos, anchorPos));
                    var entry = BuildEntry(item, anchorPos, dist);

                    if (collector != null) collector(entry);
                    else result.Add(entry);

                    // 分帧计数只累加不清零，由外层检查阈值后 yield
                    // （内部清零会导致外层判断永远为假）
                    batchCounter++;
                }
            }

            return result;
        }

        /// <summary>构建单条愿望单条目</summary>
        private static WishlistItemData BuildEntry(Item item, Vector3 anchorPos, int dist)
        {
            string itemName = OracleLootDataManager.GetLocalizedItemName(item);
            int stackCount = item.StackObjectsCount;

            // 愿望单物品统一按最高等级 9 高亮（LootTierEX 品红）
            int price = HandbookPrices.TryGetPrice(item.TemplateId, out int p) ? p : 0;
            OracleColor color = OracleLootDataManager.GetColorByLevel(9);

            string priceStr = price >= 1000000 ? (price / 1000000f).ToString("0.##") + "M" :
                              price >= 10000 ? (price / 1000f).ToString("0.#") + "K" :
                              price.ToString();

            string countStr = stackCount > 1 ? $" x{stackCount}" : "";

            string formatted = string.Format("text_esp_wishlist_item_format".i18n(),
                color, $"{itemName}{countStr}", priceStr);

            // 叠加层数据桥：纯文本（无颜色标签）
            string overlayText = $"{itemName}{countStr} {priceStr}";

            return new WishlistItemData
            {
                Position = anchorPos,
                FormattedText = formatted,
                OverlayText = overlayText,
                Distance = dist,
                Color = color,
                YOffset = 0,
                StackCount = stackCount
            };
        }
    }
}
