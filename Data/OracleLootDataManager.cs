using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using Oracle.ESP;
using Oracle.Utils;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Oracle.Data
{
    /// <summary>
    /// 战利品数据总线 —— 后台扫描并把结果发布给 ESP 绘制层
    ///
    /// IL2CPP 移植要点（均已对 5.0 interop 实测）：
    ///
    /// 1. 【协程】BasePlugin 不是 MonoBehaviour，协程挂在 OracleBehaviour 上，
    ///    且托管 IEnumerator 需经 MonoBehaviourExtensions.StartCoroutine 包装。
    ///
    /// 2. 【LootItems 遍历】GameWorld.LootItems 是 DictionaryListHydra&lt;int, LootItem&gt;，
    ///    它**没有 Count 属性**（只有 GetByIndex/GetByKey/GetValuesEnumerator），
    ///    所以不能用索引循环，必须走 GetValuesEnumerator()。
    ///
    /// 3. 【Il2Cpp 泛型序列】GetAllItems() 返回 Il2Cpp 的 IEnumerable&lt;Item&gt;，
    ///    与 .NET 的同名接口不能互转，必须经 OracleCollections 物化。
    ///    FindObjectsOfType 返回 Il2CppArrayBase，同理。
    ///
    /// 4. 【分帧】沿用旧版双缓冲 + 每 BatchSize 条让出一帧，避免千级物品造成单帧卡顿。
    ///    IL2CPP 下这一点更重要：卡顿会直接表现为掉帧而非仅仅是延迟。
    /// </summary>
    public static class OracleLootDataManager
    {
        /// <summary>全局缓存表（双缓冲的前缓冲，绘制层只读它）</summary>
        public static List<LootData> CachedLootList = new List<LootData>();

        /// <summary>容器缓存表</summary>
        public static List<LootableContainer> CachedContainers;

        /// <summary>物品等级缓存（TemplateId → 等级），避免每轮重复计算</summary>
        private static readonly Dictionary<string, int> ItemLevelCache = new Dictionary<string, int>();

        /// <summary>本地化名称缓存（全名/短名分开，互不干扰）</summary>
        private static readonly Dictionary<string, string> FullNameCache = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> ShortNameCache = new Dictionary<string, string>();

        /// <summary>价值分级界限</summary>
        public static class PriceTier
        {
            public const int Tier1 = 10000;
            public const int Tier2 = 20000;
            public const int Tier3 = 50000;
            public const int Tier4 = 100000;
            public const int Tier5 = 200000;
            public const int Tier6 = 500000;
        }

        private const int BatchSize = 300;
        private const float ScanInterval = 2f;

        /// <summary>物品栏的 TemplateId —— 尸体在数据上也是一种容器，需过滤</summary>
        private const string InventoryTemplateId = "55d7217a4bdc2d86028b456d";

        /// <summary>
        /// 扫描协程。由 OracleBehaviour 启动；进入战局时填充 CachedLootList，出局时清空。
        /// </summary>
        public static IEnumerator LootScannerCoroutine()
        {
            // 价格表只尝试一次；失败则按「无价格」模式工作（只显示名称与距离）
            HandbookPrices.EnsureLoaded();

            var frontBuffer = new List<LootData>(4096);
            var backBuffer = new List<LootData>(4096);
            var positionOffsets = new Dictionary<Vector3, int>(1024);

            CachedLootList = frontBuffer;

            while (true)
            {
                yield return new WaitForSeconds(ScanInterval);

                if (!OracleGameState.InRaid)
                {
                    // 出局清空，避免下一局开局头几秒画出上一局的物资鬼影
                    CachedContainers = null;
                    backBuffer.Clear();
                    positionOffsets.Clear();
                    Swap(ref frontBuffer, ref backBuffer);
                    CachedLootList = frontBuffer;
                    continue;
                }

                Player me = OracleGameState.LocalPlayer;
                GameWorld world = OracleGameState.CurrentGameWorld;
                if (me == null || world == null) continue;

                backBuffer.Clear();
                positionOffsets.Clear();

                Vector3 playerPos = me.Transform.position;
                int maxLootDistance = LootESPCfg.LootESPMaxDistance.Value;
                int batchCounter = 0;

                // ── 散落物资（地面 LootItem）──
                var lootItems = world.LootItems;
                if (lootItems != null)
                {
                    // 先快照：扫描期间游戏仍在增删，直接遍历活集合会抛异常
                    var snapshot = new List<LootItem>(512);
                    foreach (var li in lootItems.GetValuesEnumerator())
                    {
                        if (li != null) snapshot.Add(li);
                    }

                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        LootItem lootItem = snapshot[i];
                        if (lootItem == null) continue;

                        Item item = lootItem.Item;
                        if (item == null) continue;

                        var go = lootItem.gameObject;
                        if (go == null || !go.activeSelf) continue;

                        Vector3 pos = lootItem.transform.position;
                        if (!OracleCommon.IsInRange(maxLootDistance, playerPos, pos)) continue;

                        int dist = Mathf.RoundToInt(Vector3.Distance(playerPos, pos));
                        TryAddLootData(backBuffer, positionOffsets, item, lootItem, null,
                            GetLocalizedItemName(item), pos, dist);

                        if (++batchCounter >= BatchSize) { batchCounter = 0; yield return null; }
                    }
                }

                // ── 容器内的物资 ──
                if (CachedContainers == null)
                {
                    CachedContainers = OracleCollections.ToManagedList(
                        Object.FindObjectsOfType<LootableContainer>());
                }

                if (CachedContainers != null)
                {
                    for (int c = 0; c < CachedContainers.Count; c++)
                    {
                        LootableContainer container = CachedContainers[c];
                        if (container == null) continue;

                        var owner = container.ItemOwner;
                        if (owner == null) continue;
                        Item rootItem = owner.RootItem;
                        if (rootItem == null) continue;

                        Vector3 cpos = container.transform.position;
                        if (!OracleCommon.IsInRange(maxLootDistance, playerPos, cpos)) continue;

                        int dist = Mathf.RoundToInt(Vector3.Distance(playerPos, cpos));
                        string prefix = string.Format(
                            "text_esp_container_tag".i18n(), GetContainerName(container));

                        var items = OracleCollections.ToManagedList(rootItem.GetAllItems());
                        for (int k = 0; k < items.Count; k++)
                        {
                            Item item = items[k];
                            if (item == null || item == rootItem) continue;

                            TryAddLootData(backBuffer, positionOffsets, item, null, container,
                                GetLocalizedItemName(item), cpos, dist, prefix);

                            if (++batchCounter >= BatchSize) { batchCounter = 0; yield return null; }
                        }
                    }
                }

                Swap(ref frontBuffer, ref backBuffer);
                CachedLootList = frontBuffer;
            }
        }

        private static void Swap(ref List<LootData> a, ref List<LootData> b)
        {
            var t = a; a = b; b = t;
        }

        /// <summary>按模板缓存物品本地化名称</summary>
        public static string GetLocalizedItemName(Item item)
        {
            if (item == null) return "";

            bool fullName = LootESPCfg.ShowItemFullName.Value;
            string templateId = item.TemplateId;
            if (string.IsNullOrEmpty(templateId))
                return fullName ? item.Name.Localized() : item.ShortName.Localized();

            var cache = fullName ? FullNameCache : ShortNameCache;
            if (cache.TryGetValue(templateId, out string cached)) return cached;

            string name = fullName ? item.Name.Localized() : item.ShortName.Localized();
            cache[templateId] = name;
            return name;
        }

        /// <summary>容器显示名</summary>
        public static string GetContainerName(LootableContainer container)
        {
            string fallback = "text_esp_loot_in_the_container".i18n();
            if (container == null) return "text_esp_loot_on_the_ground".i18n();

            var owner = container.ItemOwner;
            if (owner == null) return fallback;

            Item root = owner.RootItem;
            if (root == null) return fallback;

            string name = root.ShortName.Localized();
            return string.IsNullOrEmpty(name) ? fallback : name;
        }

        /// <summary>组装一条战利品记录并写入目标列表</summary>
        private static void TryAddLootData(
            List<LootData> targetList,
            Dictionary<Vector3, int> offsetDict,
            Item item,
            LootItem lootItem,
            LootableContainer container,
            string itemName,
            Vector3 pos,
            int dist,
            string prefix = "")
        {
            if (item == null) return;

            string itemKey = item.TemplateId;
            if (itemKey == InventoryTemplateId) return;   // 过滤物品栏 / 尸体
            if (string.IsNullOrEmpty(itemName)) return;   // 过滤无名物品（内衬等）

            int itemPrice = HandbookPrices.TryGetPrice(itemKey, out int p) ? p : 0;

            int minPriceThreshold = LootESPCfg.LootESPMinPrice.Value;
            int filterLevel = GetLevelByPrice(minPriceThreshold);

            if (!ItemLevelCache.TryGetValue(itemKey, out int itemLevel))
            {
                itemLevel = GetItemLevel(item);
                ItemLevelCache[itemKey] = itemLevel;
            }

            // 特殊高亮优先于普通分级
            if (LootESPCfg.HighlightWishListItem.Value && IsWishlistItem(item.TemplateId))
            {
                itemLevel = 9;
            }
            else if (LootESPCfg.HighlightQuestItem.Value && item.Template != null && item.Template.QuestItem)
            {
                itemLevel = 8;
            }

            if (LootESPCfg.HighlightLabyrinthSpecialItem.Value &&
                ExtendWishlistItem.LabyrinthSpecialItem.ContainsKey(itemKey))
            {
                itemLevel = 9;
            }
            if (LootESPCfg.HighlightBloodyKey.Value &&
                ExtendWishlistItem.StreetsSpecialItem.ContainsKey(itemKey))
            {
                itemLevel = 9;
            }

            // 价值与等级双重过滤
            if (itemPrice < minPriceThreshold && itemLevel < filterLevel) return;

            string priceStr = FormatPrice(itemPrice);
            OracleColor color = GetColorByLevel(itemLevel);

            string fullName = string.IsNullOrEmpty(prefix) ? itemName : $"{prefix} {itemName}";
            string formatted = string.Format("text_esp_loot_format".i18n(), color, fullName,
                priceStr, OracleColorManager.Distance, dist);

            // 只有容器物品参与垂直堆叠，散落物品不参与
            int yOffset = 0;
            if (container != null)
            {
                if (!offsetDict.TryGetValue(pos, out yOffset)) yOffset = 0;
                offsetDict[pos] = yOffset + 20;
            }

            // 叠加层数据桥：同一份信息另存一份纯文本（渲染线程不做富文本解析）
            string overlayText = $"{fullName} {priceStr}";
            string overlayDistanceText = string.Format("text_esp_overlay_unit_distance".i18n(), dist);

            targetList.Add(new LootData
            {
                ItemRef = item,
                LootableItem = lootItem,
                Container = container,
                Position = pos,
                ItemLevel = itemLevel,
                Name = formatted,
                Distance = dist,
                Price = itemPrice,
                ItemColor = color,
                YOffset = yOffset,
                StackCount = item.StackObjectsCount,
                OverlayText = overlayText,
                OverlayDistanceText = overlayDistanceText
            });
        }

        /// <summary>价格缩写：1.2M / 15.3K / 8000</summary>
        private static string FormatPrice(int price)
        {
            if (price >= 1000000) return (price / 1000000f).ToString("0.##") + "M";
            if (price >= 10000) return (price / 1000f).ToString("0.#") + "K";
            return price.ToString();
        }

        /// <summary>等级 → 颜色</summary>
        public static OracleColor GetColorByLevel(int level)
        {
            switch (level)
            {
                case 9: return OracleColorManager.LootTierEX;
                case 8: return OracleColorManager.LootTierX;
                case 7: return OracleColorManager.LootTier6;
                case 6: return OracleColorManager.LootTier5;
                case 5: return OracleColorManager.LootTier4;
                case 4: return OracleColorManager.LootTier3;
                case 3: return OracleColorManager.LootTier2;
                case 2: return OracleColorManager.LootTier1;
                default: return OracleColorManager.LootTier0;
            }
        }

        /// <summary>价格 → 等级</summary>
        public static int GetLevelByPrice(int price)
        {
            if (price >= PriceTier.Tier6) return 7;
            if (price >= PriceTier.Tier5) return 6;
            if (price >= PriceTier.Tier4) return 5;
            if (price >= PriceTier.Tier3) return 4;
            if (price >= PriceTier.Tier2) return 3;
            if (price >= PriceTier.Tier1) return 2;
            return 1;
        }

        /// <summary>求物品等级：弹药按穿深、背包/胸挂按格数，其余按价格</summary>
        public static int GetItemLevel(Item item)
        {
            if (item == null) return 0;
            var template = item.Template;
            if (template == null) return 0;

            if (template is AmmoTemplate) return GetAmmoLevel(item);

            if (template is AmmoBoxTemplate)
            {
                var inner = OracleCollections.FirstOrDefault(
                    OracleCollections.ToManagedList(item.GetAllItems()),
                    x => x != null && x.Template is AmmoTemplate);
                return inner == null ? 1 : GetAmmoLevel(inner);
            }

            // Grids 定义在 CompoundItemTemplate 上，Backpack/Vest 均派生自它
            if (template is BackpackTemplate backpack)
            {
                int size = GridCellCount(backpack);
                if (size >= 35) return 6;
                if (size >= 30) return 5;
                if (size >= 25) return 4;
                if (size >= 16) return 3;
                if (size >= 12) return 2;
                return 1;
            }

            if (template is VestTemplate vest)
            {
                int size = GridCellCount(vest);
                if (size >= 20) return 5;
                if (size >= 16) return 4;
                if (size >= 12) return 3;
                if (size >= 8) return 2;
                return 1;
            }

            // 客户端 ItemTemplate 不含分类信息，只能回退到价格
            int price = HandbookPrices.TryGetPrice(item.TemplateId, out int p) ? p : 0;
            return GetLevelByPrice(price);
        }

        /// <summary>统计模板所有格子的总格数（背包/胸挂按容量分档）</summary>
        private static int GridCellCount(CompoundItemTemplate template)
        {
            var grids = template.Grids;
            if (grids == null) return 0;

            int total = 0;
            int n = grids.Length;
            for (int i = 0; i < n; i++)
            {
                var grid = grids[i];
                if (grid == null) continue;
                total += grid.GridWidth * grid.GridHeight;
            }
            return total;
        }

        /// <summary>弹药等级：按穿深划分</summary>
        public static int GetAmmoLevel(Item item)
        {
            if (item?.Template is AmmoTemplate ammo)
            {
                int pen = ammo.PenetrationPower;
                if (pen >= 60) return 6;
                if (pen >= 50) return 5;
                if (pen >= 40) return 4;
                if (pen >= 30) return 3;
                if (pen >= 20) return 2;
                if (pen >= 10) return 1;
            }
            return 1;
        }

        /// <summary>物品是否在玩家愿望单中</summary>
        public static bool IsWishlistItem(MongoID templateId)
        {
            Player me = OracleGameState.LocalPlayer;
            if (me == null) return false;

            Profile profile = me.Profile;
            if (profile == null) return false;

            var wishlist = profile.WishlistManager;
            if (wishlist == null) return false;

            return wishlist.IsInWishlist(templateId, true, out _);
        }
    }
}
