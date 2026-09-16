using BepInEx.Configuration;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using Oracle.Data;
using Oracle.Utils;
using static Oracle.Data.OracleInterface;

namespace Oracle.Ability
{
    /// <summary>
    /// 无敌 / 锁血 / 不死
    ///
    /// IL2CPP 移植说明：本批 4 个 patch 的目标签名在 SPT5 中**全部未变**
    /// （已逐一核对 interop 元数据），因此补丁体可原样移植，只需替换状态引用。
    ///
    /// 三层防护的职责划分：
    ///   · Player.ApplyDamageInfo        —— 上层入口，拦"正规"伤害
    ///   · ActiveHealthController.ApplyDamage —— 血控总入口，拦绕过上层的伤害
    ///     （火箭筒尾焰、坠落、脱水/力竭/中毒、流血效果等）
    ///     该方法是非虚 public，patch 基类即可覆盖全部来路
    ///   · Kill / DestroyBodyPart        —— 兜底，阻止死亡与部位损毁
    /// </summary>
    public static class GodMode
    {
        /// <summary>是否处于任一"免伤"状态</summary>
        private static bool AnyProtectionEnabled
            => GodModeCfg.Invincible.Value || GodModeCfg.HealthLock.Value || GodModeCfg.Undying.Value;

        /// <summary>是否只开了锁血（不开无敌）</summary>
        private static bool HealthLockOnly
            => !GodModeCfg.Invincible.Value && GodModeCfg.HealthLock.Value;

        // ── 上层伤害入口 ──
        [HarmonyPatch(typeof(Player), nameof(Player.ApplyDamageInfo))]
        public static class ApplyDamageInfoPatch
        {
            public static bool Prefix(Player __instance)
            {
                if (!__instance.IsYourPlayer) return true;

                // 无敌优先级最高：直接跳过原方法
                return !GodModeCfg.Invincible.Value;
            }

            public static void Postfix(Player __instance)
            {
                if (!__instance.IsYourPlayer) return;

                // 没开无敌但开了锁血：把血补满
                if (HealthLockOnly)
                {
                    var hc = __instance.ActiveHealthController;
                    hc?.RestoreFullHealth();
                }
            }
        }

        // ── 血控总入口（覆盖绕过上层的伤害来路）──
        [HarmonyPatch(typeof(ActiveHealthController), nameof(ActiveHealthController.ApplyDamage))]
        public static class ApplyDamagePatch
        {
            public static bool Prefix(ActiveHealthController __instance)
            {
                var player = __instance?.Player;
                if (player == null || !player.IsYourPlayer) return true;

                // 跳过时返回 default(float) 即 0f，语义正是"本次没有造成伤害"
                return !GodModeCfg.Invincible.Value;
            }

            public static void Postfix(ActiveHealthController __instance)
            {
                var player = __instance?.Player;
                if (player == null || !player.IsYourPlayer) return;

                // 锁血：把血补满（同时盖住尾焰/坠落/中毒这些绕过上层的伤害）
                if (HealthLockOnly && __instance.IsAlive)
                {
                    __instance.RestoreFullHealth();
                }
            }
        }

        // ── 阻止死亡 ──
        [HarmonyPatch(typeof(ActiveHealthController), nameof(ActiveHealthController.Kill))]
        public static class KillPatch
        {
            public static bool Prefix(ActiveHealthController __instance)
            {
                var player = __instance?.Player;
                if (player == null || !player.IsYourPlayer) return true;

                // 开启任意一个即阻止死亡
                return !AnyProtectionEnabled;
            }
        }

        // ── 阻止部位损毁 ──
        [HarmonyPatch(typeof(ActiveHealthController), nameof(ActiveHealthController.DestroyBodyPart))]
        public static class DestroyBodyPartPatch
        {
            public static bool Prefix(ActiveHealthController __instance)
            {
                var player = __instance?.Player;
                if (player == null || !player.IsYourPlayer) return true;

                return !AnyProtectionEnabled;
            }
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(2)]
    public class GodModeCfg : IOracleCfg
    {
        public static ConfigEntry<bool> Invincible { get; set; }
        public static ConfigEntry<bool> HealthLock { get; set; }
        public static ConfigEntry<bool> Undying { get; set; }

        public void Initialize(ConfigFile config)
        {
            const string section = "2. 生命之树 / Ability Module";

            Invincible = config.Bind(
                section, "无敌", false,
                new ConfigDescription("cfg_ability_module_gode_mode_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_ability_module_gode_mode_name".i18n(),
                        IsAdvanced = false,
                        Order = 220
                    }));

            HealthLock = config.Bind(
                section, "锁血", false,
                new ConfigDescription("cfg_ability_module_health_lock_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_ability_module_health_lock_name".i18n(),
                        IsAdvanced = false,
                        Order = 219
                    }));

            Undying = config.Bind(
                section, "不死", false,
                new ConfigDescription("cfg_ability_module_undead_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_ability_module_undead_name".i18n(),
                        IsAdvanced = false,
                        Order = 218
                    }));
        }
    }
}
