using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Oracle.Data;
using Oracle.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oracle.RaidManager
{
    /// <summary>
    /// AI 管理面板（创世引擎的页签之一）。
    ///
    /// 功能：全部杀死 / 逐个体传送 / 远程搜身 / 逐个杀死，以及机器人头像。
    ///
    /// 数据侧复用 ESP 已铺好的 OraclePlayerDataManager
    /// （GetEntityInfo / IsTeammate / GetDistanceToLocal 都在），本类只做展示与交互。
    ///
    /// ══════════════ 与 4.1 的三处结构性差异 ══════════════
    ///
    /// 1. 远程搜身不再构造 InteractionContextHelper.CG_GetAvailableInteractionState1。
    ///    4.1 建那个反编译产物闭包类，只是为了让它的 method_3() 干活，而 method_3 实际只有两句：
    ///        player.SaveInteractionRayInfo();
    ///        player.Interact(lootItemOwner, callback);
    ///    （4.1 额外赋的 context.lootItemLastOwner 在 method_3 里**根本没被读**，
    ///      它服务于别的分支；而 5.0 也没有 GetEverExistedBridgeByProfileID 可取那个 bridge。）
    ///    因此这里直接照做那两句 —— 等价、不依赖混淆名、也不需要那个不存在的 API。
    ///
    /// 2. 头像不再用 CloneVisibleItem()。
    ///    它在 5.0 的 interop 里是 MethodInfoStoreGeneric_CloneVisibleItem_Public_Static_T_T_0，
    ///    即**泛型存根**，C# 侧不可直接调用（泛型实参未被 AOT 实例化时取到空指针 =
    ///    进程级崩溃而非可捕获异常）。改用非泛型的 ItemExtensions.CloneItem 做等价深拷贝。
    ///
    /// 3. 修正了一处 4.1 就有的文案键笔误：
    ///    4.1 写的是 "text_button_ai_manager_no_result"，但 locales 里实际是
    ///    "text_ai_manager_no_result"（没有 button 段）—— 照抄会在界面上显示出原始键名。
    ///
    /// ⚠ 另外遵守本工程两条硬规矩：
    ///    · 可能抛异常的纯计算（string.Format / Localized）一律提到 GUILayout 分组**之外**先算好 ——
    ///      抛在 Begin/End 之间会让 IMGUI 布局栈失衡，连累整帧后续绘制。
    ///    · 所有游戏回调都给**非空且不抛出**的实现 —— 客户端多处回调是无条件调用的。
    /// </summary>
    public class AIManagerGUI
    {
        public Vector2 _scrollPos;

        /// <summary>头像纹理缓存（键为 ProfileId）</summary>
        private readonly Dictionary<string, Texture2D> _iconCache = new Dictionary<string, Texture2D>();

        /// <summary>头像渲染队列 —— 图标生成是异步的，第一次拿不到纹理，下帧再取</summary>
        private readonly Dictionary<string, ItemIcon> _pendingIcons = new Dictionary<string, ItemIcon>();

        // ══════════════════════ 绘制 ══════════════════════

        public void DrawPanel()
        {
            // ── 全部杀死 ──
            if (GUI.Button(
                    new Rect(RaidManagerGUI._windowRect.width - 145, 4, 85, 20),
                    "text_button_ai_manager_kill_all".i18n(),
                    UIStyleManager.RedButtonStyle))
            {
                KillAllBots();
            }

            GUILayout.Space(10);

            _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            var gameWorld = OracleGameState.CurrentGameWorld;
            var aliveList = gameWorld?.AllAlivePlayersList;

            if (aliveList == null)
            {
                GUILayout.Label("text_ai_manager_no_result".i18n(), UIStyleManager.BoxStyle);
            }
            else
            {
                int drawn = 0;

                foreach (Player player in aliveList)
                {
                    if (player == null) continue;
                    if (IsSamePlayer(player, OracleGameState.LocalPlayer)) continue;
                    if (!player.HealthController.IsAlive) continue;

                    var info = player.Profile?.Info;
                    if (info == null) continue;
                    if (OraclePlayerDataManager.IsTeammate(info)) continue;

                    drawn++;
                    DrawBotRow(player);
                }

                if (drawn == 0)
                {
                    GUILayout.Label("text_ai_manager_no_target".i18n(), UIStyleManager.BoxStyle);
                }
            }

            GUILayout.EndScrollView();
        }

        private void DrawBotRow(Player player)
        {
            // ⚠ 先把文案算好再进分组 —— 抛在 Begin/End 之间会破坏布局栈，连累整帧
            string nameLine = "";
            string infoLine = "";
            string avatarPendingText = "";

            try
            {
                // 第二个参数 includeName:false —— 名字单独一行展示，不必重复
                var entityInfo = OraclePlayerDataManager.GetEntityInfo(player, false, false);

                nameLine = $"<b>{entityInfo.Name}</b>  {entityInfo.LevelText}";

                // ⚠ 这个串有 **四个** 占位符，别漏中间的内部颜色：
                //     "text_ai_manager_ai_info": "<color={0}>{1} | 距离: <color={2}>{3} 米</color></color>"
                //        {0} 外层灰  {1} 阵营  {2} 距离黄  {3} 距离值
                //   只传三个会抛 FormatException —— 而它抛在 GUILayout 分组之外，
                //   所以只会让这一行文案变空，不会破坏 IMGUI 布局栈。
                infoLine = string.Format("text_ai_manager_ai_info".i18n(),
                    OracleColorManager.TextGray,
                    entityInfo.SideText,
                    OracleColorManager.Distance,
                    entityInfo.Distance);

                avatarPendingText = "text_button_ai_manager_avatar_generating".i18n();
            }
            catch (Exception ex)
            {
                OracleLog.Throttled("ai_row_format", $"[Oracle] AI 行文案格式化失败: {ex.Message}");
            }

            GUILayout.BeginHorizontal(UIStyleManager.BoxStyle);

            // ── 头像 ──
            Texture2D icon = GetPlayerIcon(player);
            if (icon != null)
            {
                GUILayout.Label(icon, GUILayout.Width(64), GUILayout.Height(64));
            }
            else
            {
                GUILayout.Box(avatarPendingText, UIStyleManager.NormalButtonStyle,
                    GUILayout.Width(64), GUILayout.Height(64));
            }

            // ── 文字信息 ──
            GUILayout.BeginVertical();
            GUILayout.Label(nameLine);
            GUILayout.Label(infoLine);
            GUILayout.EndVertical();

            // ── 操作按钮 ──
            GUILayout.BeginVertical(GUILayout.Width(130));

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("text_button_ai_manager_teleport".i18n(),
                    UIStyleManager.BlueButtonStyle, GUILayout.Height(30), GUILayout.MinWidth(60)))
            {
                TeleportBotToMe(player);
            }

            if (GUILayout.Button("text_button_ai_manager_search".i18n(),
                    UIStyleManager.BlueButtonStyle, GUILayout.Height(30), GUILayout.MinWidth(60)))
            {
                RemoteSearchPlayer(player);
            }

            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();

            // 冻结按钮：4.1 就是禁用状态（原作者注："修不好, 已弃用"）。
            // 保留占位是为了布局对齐，不让"杀死"按钮跳位置。
            GUI.enabled = false;
            GUILayout.Button("text_button_ai_manager_freeze".i18n(),
                UIStyleManager.BlueButtonStyle, GUILayout.Height(30), GUILayout.MinWidth(60));
            GUI.enabled = true;

            if (GUILayout.Button("text_button_ai_manager_kill".i18n(),
                    UIStyleManager.RedButtonStyle, GUILayout.Height(30), GUILayout.MinWidth(60)))
            {
                KillBot(player);
            }

            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        // ══════════════════════ 操作 ══════════════════════

        /// <summary>杀掉战局里所有非队友、存活的 AI（以及敌方玩家）</summary>
        private static void KillAllBots()
        {
            try
            {
                var gameWorld = OracleGameState.CurrentGameWorld;
                var aliveList = gameWorld?.AllAlivePlayersList;
                if (aliveList == null) return;

                var local = OracleGameState.LocalPlayer;

                foreach (Player player in aliveList)
                {
                    if (player == null) continue;
                    if (IsSamePlayer(player, local)) continue;
                    if (!player.HealthController.IsAlive) continue;

                    // 队友与同组玩家跳过
                    string groupId = player.Profile?.Info?.GroupId ?? "";
                    if (!string.IsNullOrEmpty(OracleGameState.LocalGroupId) &&
                        groupId == OracleGameState.LocalGroupId) continue;

                    KillBot(player);
                }
            }
            catch (Exception ex)
            {
                OracleCommon.ShowError(ex, "AIManagerGUI.KillAllBots");
            }
        }

        private static void KillBot(Player player)
        {
            if (player == null) return;

            try
            {
                // ⚠ 第二个参数在 5.0 是 Single（4.1 是 int）—— 传 int 字面量会隐式转换，无妨
                player.KillMe(EBodyPartColliderType.HeadCommon, 99999999f);

                // 4.1 的"防止无敌"：显式再走一次死亡流程。
                // 99M 伤害本身就足以致命，这一句是针对外部无敌类 mod 的兜底。
                player.OnDead(EDamageType.Environment);
            }
            catch (Exception ex)
            {
                OracleLog.Once("ai_kill_fail", $"[Oracle] 杀死 AI 失败: {ex.Message}");
            }
        }

        /// <summary>把目标传送到本地玩家面前</summary>
        private static void TeleportBotToMe(Player target)
        {
            var local = OracleGameState.LocalPlayer;
            if (local == null || target == null) return;

            try
            {
                Vector3 targetPos = local.Transform.position + local.Transform.forward * 1f;
                targetPos.y += 0.2f;

                // Player.Teleport(Vector3, bool) —— 5.0 确认为 public（NativeMethodInfoPtr_Teleport_Public_Virtual_New_Void_Vector3_Boolean_0）
                target.Teleport(targetPos, true);

                OracleLog.Info($"[Oracle] 已传送 AI 到身边：{target.Profile?.Nickname}");
            }
            catch (Exception ex)
            {
                OracleCommon.ShowError(ex, "AIManagerGUI.TeleportBotToMe");
            }
        }

        /// <summary>
        /// 远程搜身 —— 直接调出目标的装备界面，无需靠近或面向它。
        ///
        /// 与战利品面板的"远程开容器"是同一套机制：伪造交互射线 + Interact。
        /// 详见类注释里关于 method_3 的说明。
        /// </summary>
        private static void RemoteSearchPlayer(Player target)
        {
            var local = OracleGameState.LocalPlayer;
            if (local == null || target?.Profile == null) return;

            try
            {
                GamePlayerOwner owner = local.GetComponent<GamePlayerOwner>();
                if (owner == null)
                {
                    OracleLog.Warning("[Oracle] 远程搜身失败：取不到 GamePlayerOwner（UI 控制器）");
                    return;
                }

                // 目标是 AI 的装备容器
                Item aiRootItem = target.Profile.Inventory?.Equipment;
                if (aiRootItem == null)
                {
                    OracleLog.Warning("[Oracle] 远程搜身失败：目标装备容器为空");
                    return;
                }

                var aiController = aiRootItem.Owner?.TryCast<ItemController>();
                if (aiController == null)
                {
                    OracleLog.Warning("[Oracle] 远程搜身失败：装备容器不属于任何 ItemController");
                    return;
                }

                var compound = aiRootItem.TryCast<CompoundItem>();
                if (compound == null)
                {
                    OracleLog.Warning("[Oracle] 远程搜身失败：装备不是 CompoundItem");
                    return;
                }

                // ⚠ 回调绝不能抛出（会中断客户端回包处理 → 操作队列卡死 → 软锁）
                Action<IResult> onResult = result =>
                {
                    try
                    {
                        if (result == null || result.Failed)
                        {
                            OracleLog.Warning($"[Oracle] 远程搜身失败：{result?.Error}");
                            return;
                        }

                        // ⚠ ShowInventoryScreenLoot 的关闭回调是【无条件调用】的，不能传 null
                        Action onClosed = () =>
                        {
                            try { local.SetInventoryOpened(false); }
                            catch { /* 关闭失败无需上报 */ }
                        };
                        Il2CppSystem.Action closeAction = onClosed;

                        owner.ShowInventoryScreenLoot(compound, closeAction, false);

                        OracleLog.Info($"[Oracle] 已远程打开物品栏：{target.Profile?.Nickname}");
                    }
                    catch (Exception ex)
                    {
                        OracleLog.Once("ai_search_cb", $"[Oracle] 搜身回包处理异常: {ex.Message}");
                    }
                };

                // 绕过射线检查，然后发起交互
                local.SaveInteractionRayInfo();
                local.Interact(aiController, onResult);
            }
            catch (Exception ex)
            {
                OracleCommon.ShowError(ex, "AIManagerGUI.RemoteSearchPlayer");
            }
        }

        // ══════════════════════ 头像 ══════════════════════

        /// <summary>
        /// 取目标头像。
        ///
        /// 生成是异步的：首次调用通常只能拿到"正在生成"的句柄，
        /// 下一帧纹理才就绪 —— 所以用 _pendingIcons 暂存句柄，下帧再取。
        /// 任何失败都返回 null，界面退化为占位方块，不影响面板其余部分。
        ///
        /// ⚠ 原始装备不能用 CloneVisibleItem() 复制：它在 5.0 是泛型存根，
        ///   C# 侧不可调用。改用非泛型的 ItemExtensions.CloneItem 做深拷贝 ——
        ///   同样能拿到一份独立副本（避免图标生成过程改动玩家实时装备）。
        /// </summary>
        public Texture2D GetPlayerIcon(Player player)
        {
            if (player?.Profile == null) return null;

            string profileId = player.ProfileId;
            if (string.IsNullOrEmpty(profileId)) return null;

            if (_iconCache.TryGetValue(profileId, out Texture2D cached) && cached != null) return cached;

            try
            {
                // 1) 上一帧提交的生成任务是否已就绪
                if (_pendingIcons.TryGetValue(profileId, out ItemIcon pending))
                {
                    if (pending != null && pending.Sprite != null && pending.Sprite.texture != null)
                    {
                        Texture2D ready = pending.Sprite.texture;
                        _iconCache[profileId] = ready;
                        _pendingIcons.Remove(profileId);
                        return ready;
                    }
                    return null;   // 仍在生成中
                }

                // 2) 提交新的生成任务
                var source = player.Profile.Inventory?.Equipment;
                if (source == null) return null;

                var equipmentCopy = ItemExtensions
                    .CloneItem(source, ItemExtensions.SameIdGenerator.Instance)?
                    .TryCast<InventoryEquipment>();
                if (equipmentCopy == null) return null;

                var creator = Singleton<EFT.PlayerIcons.PlayerIconCreator>.Instance;
                if (creator == null) return null;

                var request = new PlayerIconRequest(equipmentCopy, player.Profile.Customization);
                var iconData = creator.GetIcon(request);
                if (iconData == null) return null;

                if (iconData.Sprite != null && iconData.Sprite.texture != null)
                {
                    Texture2D tex = iconData.Sprite.texture;
                    _iconCache[profileId] = tex;
                    return tex;
                }

                _pendingIcons[profileId] = iconData;
            }
            catch (Exception ex)
            {
                // 只报一次：头像失败通常是模板/资源问题，不是每帧都变的状态
                OracleLog.Once("ai_icon_fail", $"[Oracle] 头像生成失败: {ex.Message}");
            }

            return null;
        }

        // ══════════════════════ 补丁 ══════════════════════

        /// <summary>
        /// 远程搜索辅助：让 SearchController 认为"没有发现新容器"，
        /// 从而不弹搜索进度、直接按已搜索处理。
        ///
        /// 5.0 确认 EFT.SearchController.TryFindChangedContainer 是 public 非抽象
        /// （NativeMethodInfoPtr_TryFindChangedContainer_Public_Virtual_Boolean_ItemAddress_byref_ItemInfo_0），
        /// 可以正常挂钩。
        /// </summary>
        [HarmonyPatch(typeof(SearchController), "TryFindChangedContainer")]
        internal static class TryFindChangedContainerPatch
        {
            private static void Postfix(ref ItemInfo changedContainer, ref bool __result)
            {
                changedContainer = null;
                __result = false;
            }
        }

        // ══════════════════════ 辅助 ══════════════════════

        /// <summary>
        /// 判断两个 Player 是否同一实例。
        ///
        /// ⚠ 用原生指针比较而非托管 `==`：
        ///   interop 代理对象的托管引用身份不可靠（池化包装 / Harmony 现场构造的实例
        ///   都会让 `==` 得出错误结论）。指针比较才反映原生侧的真实身份。
        /// </summary>
        private static bool IsSamePlayer(Player a, Player b)
        {
            if (a == null || b == null) return false;
            return a.Pointer == b.Pointer;
        }
    }
}
