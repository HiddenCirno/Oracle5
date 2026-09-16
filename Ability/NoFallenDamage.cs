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
    /// 无摔落伤害
    ///
    /// IL2CPP 移植说明：
    ///   · 两个 patch 的签名在 SPT5 中未变，补丁体可原样移植。
    ///   · EFT.EDamageType 位于**独立的 PlayerEnums.dll**（不在 Assembly-CSharp 中），
    ///     但命名空间仍是 EFT —— csproj 通配引用全部 interop 程序集，故 using EFT 即可。
    ///   · 判据用「与本地玩家引用相等」而非 IsYourPlayer：
    ///     原版作者在这个方法上试过 IsYourPlayer 但未生效（见其注释），
    ///     最终采用引用比较。此处沿用其验证过的做法。
    /// </summary>
    public static class NoFallenDamage
    {
        /// <summary>是否为可拦截的摔落/撞击伤害</summary>
        private static bool IsFallDamage(in EFT.Ballistics.DamageInfo damageInfo)
        {
            var type = damageInfo.DamageType;
            return type == EDamageType.Fall || type == EDamageType.Impact;
        }

        // ── 上层入口 ──
        [HarmonyPatch(typeof(Player), nameof(Player.ApplyDamageInfo))]
        public static class ApplyDamageInfoPatch
        {
            public static bool Prefix(Player __instance, ref EFT.Ballistics.DamageInfo damageInfo,
                EBodyPart bodyPartType, EBodyPartColliderType colliderType, float absorbed)
            {
                if (!NoFallenDamageCfg.DisableFallenDamage.Value) return true;
                if (__instance != OracleGameState.LocalPlayer) return true;
                if (!IsFallDamage(in damageInfo)) return true;

                // 清零三处：伤害值、已造成伤害、延迟伤害标记
                damageInfo.Damage = 0;
                damageInfo.DidBodyDamage = 0;
                damageInfo.DelayedDamage = false;

                // 仍然放行原方法 —— 交给游戏自己处理"零伤害"这个语义，
                // 比直接跳过更安全（跳过可能让上层状态机漏掉一次结算）
                return true;
            }
        }

        // ── 血控总入口 ──
        [HarmonyPatch(typeof(ActiveHealthController), nameof(ActiveHealthController.ApplyDamage))]
        public static class ApplyDamagePatch
        {
            public static bool Prefix(ActiveHealthController __instance, EBodyPart bodyPart,
                ref float damage, ref EFT.Ballistics.DamageInfo damageInfo)
            {
                if (!NoFallenDamageCfg.DisableFallenDamage.Value) return true;
                if (!IsFallDamage(in damageInfo)) return true;

                damage = 0f;
                damageInfo.Damage = 0;
                damageInfo.DidBodyDamage = 0;
                damageInfo.DelayedDamage = false;

                return true;
            }
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(2)]
    public class NoFallenDamageCfg : IOracleCfg
    {
        internal static ConfigEntry<bool> DisableFallenDamage { get; set; }

        public void Initialize(ConfigFile config)
        {
            DisableFallenDamage = config.Bind(
                "2. 生命之树 / Ability Module",
                "阻止摔落伤害",
                true,
                new ConfigDescription(
                    "cfg_ability_module_feather_fall_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_ability_module_feather_fall_name".i18n(),
                        IsAdvanced = false,
                        Order = 200
                    }
                )
            );
        }
    }
}
