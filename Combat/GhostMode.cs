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
    /// 两条互补路径：
    ///   1. BotGroupAddEnemyPatch：从源头拦截"把玩家加进敌人列表"
    ///   2. 切换开关时主动清理/恢复仇恨（OnGhostModeChanged）
    ///
    /// IL2CPP 移植说明（一处签名变更）：
    ///   BotsGroup.CheckAndAddEnemy 在 SPT5 中多了一个参数：
    ///     4.1: bool CheckAndAddEnemy(IPlayer player, bool ignoreAI = false)
    ///     5.0: bool CheckAndAddEnemy(IPlayer player, EBotEnemyCause cause, bool ignoreAI)
    ///   原版用命名参数 ignoreAI: true 调用，在 5.0 下会因缺少 cause 而编译失败，
    ///   故需显式补齐该参数。
    /// </summary>
    public static class GhostMode
    {
        [HarmonyPatch(typeof(BotsGroup), nameof(BotsGroup.AddEnemy))]
        public static class BotGroupAddEnemyPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(IPlayer person)
            {
                if (!GhostModeCfg.EnableGhostMode.Value) return true;
                if (person == null || !person.IsYourPlayer) return true;

                // 拦截：不把本地玩家登记为敌人
                return false;
            }
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
                        // 开启隐身：清除记忆中的敌人 + 从敌人列表移除
                        bot.Memory?.DeleteInfoAboutEnemy(mainPlayer);
                        bot.BotsGroup?.RemoveEnemy(mainPlayer, EBotEnemyCause.Unknown);
                    }
                    else
                    {
                        // 关闭隐身：让 AI 立刻重新索敌
                        // ⚠ SPT5 的 CheckAndAddEnemy 多了 cause 参数（见类注释）
                        bot.BotsGroup?.CheckAndAddEnemy(mainPlayer, EBotEnemyCause.Unknown, true);
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
