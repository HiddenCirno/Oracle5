using BepInEx.Configuration;
using EFT.InventoryLogic;
using HarmonyLib;
using Oracle.Data;
using Oracle.Utils;
using static Oracle.Data.OracleInterface;

namespace Oracle.Combat
{
    /// <summary>
    /// 武器不掉耐久
    ///
    /// IL2CPP 移植说明：SPT5 中目标签名为
    ///   float GetDurabilityLossOnShot(float ammoBurnRatio, float overheatFactor,
    ///                                 float skillWeaponTreatmentFactor, ref float modsBurnRatio)
    /// 参数名与原版补丁逐一对应。
    ///
    /// modsBurnRatio 是 ByRef：跳过原方法时必须给它赋值（原版固定填 1f），
    /// 否则游戏侧会拿到未初始化值 —— 它用于武器配件的发热/磨损动画。
    /// </summary>
    public static class NoWeaponDurabilityCost
    {
        [HarmonyPatch(typeof(Weapon), nameof(Weapon.GetDurabilityLossOnShot))]
        public static class DurabilityLossPatch
        {
            private static bool Prefix(Weapon __instance,
                float ammoBurnRatio, float overheatFactor, float skillWeaponTreatmentFactor,
                ref float modsBurnRatio, ref float __result)
            {
                // 保持正常发热表现：即使不掉耐久，配件发热动画照常
                modsBurnRatio = 1f;

                if (!NoWeaponDurabilityCostCfg.EnableInfinityDurability.Value) return true;

                __result = 0f;
                return false;
            }
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(1)]
    public class NoWeaponDurabilityCostCfg : IOracleCfg
    {
        internal static ConfigEntry<bool> EnableInfinityDurability { get; set; }

        public void Initialize(ConfigFile config)
        {
            EnableInfinityDurability = config.Bind(
                "1. 天堂支点 / Combat Module",
                "无限耐久",
                false,
                new ConfigDescription(
                    "cfg_combat_module_infinity_durability_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_infinity_durability_name".i18n(),
                        IsAdvanced = false,
                        Order = 250
                    }
                )
            );
        }
    }
}
