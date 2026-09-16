using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using Oracle.Data;
using Oracle.ItemSpawn;
using Oracle.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oracle.RaidManager
{
    /// <summary>
    /// 战利品管理器面板（创世引擎的页签之一）。
    ///
    /// 数据侧完全复用 ESP 已经铺好的那套：OracleLootDataManager.CachedLootList
    /// （含 ItemRef / LootableItem / Container / Price / Distance / ItemLevel / ItemColor / StackCount）。
    /// 本类只负责"展示 + 操作"，是纯视图 + 交互层。
    ///
    /// ══════════════ 三个"远程交互"入口 ══════════════
    ///
    /// 1. 远程搜索：挂钩 ItemManipulator.IsItemLocked 恒返回"未锁定"，
    ///    让没搜过的物品也直接显示可交互状态。
    ///
    /// 2. 远程拾取（散落物资）：QuickFindAppropriatePlace 组包 → CanExecute 校验
    ///    → RunNetworkTransaction 发包。
    ///
    /// 3. 远程开容器（容器内物品）：伪造交互射线 + Player.Interact，
    ///    成功后调 GamePlayerOwner.ShowInventoryScreenLoot 打开拾取界面。
    ///
    /// ══════════════ IL2CPP 移植要点 ══════════════
    ///
    /// · 4.1 第 3 条路构造了 InteractionContextHelper.CG_GetAvailableInteractionState1
    ///   这个**反编译器产物类**再调它的 method_3()。读过 4.1 客户端源码可知
    ///   method_3 实际只做两件事：SaveInteractionRayInfo() + Interact(lootItemOwner, cb)。
    ///   因此**不需要重建那个闭包类**，直接照做这两步即可（等价且不依赖混淆名）。
    ///
    /// · 4.1 的 player.Inventory.Equipment.ToEnumerable() 不可用 ——
    ///   ToEnumerable&lt;T&gt; 是 Diz.Utils.LinqExtensions 上的**泛型**扩展方法，
    ///   在 IL2CPP 下调用会踩本工程的泛型雷区（泛型实参未被 AOT 实例化时
    ///   取到空指针 = 进程级崩溃而非可捕获异常）。改为手工收集容器。
    ///
    /// · 所有网络回调都**绝不能抛出**，且**绝不能传 null**。
    ///   客户端 ClientBackendSession.SendCallback 里是 `Callback.Invoke(result)`，
    ///   没有空判断 —— 抛异常或传 null 都会中断回包处理，
    ///   让 QueueStatus 永久停在 AwaitingResponse，导致整个操作队列死掉（软锁）。
    ///   同理 GamePlayerOwner.ShowInventoryScreenLoot 的关闭回调也是无条件调用。
    /// </summary>
    public class LootManagerGUI
    {
        public Vector2 _scrollPos;

        public static bool ShowLooseLoot = true;   // 散落物资
        public static bool ShowStaticLoot = true;  // 容器内物资

        /// <summary>物品图标缓存（键为 TemplateId）</summary>
        private readonly Dictionary<string, Texture2D> _iconCache = new Dictionary<string, Texture2D>();

        // ══════════════════════ 远程搜索补丁 ══════════════════════

        /// <summary>
        /// 让所有物品都显示为"未锁定"。
        ///
        /// IsItemLocked 原实现会检查物品是否已被搜索过（未搜索的容器内物品不可交互），
        /// 这里是"远程搜索"功能的核心：跳过原判断直接返回 false。
        ///
        /// ⚠ 这是本模块唯一一个"改变全局游戏判定"的补丁。
        ///   若出现异常行为（例如未搜索容器也能直接拿东西导致状态错乱），
        ///   优先怀疑这里 —— 它是本模块中侵入性最强的改动。
        /// </summary>
        [HarmonyPatch(typeof(ItemManipulator), nameof(ItemManipulator.IsItemLocked))]
        internal static class RemoteSearchPatch
        {
            private static bool Prefix(out Error error, ref bool __result)
            {
                error = null;
                __result = false;
                return false;   // 跳过原实现
            }
        }

        // ══════════════════════ 面板绘制 ══════════════════════

        public void DrawPanel()
        {
            UIStyleManager.EnsureInitialized();

            // 顶部两个过滤开关
            if (GUI.Button(
                    new Rect(RaidManagerGUI._windowRect.width - 130, 4, 70, 20),
                    "text_button_loot_manager_ground".i18n(),
                    ShowLooseLoot ? UIStyleManager.BlueButtonStyle : UIStyleManager.RedButtonStyle))
            {
                ShowLooseLoot = !ShowLooseLoot;
            }

            if (GUI.Button(
                    new Rect(RaidManagerGUI._windowRect.width - 205, 4, 70, 20),
                    "text_button_loot_manager_container".i18n(),
                    ShowStaticLoot ? UIStyleManager.BlueButtonStyle : UIStyleManager.RedButtonStyle))
            {
                ShowStaticLoot = !ShowStaticLoot;
            }

            GUILayout.Space(10);

            _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            var cached = OracleLootDataManager.CachedLootList;
            if (cached == null || cached.Count == 0)
            {
                GUILayout.Label("text_button_loot_manager_no_result".i18n(), UIStyleManager.BoxStyle);
            }
            else
            {
                // ══════════════ 排序：等级 → 价格 → 名称 → 距离 ══════════════
                //
                // ⚠ 后两个键不是锦上添花，是**必须的**。实机现象：列表里会出现
                //   "物品1, 物品1, 物品2, 物品1" 这种交错错位，且跨帧还会跳动。
                //
                //   成因：
                //     · 手册价格表里没有的模板（例如赛季通行证道具）→ itemPrice = 0，
                //       而 ItemLevel 又是由价格推导的 → 这一整批物品的
                //       (ItemLevel, Price) **完全相同**。
                //     · LINQ 的 OrderBy 是**稳定排序**：键相等时保持**源列表顺序**。
                //       而 CachedLootList 每轮扫描都被整份重建，源顺序取决于
                //       GetValuesEnumerator / GetAllItems 的遍历次序 —— 于是同组的
                //       不同物品彼此自由交错，且每轮都可能变。
                //
                //   加上「名称 → 距离」作为确定性次级键后：
                //     · 同一件物品必定聚在一起，不再被别的物品插到中间；
                //     · 整个列表跨帧稳定，不会跳来跳去。
                //
                //   排序键用 Name 而非 ItemRef.Name.Localized()：
                //   Name 是扫描时就算好的纯字符串字段，取用零成本；
                //   若改成属性访问，每次比较都会穿透一次 interop 边界，大列表下会明显变慢。
                //
                // ⚠ 比较器用 CurrentCulture（区域性）而非 Ordinal（序数）。
                //   Ordinal 是**按 UTF-16 码点**比较的：拉丁字母勉强还能看，
                //   中文名则完全是无意义的人为顺序（"阿"排在"白"前面纯属码点巧合）。
                //   区域性比较才会按人真正认得的顺序排 —— 中文按拼音/笔画，
                //   西文按字母表 —— 这样列表看上去才是"有序"的。
                //   （代价是顺序依赖运行环境的区域设置，比 Ordinal 略慢；
                //     对同一次运行而言完全确定，不影响上面那条"跨帧稳定"的结论。）
                List<LootData> sorted = cached
                    .OrderByDescending(l => l.ItemLevel)
                    .ThenByDescending(l => l.Price)
                    .ThenBy(l => l.Name ?? "", StringComparer.CurrentCulture)
                    .ThenBy(l => l.Distance)
                    .ToList();

                foreach (LootData loot in sorted)
                {
                    bool isContainerLoot = loot.Container != null;
                    if (!((ShowStaticLoot && isContainerLoot) || (ShowLooseLoot && !isContainerLoot))) continue;

                    DrawLootRow(loot);
                }
            }

            GUILayout.EndScrollView();
        }

        private void DrawLootRow(LootData loot)
        {
            // ══════════════ ⚠ 先把所有文案算好，再进入 GUILayout 分组 ══════════════
            //
            // 实机踩坑：string.Format 的参数个数少于占位符时会抛 FormatException。
            // 当时它发生在 BeginVertical / EndVertical **之间**，于是：
            //   · IMGUI 的布局栈失衡（End 没执行到）
            //   · Unity 报 "Getting control 2's position in a group with only 2 controls
            //     when doing repaint" 与 "You are pushing more GUIClips than you are popping"
            //   · 连累同一帧后续的绘制 —— 连窗口页脚都画不出来，整个窗口看起来是空的
            //   · 而外层 try/catch 只能挡住异常本身，**挡不住已被破坏的布局栈**
            //
            // 根因是这里漏传了 locale 串的 {0}（颜色）。正确的参数表是
            //   {0}=颜色  {1}=数值/容器名  {2}=距离/数量
            // 见 locales/*.json："<color={0}>价值: {1} 卢布 | 距离: {2}米</color>"
            //
            // 通用防御：把"可能抛异常的纯计算"提到分组之外。
            // 布局栈只在 Begin/End 成对、且中间不抛异常时才是平衡的。
            string displayName = "";
            string infoText = "";
            string statusText = "";
            string noIconText = "";

            try
            {
                // Name 是 OracleLootDataManager 预先拼好的富文本串（含颜色标记），直接使用
                displayName = loot.Name ?? "";

                // OracleColor 有 implicit operator string（返回 HexColor），可直接当颜色串用
                infoText = string.Format("text_loot_manager_loot_item_info".i18n(),
                    OracleColorManager.TextGray, loot.Price, loot.Distance);

                statusText = string.Format("text_loot_manager_loot_item_status".i18n(),
                    OracleColorManager.TextGray,
                    OracleLootDataManager.GetContainerName(loot.Container),
                    loot.StackCount);

                noIconText = "text_loot_manager_no_icon".i18n();
            }
            catch (Exception ex)
            {
                // 文案坏了不该让整行画不出来，更不该破坏布局栈
                OracleLog.Throttled("loot_row_format", $"[Oracle] 战利品行文案格式化失败: {ex.Message}");
            }

            GUILayout.BeginHorizontal(UIStyleManager.BoxStyle);

            // ── 图标 ──
            Texture2D icon = GetCachedIcon(loot.ItemRef);
            if (icon != null)
            {
                GUILayout.Label(icon, GUILayout.Width(64), GUILayout.Height(64));
            }
            else
            {
                GUILayout.Label(noIconText, GUILayout.Width(64), GUILayout.Height(64));
            }

            // ── 物品信息 ──
            GUILayout.BeginVertical();
            GUILayout.Label(displayName);
            GUILayout.Label(infoText);
            GUILayout.Label(statusText);
            GUILayout.EndVertical();

            // ── 操作按钮 ──
            GUILayout.BeginVertical(GUILayout.Width(110));

            if (GUILayout.Button("text_button_loot_manager_pick".i18n(),
                    UIStyleManager.BlueButtonStyle, GUILayout.Height(30)))
            {
                Player mainPlayer = OracleGameState.LocalPlayer;
                if (mainPlayer != null) PickupLootItemEx(mainPlayer, loot);
            }

            GUILayout.Space(4);

            if (GUILayout.Button("text_button_loot_manager_copy".i18n(),
                    UIStyleManager.BlueButtonStyle, GUILayout.Height(30)))
            {
                CaptureLootMetadata(loot);
            }

            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 把该物品克隆一份收进造物工作区（"复制"按钮）。
        /// 与奇迹之门的捕获行为一致：克隆 + 重编号，保留原本的耐久与带勾状态。
        /// </summary>
        private static void CaptureLootMetadata(LootData loot)
        {
            try
            {
                if (loot.ItemRef == null) return;

                // ⚠ CloneItem 在 5.0 需要 ID 生成器；SameIdGenerator 是嵌套在
                //   ItemExtensions 里的单例类型，必须写全限定名
                var cloned = ItemExtensions.CloneItem(
                    loot.ItemRef,
                    ItemExtensions.SameIdGenerator.Instance);
                if (cloned == null) return;

                OracleItemWorkspace.ActiveList.Add(cloned.ReassignAllIds());

                OracleNotify.Message(string.Format(
                    "text_item_instance_manager_item_saved".i18n(),
                    loot.ItemRef.Name.Localized(),
                    loot.ItemRef.TemplateId));
            }
            catch (Exception ex)
            {
                OracleCommon.ShowError(ex, "LootManagerGUI.CaptureLootMetadata");
            }
        }

        /// <summary>取物品图标（带缓存）。失败返回 null，由调用方显示占位文本。</summary>
        public Texture2D GetCachedIcon(Item item)
        {
            if (item == null) return null;

            string key = item.TemplateId;
            if (string.IsNullOrEmpty(key)) return null;

            if (_iconCache.TryGetValue(key, out Texture2D cached) && cached != null) return cached;

            try
            {
                var iconData = ItemViewFactory.LoadItemIcon(item, 1, false);
                if (iconData?.Sprite?.texture != null)
                {
                    Texture2D tex = iconData.Sprite.texture;
                    _iconCache[key] = tex;
                    return tex;
                }
            }
            catch (Exception ex)
            {
                // 只报一次：图标加载失败通常是模板缺图，不是每帧都会变的问题
                OracleLog.Once("loot_icon_fail", $"[Oracle] 战利品图标加载失败: {ex.Message}");
            }

            return null;
        }

        // ══════════════════════ 远程拾取 ══════════════════════

        /// <summary>
        /// 远程拾取入口：散落物资直接拿起；容器内物资走"远程开容器"。
        /// </summary>
        public static void PickupLootItemEx(Player player, LootData loot)
        {
            if (player == null) return;

            if (loot.LootableItem != null)
            {
                PickupLootItem(player, loot.LootableItem);
                return;
            }

            if (loot.Container != null)
            {
                OpenContainerRemotely(player, loot);
            }
        }

        /// <summary>
        /// 远程拾起地面上的散落物资。
        ///
        /// 链路（与 4.1 一致，仅做了 IL2CPP 适配）：
        ///   1. 先在背包里找一个放得下的地址（找不到就提示"空间不足"并放弃）
        ///   2. QuickFindAppropriatePlace 组包（simulate=true，只求结果不落位）
        ///   3. CanExecute 校验
        ///   4. RunNetworkTransaction 发包
        ///
        /// ⚠ 第 4 步用的是**非泛型**重载 RunNetworkTransaction(IOperationResult, Callback)。
        ///   泛型版叫 TryRunNetworkTransaction，名字不同所以不存在重载歧义 ——
        ///   这一点很重要，泛型版本在 IL2CPP 下不可用。
        /// </summary>
        public static void PickupLootItem(Player player, LootItem lootItem)
        {
            if (player == null || lootItem == null) return;

            try
            {
                Item item = lootItem.Item;
                if (item == null) return;

                // 找放置位置（复用奇迹之门那套寻址：胸挂 → 口袋 → 背包）
                ItemAddress targetLocation = ItemSpawner.FindEmptyLocation(player, item);
                if (targetLocation == null)
                {
                    OracleNotify.Warning("text_spawn_item_error".i18n());
                    OracleLog.Warning($"[Oracle] 远程拾取失败：背包空间不足（{item.Name?.Localized()}）");
                    return;
                }

                var controller = player.InventoryController;
                if (controller == null)
                {
                    OracleLog.Warning("[Oracle] 远程拾取失败：InventoryController 为空");
                    return;
                }

                var pickUpResult = ItemManipulator.QuickFindAppropriatePlace(
                    item,
                    controller,
                    CollectEquipmentContainers(player),
                    ItemManipulator.EMoveItemOrder.PickUp,
                    true);

                if (!pickUpResult.Succeeded)
                {
                    OracleLog.Warning($"[Oracle] 远程拾取失败：组包未成功（{pickUpResult.Error}）");
                    return;
                }

                if (!controller.CanExecute(pickUpResult.Value))
                {
                    OracleLog.Warning($"[Oracle] 远程拾取失败：CanExecute 拒绝（{item.Name?.Localized()}）");
                    return;
                }

                // ⚠ 回调运行在客户端回包处理路径上：
                //   绝不能抛出（否则中断 SendCallback → QueueStatus 卡死 → 软锁）
                Action<IResult> onResult = result =>
                {
                    try
                    {
                        if (result != null && result.Succeed) player.UpdateInteractionCast();

                        // ⚠ 向下转型必须 TryCast（is/as 在 interop 代理上不可靠）
                        player.CurrentState?.TryCast<PickUpState>()?.Pickup(false, NoopAction);
                    }
                    catch (Exception ex)
                    {
                        OracleLog.Once("loot_pickup_cb", $"[Oracle] 拾取回包处理异常: {ex.Message}");
                    }
                };

                controller.RunNetworkTransaction(pickUpResult.Value, onResult);

                // 让角色进入拾取动作（纯表现层；失败也不影响物品转移）
                try
                {
                    player.CurrentManagedState?.Pickup(true, NoopAction);
                }
                catch (Exception ex)
                {
                    OracleLog.Once("loot_pickup_state", $"[Oracle] 进入拾取状态失败（不影响拾取）: {ex.Message}");
                }

                OracleLog.Info($"[Oracle] 已远程拾取：{item.Name?.Localized()}");
            }
            catch (Exception ex)
            {
                OracleCommon.ShowError(ex, "LootManagerGUI.PickupLootItem");
            }
        }

        // ══════════════════════ 远程开容器 ══════════════════════

        /// <summary>
        /// 远程打开容器（把容器内的战利品界面调出来）。
        ///
        /// 4.1 的写法是构造 InteractionContextHelper.CG_GetAvailableInteractionState1
        /// 这个反编译产物类再调 method_3()。读过客户端源码后可知它实际只做：
        ///     owner.Player.SaveInteractionRayInfo();
        ///     owner.Player.Interact(lootItemOwner, callback);
        /// 所以这里直接照做 —— 等价、且不依赖任何混淆名或反编译产物。
        /// </summary>
        private static void OpenContainerRemotely(Player player, LootData loot)
        {
            try
            {
                Item containerItem = loot.Container?.ItemOwner?.RootItem;
                if (containerItem == null)
                {
                    OracleLog.Warning("[Oracle] 远程打开容器失败：容器根物品为空");
                    return;
                }

                // 容器所属的 ItemController —— 就是 Interact 的目标
                var lootItemOwner = containerItem.Owner?.TryCast<ItemController>();
                if (lootItemOwner == null)
                {
                    OracleLog.Warning("[Oracle] 远程打开容器失败：容器不属于任何 ItemController");
                    return;
                }

                GamePlayerOwner owner = player.GetComponent<GamePlayerOwner>();
                if (owner == null)
                {
                    OracleLog.Warning("[Oracle] 远程打开容器失败：取不到 GamePlayerOwner（UI 控制器）");
                    return;
                }

                var compound = containerItem.TryCast<CompoundItem>();
                if (compound == null)
                {
                    OracleLog.Warning("[Oracle] 远程打开容器失败：容器不是 CompoundItem");
                    return;
                }

                // ⚠ 回调绝不能抛出 —— 与拾取路径同理，中断回包处理会导致操作队列卡死
                Action<IResult> onResult = result =>
                {
                    try
                    {
                        if (result == null || result.Failed)
                        {
                            OracleLog.Warning($"[Oracle] 远程打开容器失败：{result?.Error}");
                            return;
                        }

                        // ⚠ ShowInventoryScreenLoot 的第三个参数是"界面关闭时"的回调，
                        //   客户端实现里是【无条件调用】的：
                        //       this.eftGamePlayerOwner_0.Player.SetInventoryOpened(false);
                        //       this.callback();          ← 传 null 必炸
                        //   所以必须给一个非空（且不抛出）的 Action。
                        Action onClosed = () =>
                        {
                            try { player.SetInventoryOpened(false); }
                            catch { /* 关闭失败无需上报 */ }
                        };
                        Il2CppSystem.Action closeAction = onClosed;

                        owner.ShowInventoryScreenLoot(compound, closeAction, false);

                        try
                        {
                            player.StatisticsManager?.OnInteractWithLootContainer(containerItem);
                        }
                        catch { /* 统计失败与功能无关 */ }
                    }
                    catch (Exception ex)
                    {
                        OracleLog.Once("loot_container_cb", $"[Oracle] 开容器回包处理异常: {ex.Message}");
                    }
                };

                // 伪造交互射线 —— 远程交互的关键：让原生侧认为玩家正对着这个容器
                player.SaveInteractionRayInfo();
                player.Interact(lootItemOwner, onResult);

                OracleLog.Info($"[Oracle] 已远程打开容器：{containerItem.Name?.Localized()}");
            }
            catch (Exception ex)
            {
                OracleCommon.ShowError(ex, "LootManagerGUI.OpenContainerRemotely");
            }
        }

        // ══════════════════════ 辅助 ══════════════════════

        /// <summary>
        /// 空委托 —— 供游戏回调使用。
        ///
        /// ⚠ 刻意不用 null：这些回调在客户端实现里往往是**无条件调用**的
        ///   （GamePlayerOwner.CG_ShowInventoryScreenLoot.method_0 就是如此，
        ///   它先 SetInventoryOpened(false) 再直接 this.callback()）。
        ///   传 null 会在原生侧抛 NRE；而抛异常的位置又常常在
        ///   队列状态机内部，会连带把整个操作队列卡死。
        ///   一个什么都不做的非空委托是严格更安全的选择。
        /// </summary>
        private static readonly Il2CppSystem.Action NoopAction =
            (Action)(() => { });

        /// <summary>
        /// 收集玩家身上可作为"放置目标"的容器（胸挂 / 口袋 / 背包 / 保险箱）。
        ///
        /// ⚠ 4.1 这里写的是 player.Inventory.Equipment.ToEnumerable()，
        ///   而 ToEnumerable&lt;T&gt; 是泛型扩展方法，在 IL2CPP 下不可直接调用
        ///   （泛型实参未被 AOT 实例化时取到空指针 = 进程级崩溃，而非可捕获异常）。
        ///   因此改为手工收集。
        ///
        /// ⚠ 向下转型必须 TryCast —— is/as 在 interop 代理上不可靠
        ///   （代理按声明类型包装，Slot.ContainedItem 的声明类型是 Item）。
        /// </summary>
        private static Il2CppSystem.Collections.Generic.List<CompoundItem> CollectEquipmentContainers(Player player)
        {
            var targets = new Il2CppSystem.Collections.Generic.List<CompoundItem>();

            var equipment = player?.Inventory?.Equipment;
            if (equipment == null) return targets;

            EquipmentSlot[] slots =
            {
                EquipmentSlot.Pockets,
                EquipmentSlot.TacticalVest,
                EquipmentSlot.Backpack,
                EquipmentSlot.SecuredContainer,
            };

            for (int i = 0; i < slots.Length; i++)
            {
                var container = equipment.GetSlot(slots[i])?.ContainedItem?.TryCast<CompoundItem>();
                if (container != null) targets.Add(container);
            }

            return targets;
        }
    }
}
