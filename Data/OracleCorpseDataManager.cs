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
    /// 尸体数据总线
    ///
    /// IL2CPP 移植要点：
    ///   1. 【方法改名】旧版用 GameWorld.GetEverExistedPlayerByID(string)，
    ///      在 SPT5 中该方法签名变为 (int)（按 RaidID），
    ///      按 ProfileId 查询的新名字是 **GetEverExistedPlayerByProfileId(string)**。
    ///      传错会编译失败或查到错误对象。
    ///   2. 【向下转型】rootItem 是 Item，要拿到 InventoryEquipment 需用 TryCast，
    ///      不要用 as —— Il2CppInterop 的代理类型之间跨层转型行为不一致。
    ///   3. 【尸体也是 LootItem】Corpse 继承自 LootItem，因此躺在 GameWorld.LootItems 里，
    ///      扫描方式与散落物资相同（GetValuesEnumerator）。
    /// </summary>
    public static class OracleCorpseDataManager
    {
        /// <summary>全局尸体缓存表</summary>
        public static List<CorpseData> CachedCorpseList = new List<CorpseData>();

        private const float ScanInterval = 2f;
        private const int BatchSize = 300;

        /// <summary>
        /// 相位偏移 —— 与其它三个扫描器错开，避免尖峰叠加。
        /// 详见 `OracleLootDataManager.PhaseOffset` 的完整说明。
        /// </summary>
        private const float PhaseOffset = 0.37f;

        /// <summary>扫描协程</summary>
        public static IEnumerator CorpseScannerCoroutine()
        {
            var frontBuffer = new List<CorpseData>(128);
            var backBuffer = new List<CorpseData>(128);
            CachedCorpseList = frontBuffer;

            while (true)
            {
                yield return new WaitForSeconds(ScanInterval + PhaseOffset);

                if (!OracleGameState.InRaid)
                {
                    backBuffer.Clear();
                    Swap(ref frontBuffer, ref backBuffer);
                    CachedCorpseList = frontBuffer;
                    continue;
                }

                // ★ 门禁：同战利品扫描器 —— 没人看就不扫。
                //   尸体数据的消费方只有三处（已逐一核对）：
                //     · 尸体 ESP          （ESP/CorpseESP.cs）
                //     · 愿望单的尸体高亮   （ESP/WishlistESP.cs，由 EnableCorpseWishlistESP 控制）
                //     · 原生叠加层        （Overlay/OverlayPrimitiveBuilder.cs，同样读这两个开关）
                //   ⚠ 愿望单那一侧必须算进来：它打开时要在尸体里高亮愿望单物品，
                //     扫描器下面第 141 行也是按这个开关决定要不要收集该数据的。
                if (!CorpseESPCfg.EnableCorpseESP.Value
                    && !WishlistESPCfg.EnableCorpseWishlistESP.Value)
                {
                    backBuffer.Clear();
                    Swap(ref frontBuffer, ref backBuffer);
                    CachedCorpseList = frontBuffer;
                    continue;
                }

                Player me = OracleGameState.LocalPlayer;
                GameWorld world = OracleGameState.CurrentGameWorld;
                if (me == null || world == null) continue;

                var lootItems = world.LootItems;
                if (lootItems == null) continue;

                backBuffer.Clear();

                int batchCounter = 0;
                Vector3 myPos = me.Transform.position;
                int maxDistance = CorpseESPCfg.CorpseESPMaxDistance.Value;

                // 快照：分帧 yield 期间游戏仍会增删 LootItems
                var snapshot = new List<LootItem>(512);
                foreach (var li in lootItems.GetValuesEnumerator())
                {
                    if (li != null) snapshot.Add(li);
                }

                for (int i = 0; i < snapshot.Count; i++)
                {
                    // 只有 Corpse 才是尸体（其它是普通散落物资）
                    var corpse = snapshot[i]?.TryCast<Corpse>();
                    if (corpse == null) continue;

                    var go = corpse.gameObject;
                    if (go == null || !go.activeSelf) continue;

                    Vector3 corpsePos = corpse.TrackableTransform != null
                        ? corpse.TrackableTransform.position
                        : corpse.transform.position;

                    float rawDist = Vector3.Distance(myPos, corpsePos);
                    if (rawDist > maxDistance) continue;
                    int dist = Mathf.RoundToInt(rawDist);

                    // ── 身份信息 ──
                    string profileId = corpse.PlayerProfileID;
                    string result = "Scav Nikita Buyanov";

                    // 叠加层数据桥：与 result 同源，但拆成纯文本段 + 独立配色
                    string overlayTeammate = "";
                    string overlayLevel = "";
                    string overlaySide = result;
                    OracleColor overlayColor = OracleColorManager.Corpse;

                    // 阵营：默认取尸体自带值，若能从档案解析出更准确的值则覆盖。
                    // 原因同玩家 ESP —— PMC bot 的 Profile.Info.Side 被 bots/base.json
                    // 固定为 Savage，Corpse.Side 也源自该档案，因此同样不可信。
                    EPlayerSide corpseSide = corpse.Side;

                    if (!string.IsNullOrEmpty(profileId))
                    {
                        // ⚠ SPT5 中是 GetEverExistedPlayerByProfileId（按 ProfileId），
                        //   而非 GetEverExistedPlayerByID（那个现在按 int RaidID）
                        Player deadPlayer = world.GetEverExistedPlayerByProfileId(profileId);
                        if (deadPlayer != null && deadPlayer.Profile != null)
                        {
                            ProfileInfo info = deadPlayer.Profile.Info;
                            if (info != null)
                            {
                                corpseSide = OraclePlayerDataManager.GetEffectiveSide(info);

                                string name = OraclePlayerDataManager.GetPlayerName(info);
                                bool isTeammate = OraclePlayerDataManager.IsTeammate(info);

                                OraclePlayerDataManager.DeterminePlayerText(info, name, isTeammate,
                                    true, out result, out string levelText);

                                if (!string.IsNullOrEmpty(levelText))
                                {
                                    result = $"{levelText} {result}";
                                }

                                // 叠加层版本：纯文本分段（颜色单独带走）
                                OraclePlayerDataManager.GetPlayerOverlayLabel(info, name, isTeammate, true,
                                    out overlayLevel, out _,
                                    out overlayTeammate, out _,
                                    out overlaySide, out overlayColor);
                            }
                        }
                    }

                    string formattedText = string.Format("text_esp_corpse_format".i18n(),
                        OracleColorManager.Corpse, "text_esp_corpse_dead_tag".i18n(), result);

                    // ── 尸体身上的愿望单 ──
                    List<WishlistItemData> corpseWishlist = null;
                    if (WishlistESPCfg.EnableCorpseWishlistESP.Value)
                    {
                        var owner = corpse.ItemOwner;
                        Item rootItem = owner?.RootItem;
                        var equipment = rootItem?.TryCast<InventoryEquipment>();

                        if (equipment != null)
                        {
                            var wishItems = new List<WishlistItemData>();
                            OracleWishlistDataManager.CollectWishlistItems(equipment, corpseSide,
                                corpsePos, maxDistance, myPos, ref batchCounter, BatchSize,
                                entry => wishItems.Add(entry));

                            if (wishItems.Count > 0) corpseWishlist = wishItems;

                            if (batchCounter >= BatchSize)
                            {
                                batchCounter = 0;
                                yield return null;
                            }
                        }
                    }

                    backBuffer.Add(new CorpseData
                    {
                        Position = corpsePos,
                        FormattedText = formattedText,
                        Distance = dist,
                        WishlistItems = corpseWishlist,
                        OverlayTag = "text_esp_overlay_corpse_dead_tag".i18n(),
                        OverlayLevelText = overlayLevel,
                        OverlayTeammateText = overlayTeammate,
                        OverlaySideText = overlaySide,
                        OverlayColor = overlayColor
                    });
                }

                Swap(ref frontBuffer, ref backBuffer);
                CachedCorpseList = frontBuffer;
            }
        }

        private static void Swap(ref List<CorpseData> a, ref List<CorpseData> b)
        {
            var t = a; a = b; b = t;
        }
    }
}
