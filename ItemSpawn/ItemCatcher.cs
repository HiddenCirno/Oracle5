using BepInEx.Configuration;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using Oracle.Data;
using Oracle.Utils;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.ItemSpawn
{
    /// <summary>
    /// 物品实例捕获器 —— 跟踪"鼠标当前悬停在哪件物品上"。
    ///
    /// 用途：让玩家在背包/仓库里把鼠标移到某件物品上，按保存键把它存进工作区，
    /// 之后再按复制键就能凭空复制出来。
    ///
    /// ══════════════ IL2CPP 移植要点 ══════════════
    ///
    /// 4.1 用 Harmony 的 Traverse 反射去读私有字段：
    ///     Traverse.Create(__instance).Field("_item").GetValue&lt;Item&gt;()
    ///     Traverse.Create(__instance).Property("Item").GetValue&lt;Item&gt;()
    ///
    /// 这在 IL2CPP 下**必然失败** —— Traverse 走的是 .NET 反射，
    /// 拿到的是 Il2CppInterop 代理类的成员，与原生对象内存无关，读出来永远是 null。
    ///
    /// 而 5.0 的 interop 程序集已把这些字段生成为**公开属性**（Il2CppInterop 会为
    /// 每个原生字段生成 NativeFieldInfoPtr 与同名属性），因此直接访问即可：
    ///     EntityIcon._item                  → 公开属性
    ///     TradingRequisitePanel._itemContext → 公开属性
    ///
    /// ⚠ 补丁目标变更（4.1 → 5.0）：
    ///   EntityIcon.CG_Awake              → 已不存在。5.0 只有 Awake（Unity 生命周期，
    ///                                      挂钩有方法体合并风险），故改挂 Show(Item)
    ///                                      —— 语义等价（都是"这个图标开始展示某件物品"）
    ///                                      且签名更明确。
    ///   HideoutItemView.OnPointerEnter   → 已不存在。改挂 Show(Item, ...)。
    ///   TradingRequisitePanel.CG_Awake   → 已不存在。改挂 Show(Requisite, Assortment)
    ///                                      （但要读的 _itemContext 由后续赋值，
    ///                                        故这里退化为读取当前属性，见下方注释）。
    /// </summary>
    public class ItemCatcher : IOracleKeyUpdate
    {
        /// <summary>当前鼠标悬停的物品实例</summary>
        public static Item SelectedItem { get; set; }

        /// <summary>已保存的物品实例（复制功能的来源）</summary>
        public static Item SavedItem { get; set; }

        public void RegisterKeyUpdate()
        {
            OracleEvent.OnUpdate += KeyUpdate;
        }

        /// <summary>
        /// 快捷键监听：把当前悬停的物品保存下来。
        ///
        /// 刻意**不**清洗状态 —— 带勾与否是玩家的战斗成果，应由玩家自己决定，
        /// 因此这里只做"复制 + 重编号"，保留原本的耐久与带勾标记。
        /// </summary>
        public void KeyUpdate()
        {
            if (SelectedItem == null) return;

            if (ItemSpawnerCfg.CopyItemKey.Value.IsDown())
            {
                string itemID = SelectedItem.TemplateId;
                string itemName = SelectedItem.Name.Localized();

                // 5.0 起 CloneItem 需要 ID 生成器（见 ItemSpawner 注释）
                // ⚠ SameIdGenerator 是嵌套在 ItemExtensions 里的类型，需写全限定名
                var cloned = ItemExtensions.CloneItem(
                    SelectedItem,
                    ItemExtensions.SameIdGenerator.Instance);
                if (cloned == null) return;

                SavedItem = cloned.ReassignAllIds();
                OracleItemWorkspace.ActiveList.Add(SavedItem);

                OracleNotify.Message(
                    string.Format("text_item_instance_manager_item_saved".i18n(), itemName, itemID),
                    ENotificationIconType.Default,
                    GlobalCfg.MuteNotice.Value);
            }
        }
    }

    // ══════════════════════ 捕获补丁 ══════════════════════
    //
    // ⚠ 关于挂钩安全性的说明：
    //   下面这些 OnPointerEnter / Show 都是**有实体实现的 UI 方法**（要处理悬停高亮、
    //   图标加载等），不是 Unity 生命周期空实现，因此不构成 IL2CPP 方法体合并的风险。
    //   （本工程曾因挂钩 GameWorld.OnDestroy 这种空实现导致 64 万次调用，见 PluginsCore 注释。）

    /// <summary>背包/仓库格子：鼠标进入即捕获</summary>
    [HarmonyPatch(typeof(ItemView), nameof(ItemView.OnPointerEnter))]
    internal static class ItemViewPointerEnterPatch
    {
        private static void Postfix(ItemView __instance)
        {
            if (__instance != null && __instance.Item != null)
            {
                ItemCatcher.SelectedItem = __instance.Item;
            }
        }
    }

    /// <summary>鼠标离开：清空捕获</summary>
    [HarmonyPatch(typeof(ItemView), nameof(ItemView.OnPointerExit))]
    internal static class ItemViewPointerExitPatch
    {
        private static void Postfix() => ItemCatcher.SelectedItem = null;
    }

    /// <summary>
    /// 网格视图的退出同样必须单独挂钩。
    ///
    /// ⚠ 这是**正确性**问题，不是优化：GridItemView 覆写了 OnPointerExit
    ///   （已核验：它有自己的 NativeMethodInfoPtr_OnPointerExit），
    ///   因此在网格物品上移开鼠标时，调用的是 GridItemView 的版本而非基类版本，
    ///   只挂 ItemView.OnPointerExit 的话选中项**不会被清空**。
    ///
    ///   残留的旧选中会造成实际错误：玩家移开鼠标后按保存键，
    ///   存下的是上一次悬停的物品，而不是当前什么都不选。
    ///
    /// 补充说明：HideoutItemView / StaticGridItemView 都**没有**覆写 OnPointerExit
    ///（已核验），它们继承的正是 GridItemView 的实现，故本补丁同时覆盖这两者。
    /// </summary>
    [HarmonyPatch(typeof(GridItemView), nameof(GridItemView.OnPointerExit))]
    internal static class GridItemViewPointerExitPatch
    {
        private static void Postfix() => ItemCatcher.SelectedItem = null;
    }

    /// <summary>
    /// 网格视图：GridItemView 覆写了 OnPointerEnter，因此必须单独挂钩，
    /// 只挂基类 ItemView 的话网格内的物品捕获不到。
    /// </summary>
    [HarmonyPatch(typeof(GridItemView), nameof(GridItemView.OnPointerEnter))]
    internal static class GridItemViewPointerEnterPatch
    {
        private static void Postfix(GridItemView __instance)
        {
            if (__instance != null && __instance.Item != null)
            {
                ItemCatcher.SelectedItem = __instance.Item;
            }
        }
    }

    /// <summary>
    /// 手册图标 · 悬停进入。
    ///
    /// ══════════════ 这里曾经挂错了方法 ══════════════
    ///
    /// 原实现挂的是 EntityIcon.Show(Item)。但 Show 是**图标被渲染/刷新**时调用的，
    /// 不是悬停事件 —— 手册一屏会一次性渲染整批图标，Show 被连着调用 N 次，
    /// SelectedItem 被最后一次调用覆盖。
    ///
    /// 于是捕获到的是"当前渲染批次里的某一个"，与鼠标真正悬停哪件物品毫无关系：
    /// 实机表现就是"在手册里复制物品，复制出来的是同分类下另一件东西，
    /// 顺序还测不出来"（顺序取决于图标渲染次序，而非玩家的操作）。
    ///
    /// ══════════════ 正解：挂真正的悬停回调（与 4.1 一致）══════════════
    ///
    /// 4.1 的 EntityIcon.Awake（I:\build\codespace\4.1\Assembly-CSharp\EFT\HandBook\EntityIcon.cs）：
    ///
    ///     HoverTrigger hover = this.GetOrAddComponent&lt;HoverTrigger&gt;();
    ///     this._tooltip = ItemUiContext.Instance.Tooltip;
    ///     hover.OnHoverStart += this.CG_Awake;     // 显示 tooltip
    ///     hover.OnHoverEnd   += this.CG_Awake1;    // 关闭 tooltip
    ///
    /// 这两个 lambda 就是悬停进 / 出；4.1 挂的正是它们。
    /// 在 5.0 的元数据里它们被展开成 _Awake_b__7_0 / _Awake_b__7_1
    /// （编译器按源码中出现顺序编号，故 _0 = OnHoverStart、_1 = OnHoverEnd，
    ///   与 4.1 的 CG_Awake / CG_Awake1 一一对应）。
    ///
    /// 附带核对了方法体合并风险（本工程曾因 IL2CPP 方法体合并被坑过）：
    /// 这两个 lambda 一个调 _tooltip.Show、一个调 _tooltip.Close，IL 明显不同，
    /// 不会被折叠到同一原生入口。
    /// </summary>
    [HarmonyPatch(typeof(EFT.HandBook.EntityIcon), "_Awake_b__7_0")]
    internal static class EntityIconHoverStartPatch
    {
        private static void Postfix(EFT.HandBook.EntityIcon __instance)
        {
            // 5.0 把 _item 生成成了公开属性（4.1 要靠 Traverse 反射读私有字段）
            Item item = __instance?._item;
            if (item == null) return;

            ItemCatcher.SelectedItem = item;

            // 留一条可观测的痕迹：悬停事件本身很低频（只在进入时触发一次），
            // 但鼠标扫过整屏图标时会连发，所以仍走节流。
            // 它同时是这条修复的验证点 —— 只要它报出的正是鼠标下的那件物品，映射就是对的。
            OracleLog.Throttled("handbook_hover", $"[Oracle] 手册悬停：{item.Name?.Localized()}");
        }
    }

    /// <summary>
    /// 手册图标 · 悬停离开（4.1 的 CG_Awake1 → 5.0 的 _Awake_b__7_1）。
    ///
    /// ⚠ 与网格视图同理，这里是**正确性**问题而非优化：
    ///   不清空的话，鼠标移开手册图标后按保存键，存下的会是上一次悬停的物品。
    /// </summary>
    [HarmonyPatch(typeof(EFT.HandBook.EntityIcon), "_Awake_b__7_1")]
    internal static class EntityIconHoverEndPatch
    {
        private static void Postfix() => ItemCatcher.SelectedItem = null;
    }

    /// <summary>
    /// 藏身处物品视图。
    ///
    /// 继承链：HideoutItemView : StaticGridItemView : GridItemView : ItemView
    /// 它**自己覆写**了 OnPointerEnter，因此必须单独挂钩 ——
    /// 只挂 ItemView / GridItemView 都捕获不到藏身处里的物品。
    /// （Item 属性继承自 ItemView，可直接读取。）
    /// </summary>
    [HarmonyPatch(typeof(HideoutItemView), nameof(HideoutItemView.OnPointerEnter))]
    internal static class HideoutItemViewPointerEnterPatch
    {
        private static void Postfix(HideoutItemView __instance)
        {
            if (__instance != null && __instance.Item != null)
            {
                ItemCatcher.SelectedItem = __instance.Item;
            }
        }
    }
}
