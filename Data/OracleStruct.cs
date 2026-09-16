using EFT.Interactive;
using EFT.InventoryLogic;
using Oracle.Utils;
using System.Collections.Generic;
using UnityEngine;

namespace Oracle.Data
{
    /// <summary>
    /// 实体（玩家）显示信息
    /// </summary>
    public readonly struct EntityDisplayInfo
    {
        public readonly string Name;
        public readonly string SideText;
        public readonly string LevelText;
        public readonly int Distance;

        public EntityDisplayInfo(string name, string sideText, string levelText, int distance)
        {
            Name = name;
            SideText = sideText;
            LevelText = levelText;
            Distance = distance;
        }

        /// <summary>格式化输出结果（富文本，供 OnGUI 使用）</summary>
        public string ToEspString()
            => string.Format("text_esp_player".i18n(), LevelText, SideText, OracleColorManager.Distance, Distance).Trim();
    }

    /// <summary>
    /// 战利品数据结构
    /// </summary>
    public struct LootData
    {
        /// <summary>物品实例</summary>
        public Item ItemRef;

        /// <summary>地面物品组件（散落物资）；容器内物品为 null</summary>
        public LootItem LootableItem;

        /// <summary>所属容器；散落物资为 null</summary>
        public LootableContainer Container;

        /// <summary>世界坐标（散落物资取自身，容器内物品取容器坐标）</summary>
        public Vector3 Position;

        /// <summary>物品等级 0..9</summary>
        public int ItemLevel;

        /// <summary>已格式化的富文本显示串</summary>
        public string Name;

        /// <summary>距离（米）</summary>
        public int Distance;

        /// <summary>价格（卢布）；价格表不可用时为 0</summary>
        public int Price;

        /// <summary>等级对应颜色</summary>
        public OracleColor ItemColor;

        /// <summary>容器内物品的垂直堆叠偏移，避免文字互相覆盖</summary>
        public int YOffset;

        /// <summary>堆叠数量</summary>
        public int StackCount;

        // ── 叠加层数据桥专用 ──
        // GDI 渲染线程拿不到富文本（也不该做字符串解析），
        // 因此这里另存一份「纯文本 + 独立配色」。

        /// <summary>纯文本："名称 价格"（无颜色标签）</summary>
        public string OverlayText;

        /// <summary>距离段文本（单独配色，故与主文本分开）</summary>
        public string OverlayDistanceText;
    }

    /// <summary>绊雷数据缓存</summary>
    public struct TripwireData
    {
        /// <summary>绊雷一端坐标</summary>
        public Vector3 StartPos;

        /// <summary>绊雷另一端坐标</summary>
        public Vector3 EndPos;

        /// <summary>中点坐标（用于距离过滤与文字锚点）</summary>
        public Vector3 CenterPos;

        /// <summary>叠加层数据桥：标签纯文本</summary>
        public string OverlayLabel;
    }

    /// <summary>
    /// 实体（玩家/尸体）身上的愿望单战利品条目
    /// </summary>
    public struct WishlistItemData
    {
        /// <summary>条目锚点世界坐标（玩家头顶 / 尸体坐标）</summary>
        public Vector3 Position;

        /// <summary>富文本："{颜色}物品名 x数量 {价格}"</summary>
        public string FormattedText;

        /// <summary>距离</summary>
        public int Distance;

        /// <summary>物品等级色（愿望单固定为 9 级高亮）</summary>
        public OracleColor Color;

        /// <summary>多条目垂直堆叠偏移（由绘制层按索引计算）</summary>
        public int YOffset;

        /// <summary>堆叠数量</summary>
        public int StackCount;

        /// <summary>叠加层数据桥：纯文本（"{物品名 x数量} {价格}"）</summary>
        public string OverlayText;
    }

    /// <summary>
    /// 尸体透视数据
    /// </summary>
    public struct CorpseData
    {
        /// <summary>尸体世界坐标</summary>
        public Vector3 Position;

        /// <summary>富文本："[已死亡] 阵营 名字"</summary>
        public string FormattedText;

        /// <summary>距离</summary>
        public int Distance;

        /// <summary>尸体身上的愿望单战利品（仅过滤愿望单，由尸体扫描协程填充）</summary>
        public List<WishlistItemData> WishlistItems;

        // ── 叠加层数据桥专用：与 FormattedText 同源，拆成纯文本段 + 独立配色 ──

        /// <summary>死尸标记段（固定为 Corpse 色）</summary>
        public string OverlayTag;

        /// <summary>等级段文本</summary>
        public string OverlayLevelText;

        /// <summary>友军段文本</summary>
        public string OverlayTeammateText;

        /// <summary>阵营段文本</summary>
        public string OverlaySideText;

        /// <summary>阵营段颜色</summary>
        public OracleColor OverlayColor;
    }

    /// <summary>
    /// 高亮拓展 —— 特殊物品字典
    ///
    /// ⚠ 这些 TemplateId 是从 SPT5 的 locales/global/ch.json 反查得到的
    ///   （按物品名匹配），已逐一核对：
    ///     · 原 Oracle 字典中的 8 条在 SPT5 中全部仍然有效
    ///     · 新增本版本追加的「行刑室钥匙」与「迷宫钥匙」
    ///     · 「观察室钥匙」原字典已有，无需重复添加
    ///   若日后游戏更新导致 ID 变化，可用 codespace/_tools/lookup_keys.py 重新反查。
    /// </summary>
    public static class ExtendWishlistItem
    {
        /// <summary>迷宫（Labyrinth）特殊道具</summary>
        public static readonly Dictionary<string, string> LabyrinthSpecialItem = new Dictionary<string, string>
        {
            { "679baa2c61f588ae2b062a24", "一号房钥匙" },
            { "679baa4f59b8961f370dd683", "二号房钥匙" },
            { "679baa5a59b8961f370dd685", "三号房钥匙" },
            { "679baa9091966fe40408f149", "四号房钥匙" },
            { "679baace4e9ca6b3d80586b2", "观察室钥匙" },
            { "679baae891966fe40408f14c", "行刑室钥匙" },   // SPT5 新增
            { "679bab714e9ca6b3d80586b4", "停尸房钥匙" },
            { "679bac1d61f588ae2b062a26", "迷宫钥匙" },     // SPT5 新增
            { "678fa929819ddc4c350c0317", "阀门手轮" },
            { "67ab3d4b83869afd170fdd3f", "BBQ-S43 喷枪" }
        };

        /// <summary>街区特殊道具</summary>
        public static readonly Dictionary<string, string> StreetsSpecialItem = new Dictionary<string, string>
        {
            { "64d4b23dc1b37504b41ac2b6", "生锈的带血钥匙" }
        };
    }
}
