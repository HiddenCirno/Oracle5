using BepInEx.Configuration;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Oracle.Data;
using Oracle.Utils;
using static Oracle.Data.OracleInterface;

namespace Oracle.Ability
{
    /// <summary>
    /// 无限体力 / 无限负重
    ///
    /// IL2CPP 移植说明：
    ///
    /// 1. 【架构改动】原版把 InfinityStaminaComponent 挂在玩家的 GameObject 上
    ///    （GameStartPatch 里 AddComponent）。在 IL2CPP 下这意味着：
    ///      · 需要再注册一个注入类型（ClassInjector）
    ///      · 每次进战局都要 AddComponent，且注入实例受 IL2CPP 侧 GC 管理
    ///    而我们已经有一个常驻的 OracleBehaviour 在每帧跑，直接并进去即可，
    ///    省掉一整类生命周期问题。行为等价。
    ///
    /// 2. 【类型位置】PhysicalBase 与 Stamina **都在全局命名空间**（不在 EFT 下），
    ///    与 PlayerBones 情况相同 —— 这是 Il2CppInterop 输出里相当容易踩空的一类。
    ///
    /// 3. 【跨域泛型】GetTotalWeight 的参数在 SPT5 中是
    ///    Il2CppSystem.Collections.Generic.IEnumerable&lt;Slot&gt;，
    ///    不是 System 的同名接口。补丁签名必须写 Il2Cpp 版，否则 Harmony 静默不挂钩。
    /// </summary>
    public static class InfinityStamina
    {
        /// <summary>
        /// 每帧锁定耐力（由 OracleBehaviour.Update 调用）。
        ///
        /// 三个量一起处理：奔跑耐力、据枪耐力、屏息氧气 —— 原版行为一致。
        /// </summary>
        public static void UpdateTick()
        {
            if (!InfinityStaminaCfg.EnableInfiniteStamina.Value) return;

            Player me = OracleGameState.LocalPlayer;
            if (me == null) return;

            var physical = me.Physical;
            if (physical == null) return;

            bool isInfinite = true;

            var stamina = physical.Stamina;
            if (stamina != null) stamina.ForceMode = isInfinite;

            var handsStamina = physical.HandsStamina;
            if (handsStamina != null) handsStamina.ForceMode = isInfinite;

            var oxygen = physical.Oxygen;
            if (oxygen != null) oxygen.ForceMode = isInfinite;
        }
    }

    /// <summary>
    /// 无限负重：直接把总重量判定为 0
    ///
    /// ⚠ 补丁签名里的 slots 参数必须是 Il2CppSystem.Collections.Generic.IEnumerable&lt;Slot&gt;，
    ///   写 System.Collections.Generic.IEnumerable&lt;Slot&gt; 会导致 Harmony 找不到匹配的方法
    ///   而**静默不挂钩**（不报错，只是功能不生效）。
    /// </summary>
    [HarmonyPatch(typeof(InventoryEquipment), nameof(InventoryEquipment.GetTotalWeight))]
    public static class InfinityWeightPatch
    {
        public static bool Prefix(InventoryEquipment __instance,
            Il2CppSystem.Collections.Generic.IEnumerable<Slot> slots,
            ref float __result)
        {
            if (!InfinityStaminaCfg.EnableInfiniteWeight.Value) return true;

            __result = 0f;
            return false;   // 跳过原方法，彻底不计重量
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(2)]
    public class InfinityStaminaCfg : IOracleCfg
    {
        internal static ConfigEntry<bool> EnableInfiniteStamina { get; set; }
        internal static ConfigEntry<bool> EnableInfiniteWeight { get; set; }

        public void Initialize(ConfigFile config)
        {
            const string section = "2. 生命之树 / Ability Module";

            EnableInfiniteStamina = config.Bind(
                section, "无限体力", true,
                new ConfigDescription("cfg_ability_module_infinity_stamina_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_ability_module_infinity_stamina_name".i18n(),
                        IsAdvanced = false,
                        Order = 210
                    }));

            EnableInfiniteWeight = config.Bind(
                section, "无限负重", true,
                new ConfigDescription("cfg_ability_module_infinity_weight_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_ability_module_infinity_weight_name".i18n(),
                        IsAdvanced = false,
                        Order = 209
                    }));
        }
    }
}
