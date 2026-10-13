using BepInEx.Configuration;
using EFT;
using EFT.Communications;
using HarmonyLib;
using Oracle.Data;
using Oracle.Utils;
using System;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.Combat
{
    /// <summary>
    /// 隐身 —— 让 AI 无视本地玩家。
    ///
    /// ── 47965（客户端 1.2.0.0）的 API 变更 ──────────────────────────────
    /// 4.1 时代挂的是 `BotsGroup.AddEnemy`，并在切换时调用
    /// `BotsGroup.RemoveEnemy` / `BotsGroup.CheckAndAddEnemy`。
    ///
    /// ⚠ 这三个方法**不是被删了，是被搬走了**（interop 元数据已核对）：
    ///   `BotsGroup` 上确实不再有 Add/Remove/Check 型敌人方法，
    ///   它们改挂在 `BotsGroup.BotGroupHostility` 上：
    ///     · `AddEnemyAction(IPlayer, BotGroupEnemyInfo)`    ← 对应 AddEnemy
    ///     · `RemoveEnemyAction(IPlayer)`                    ← 对应 RemoveEnemy
    ///   小队敌人列表本身也从 `BotsGroup.Enemies`（get-only 只读视图）
    ///   挪到 `BotGroupHostility.Enemies`（get/set 的 Dictionary）。
    ///
    /// 症状：旧程序集是针对 47242 编译的，运行时 JIT 期直接抛
    ///   MissingMethodException: BotsGroup.CheckAndAddEnemy(IPlayer, EBotEnemyCause, Boolean)
    /// 由「切换配置项」触发（OnGhostModeChanged）。
    ///
    /// 现行方案：改在**两个仍然存在的入口**上做源头拦截 ——
    ///   · BotsGroup.ReportAboutEnemy(IPlayer, EEnemyPartVisibleType, BotOwner)
    ///       ── 小队级「有人看到敌人了」的上报入口
    ///   · BotMemory.AddEnemyInfo(IPlayer, BotGroupEnemyInfo, bool)
    ///       ── 单个 AI 把目标写进自身敌人记忆的入口
    /// 两个都是 void 返回，Prefix 返回 false 即可整体跳过。
    ///
    /// ⚠ 与 4.1 的差异：4.1 关闭隐身时调的是 `BotsGroup.CheckAndAddEnemy(mainPlayer, true)`，
    ///   该 API 在 47965 已被搬进 `BotGroupHostility`（`AddEnemyAction` / `RemoveEnemyAction`）。
    ///   现行做法见 OnGhostModeChanged 的"关闭"分支 —— 手工重建敌人登记。
    ///
    /// ── 47965 的敌人登记是【两套独立列表】，务必分清 ─────────────────────
    ///   · 小队级：`BotsGroup.Enemies`（真正的存储是 `BotGroupHostility.Enemies`）
    ///   · 单 AI 级：`BotOwner.EnemiesController.EnemyInfos`
    /// `BotsGroup.ReportAboutEnemy` **并不往小队列表里加人** —— 它内部只做
    /// `SetEnemyPos` + 触发 OnReportEnemy 委托，而 SetEnemyPos 只在"小队已登记该玩家"
    /// 时才更新。真正把小队列里的人扇出到每个 AI 的，是敌对系统的
    /// `BotGroupHostility.AddEnemyAction` → 逐个成员调 `BotMemory.AddEnemyInfo`。
    ///
    /// 于是本模块的拦截点只需落在 **`BotMemory.AddEnemyInfo`** 上：
    /// 单 AI 的 EnemyInfos 一空，`LookSensor` 遍历不到任何敌人、`BotEnemyChooser`
    /// 也选不出目标 → AI 对你彻底失明（这才是隐身生效的原因）。
    ///
    /// ── 关闭隐身为什么会被"卡住"（本 bug 的根因）────────────────────────
    /// `BotMemory.DeleteInfoAboutEnemy` 只清单 AI 级，**不碰小队级**；而敌对系统的
    /// 周期评估是"小队已有该玩家就不再调 AddEnemyAction"，于是不会再驱动单 AI 的
    /// AddEnemyInfo。结果关闭隐身后状态停在"小队认为你是敌人、每个 AI 的列表却是空的"，
    /// 靠 AI 自己的视野逻辑永远恢复不过来（LookSensor 只遍历已存在的 EnemyInfos）。
    /// 所以关闭时必须**显式重建**单 AI 登记，不能指望自然恢复。
    /// </summary>
    public static class GhostMode
    {
        /// <summary>是否应当拦截该目标（= 本地玩家 且 隐身已开启）</summary>
        private static bool ShouldBlock(IPlayer target)
        {
            if (!GhostModeCfg.EnableGhostMode.Value) return false;
            if (target == null || !target.IsYourPlayer) return false;
            return true;
        }

        [HarmonyPatch(typeof(BotsGroup), nameof(BotsGroup.ReportAboutEnemy))]
        public static class BotGroupReportEnemyPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(IPlayer enemy)
            {
                // 拦截：小队不再共享本地玩家的位置/可见性
                // （注意它本身并不负责"登记"，登记靠下面的 AddEnemyInfo）
                return !ShouldBlock(enemy);
            }
        }

        [HarmonyPatch(typeof(BotMemory), nameof(BotMemory.AddEnemyInfo))]
        public static class BotMemoryAddEnemyInfoPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(IPlayer enemy)
            {
                // 拦截：AI 不把本地玩家写进自己的敌人记忆 —— 这是隐身真正起作用的那一刀
                return !ShouldBlock(enemy);
            }
        }

        /// <summary>
        /// 关闭隐身时，把本地玩家重新登记回单个 AI 的敌人列表。
        ///
        /// 这一步必须有：小队级可能仍留着记录（DeleteInfoAboutEnemy 不清它），
        /// 敌对系统的周期评估因此不会再把玩家扇出到各 AI。
        /// 见类注释的"关闭隐身为什么会被卡住"。
        /// </summary>
        internal static void ReEnrollAsEnemy(BotOwner bot, IPlayer target)
        {
            if (bot == null || target == null) return;

            BotsGroup group = bot.BotsGroup;

            // ① 小队级也没有的话先补上 —— 随后 AddEnemyAction 会向成员扇出。
            //    ⚠ BotsGroup.Enemies 是 get-only 的 IReadOnlyDictionary，不能直接写；
            //      写入必须走 BotGroupHostility.Enemies / AddEnemyAction。
            try
            {
                var hostility = group?.BotGroupHostility;
                if (hostility?.Enemies != null && !hostility.Enemies.ContainsKey(target))
                {
                    hostility.AddEnemyAction(target, new BotGroupEnemyInfo(target, group));
                }
            }
            catch (Exception ex)
            {
                OracleLog.Once("ghost_group_enroll_fail",
                    $"[Oracle] 隐身关闭：补小队敌人登记失败: {ex.Message}");
            }

            // ② 关键：补单 AI 级的登记。
            //    本方法是在"关闭"分支里调的，此时 EnableGhostMode 已是 false，
            //    所以上面那个 AddEnemyInfo 的 Prefix 不会再拦自己这一刀。
            try
            {
                var controller = bot.EnemiesController;
                if (controller == null || controller.IsEnemy(target)) return;

                bot.Memory?.AddEnemyInfo(target, GetOrCreateGroupEnemyInfo(bot, target), false);
            }
            catch (Exception ex)
            {
                OracleLog.Once("ghost_reenroll_fail",
                    $"[Oracle] 隐身关闭：重建 AI 敌人登记失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 取该 AI 所在小队里"本地玩家"的那条 BotGroupEnemyInfo；
        /// 小队里没有（或取不到）就新建一条。
        /// </summary>
        private static BotGroupEnemyInfo GetOrCreateGroupEnemyInfo(BotOwner bot, IPlayer target)
        {
            try
            {
                var enemies = bot?.BotsGroup?.BotGroupHostility?.Enemies;
                if (enemies != null && enemies.TryGetValue(target, out BotGroupEnemyInfo existing) && existing != null)
                {
                    return existing;
                }
            }
            catch (Exception ex)
            {
                // 取现有条目失败不是致命问题，新建一条即可（丢失的只是 IsHaveSeen 之类的热度）
                OracleLog.Once("ghost_groupinfo_fail",
                    $"[Oracle] 隐身关闭：读取小队敌人条目失败，改用新建: {ex.Message}");
            }

            return new BotGroupEnemyInfo(target, bot.BotsGroup);
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(1)]
    public class GhostModeCfg : IOracleCfg, IOracleKeyUpdate
    {
        internal static ConfigEntry<KeyCode> GhostModeKey { get; set; }
        internal static ConfigEntry<bool> EnableGhostMode { get; set; }

        public void Initialize(ConfigFile config)
        {
            EnableGhostMode = config.Bind(
                "1. 天堂支点 / Combat Module",
                "隐身模式",
                false,
                new ConfigDescription(
                    "cfg_combat_module_ghost_mode_enable_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_ghost_mode_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 280
                    }
                )
            );

            GhostModeKey = config.Bind(
                "1. 天堂支点 / Combat Module",
                "隐身快捷键",
                KeyCode.F11,
                new ConfigDescription(
                    "cfg_combat_module_ghost_mode_enable_key_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_ghost_mode_enable_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 279
                    }
                )
            );

            EnableGhostMode.SettingChanged += OnGhostModeChanged;
        }

        public void RegisterKeyUpdate()
        {
            OracleEvent.OnUpdate += KeyUpdate;
        }

        public static void KeyUpdate()
        {
            if (Input.GetKeyDown(GhostModeKey.Value))
            {
                EnableGhostMode.Value = !EnableGhostMode.Value;
                var value = EnableGhostMode.Value;
                OracleNotify.Message(
                    string.Format("message_ghost_mode_enable".i18n(),
                        value ? "text_enable".i18n() : "text_disable".i18n()),
                    value ? ENotificationIconType.Default : ENotificationIconType.Alert,
                    GlobalCfg.MuteNotice.Value);
            }
        }

        /// <summary>
        /// 切换隐身时，主动清理或恢复所有 AI 对本地玩家的仇恨。
        ///
        /// 只做拦截是不够的：开启前 AI 可能已经把你登记为敌人，
        /// 不清理的话它们仍会朝你开火。
        /// </summary>
        private static void OnGhostModeChanged(object sender, EventArgs e)
        {
            Player mainPlayer = OracleGameState.LocalPlayer;
            var allPlayers = OracleGameState.CurrentGameWorld?.AllAlivePlayersList;
            if (mainPlayer == null || allPlayers == null) return;

            int count = OracleCollections.SafeCount(allPlayers);

            for (int i = 0; i < count; i++)
            {
                Player player = OracleCollections.SafeGet(allPlayers, i);
                if (player == null || !player.IsAI) continue;

                var aiData = player.AIData;
                if (aiData == null) continue;

                BotOwner bot = aiData.BotOwner;
                if (bot == null) continue;

                try
                {
                    if (EnableGhostMode.Value)
                    {
                        // 开启隐身：抹掉该 AI 记忆里"本地玩家是敌人"的那条记录。
                        // （47965 已无 BotsGroup.RemoveEnemy，小队级列表交给游戏自身老化；
                        //   配合上面两个 Prefix，源头已不再产生新的敌人记录。
                        //   ⚠ DeleteInfoAboutEnemy 不清小队级 —— 正是这一点让"关闭"必须显式重建，
                        //     见类注释。）
                        bot.Memory?.DeleteInfoAboutEnemy(mainPlayer);
                    }
                    else
                    {
                        // 关闭隐身：显式重建敌人登记（4.1 是 BotsGroup.CheckAndAddEnemy，
                        // 该 API 已搬进 BotGroupHostility，且小队已登记时不会再驱动单 AI）。
                        GhostMode.ReEnrollAsEnemy(bot, mainPlayer);

                        // 顺带把位置/可见性上报一次 —— 小队此刻已有该玩家，SetEnemyPos 才会真的生效。
                        // EEnemyPartVisibleType 是【值类型枚举】，绝不能传 default(...)：
                        // interop 的值类型参数走 ToPtrNotNull，default 会得到 IntPtr.Zero 而 NRE。
                        bot.BotsGroup?.ReportAboutEnemy(mainPlayer, EEnemyPartVisibleType.Visible, bot);
                    }
                }
                catch (Exception ex)
                {
                    // 单个 AI 失败不应中断整轮处理
                    OracleLog.ErrorOnce($"ghost_mode_bot_failed_{ex.GetType().Name}",
                        $"[Oracle] 隐身切换处理 AI 失败: {ex.Message}");
                }
            }
        }
    }
}
