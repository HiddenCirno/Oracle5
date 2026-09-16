using BepInEx.Configuration;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Oracle.Data;
using Oracle.Utils;
using static Oracle.Data.OracleInterface;

namespace Oracle.Combat
{
    /// <summary>
    /// 武器无故障
    ///
    /// IL2CPP 移植说明：目标签名在 SPT5 中为
    ///   EMalfunctionState GetMalfunctionState(Ammo, bool, bool, bool, float, float, ref EMalfunctionSource)
    /// 与原版补丁的声明完全对应（返回类型 -> __result，最后一个 ByRef 参数 -> malfunctionSource）。
    /// EMalfunctionState / EMalfunctionSource 仍是嵌套在 EFT.InventoryLogic.Weapon 下的枚举。
    /// </summary>
    public static class NoMalfunction
    {
        [HarmonyPatch(typeof(Player.FirearmController), nameof(Player.FirearmController.GetMalfunctionState))]
        public static class PlayerWeaponNeverJamPatch
        {
            private static bool Prefix(Player.FirearmController __instance,
                ref Weapon.EMalfunctionState __result,
                ref Weapon.EMalfunctionSource malfunctionSource)
            {
                if (!NoMalfunctionCfg.EnableNoMalfunction.Value) return true;

                Player me = OracleGameState.LocalPlayer;
                if (__instance == null || me == null) return true;

                Player.AbstractHandsController hands = me.HandsController;
                if (hands == null) return true;

                // 只对本地玩家手上的武器生效。
                //
                // ⚠ 这里【必须】比较原生指针，不能写成 __instance != hands。原因是包装来源不同：
                //
                //   · hands 来自普通属性 getter。Il2CppInterop 生成的 getter 末尾会走
                //     Il2CppObjectPool.Get<T>（已反汇编确认：一个以 IntPtr 为键、
                //     值为弱引用的静态缓存 s_cache，命中即复用同一托管实例），
                //     所以托管侧对象之间直接比较是可靠的 —— ESP 里的 `player == me` 正是靠这个成立。
                //
                //   · __instance 则是 HarmonyX 从原生 this 指针现场构造的，**不经过这个池**，
                //     因此它与 hands 很可能不是同一个托管对象。
                //     Il2CppObjectBase 上并未重载 op_Inequality（已核对元数据），
                //     且 FirearmController : ItemHandsController : AbstractHandsController
                //     存在继承关系，于是 != 会被编译成纯粹的托管引用比较 ——
                //     结果就是两个包装永不相等，补丁**静默失效**：不报错、只是不生效。
                //
                //   比较 Pointer 与包装来源无关，恒为精确判定。
                if (__instance.Pointer != hands.Pointer) return true;

                // ref 结果直接改为无故障
                __result = Weapon.EMalfunctionState.None;

                // 故障来源标记为控制台命令（游戏内部认为这是被外力清除的，不会触发后续逻辑）
                malfunctionSource = Weapon.EMalfunctionSource.ConsoleCommand;

                // 阻止原方法执行
                return false;
            }
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(1)]
    public class NoMalfunctionCfg : IOracleCfg
    {
        internal static ConfigEntry<bool> EnableNoMalfunction { get; set; }

        public void Initialize(ConfigFile config)
        {
            EnableNoMalfunction = config.Bind(
                "1. 天堂支点 / Combat Module",
                "武器无故障",
                true,
                new ConfigDescription(
                    "cfg_combat_module_no_malfunction_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_no_malfunction_name".i18n(),
                        IsAdvanced = false,
                        Order = 240
                    }
                )
            );
        }
    }
}
