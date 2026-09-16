using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using Oracle.Data;
using Oracle.Utils;
using System;
using static Oracle.Data.OracleInterface;

namespace Oracle.Combat
{
    /// <summary>
    /// 无限子弹
    ///
    /// 游戏里有四条互不相通的开火链路，必须分别处理，缺一不可：
    ///   1. 常规枪械 —— BallisticsCalculator.Shoot(Shot)，Postfix 往弹匣/转轮/枪膛补弹
    ///   2. 下挂榴弹发射器 —— LauncherFire.OnFireEvent（走 LaunchRocketShot，
    ///      不经 Shoot(Shot)）；且 shot.Weapon 是 Launcher（GearMod）而非 Weapon，
    ///      第 1 条的 `is Weapon` 判定会直接跳过
    ///   3. 火箭筒 —— RocketLauncherFire.OnFireEvent，走 9 参 Shoot 重载
    ///   4. 信号枪 —— FlareGunFire.OnFireEvent，打的是 FlareCartridge 实体，完全不经过 BallisticsCalculator
    ///
    /// 后三者的共同点：扣弹都在 OnFireEvent 的**最后一步**，
    /// 所以必须在 Postfix 补弹 —— 在 Prefix 补会被紧随其后的 RemoveItem 一起清掉。
    ///
    /// IL2CPP 移植说明：本文件所有 API 已逐一核对 SPT5 interop 元数据，签名均未变。
    /// </summary>
    public static class InfiniteAmmo
    {
        // ── 常规枪械 ──
        [HarmonyPatch(typeof(BallisticsCalculator), "Shoot", new Type[] { typeof(Shot) })]
        public static class ShootPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Shot shot)
            {
                if (!IsReady) return;
                if (shot == null) return;

                // 确认是本地玩家开的枪
                var bridge = shot.Player;
                if (bridge == null) return;
                var shooter = bridge.iPlayer;
                if (shooter == null || !shooter.IsYourPlayer) return;

                Item ammoItem = shot.Ammo;
                if (ammoItem == null) return;
                if (!(shot.Weapon?.TryCast<Weapon>() is Weapon weapon)) return;

                Magazine currentMagazine = weapon.GetCurrentMagazine();

                if (currentMagazine != null)
                {
                    // 转轮：每个弹巢各补一发
                    if (currentMagazine.TryCast<CylinderMagazine>() is CylinderMagazine cylinderMag)
                    {
                        var camoras = cylinderMag.Camoras;
                        if (camoras != null)
                        {
                            int n = camoras.Length;
                            for (int i = 0; i < n; i++)
                            {
                                camoras[i]?.Add(CreateAmmo(ammoItem), false, true);
                            }
                        }
                    }
                    // 弹匣：补一发
                    else
                    {
                        RefillMagazine(currentMagazine, ammoItem);
                    }
                }
                else
                {
                    // 无弹匣武器（如部分霰弹枪）：补进枪膛
                    var chambers = weapon.Chambers;
                    if (chambers != null)
                    {
                        int n = chambers.Length;
                        for (int i = 0; i < n; i++)
                        {
                            chambers[i]?.Add(CreateAmmo(ammoItem), false, true);
                        }
                    }
                }
            }
        }

        // ── 下挂榴弹发射器 ──
        [HarmonyPatch(typeof(Player.FirearmController.LauncherFire), "OnFireEvent")]
        public static class LauncherFirePatch
        {
            [HarmonyPrefix]
            public static void Prefix(Player.FirearmController.LauncherFire __instance, out Ammo __state)
            {
                __state = null;
                if (!CanWork(__instance?.Player)) return;

                // 记下这一发，Postfix 里用它的模板复制一发新的
                __state = GetLauncherChamber(__instance.Controller?.UnderbarrelWeapon)?.ContainedItem?.TryCast<Ammo>();
            }

            [HarmonyPostfix]
            public static void Postfix(Player.FirearmController.LauncherFire __instance, Ammo __state)
            {
                if (__state == null || !CanWork(__instance?.Player)) return;

                Slot chamber = GetLauncherChamber(__instance.Controller?.UnderbarrelWeapon);

                // 还没被清空就先不动，避免和游戏自身的扣弹打架
                if (chamber == null || chamber.ContainedItem != null) return;

                chamber.Add(CreateAmmo(__state), false, true);
            }
        }

        // ── 火箭筒 ──
        [HarmonyPatch(typeof(Player.FirearmController.RocketLauncherFire), "OnFireEvent")]
        public static class RocketLauncherFirePatch
        {
            [HarmonyPrefix]
            public static void Prefix(Player.FirearmController.RocketLauncherFire __instance, out Ammo __state)
            {
                __state = null;
                if (!CanWork(__instance?.Player)) return;

                __state = CaptureChamberAmmo(__instance.Weapon);
            }

            [HarmonyPostfix]
            public static void Postfix(Player.FirearmController.RocketLauncherFire __instance, Ammo __state)
            {
                if (__state == null || !CanWork(__instance?.Player)) return;
                RefillChamber(__instance.Weapon, __state);
            }
        }

        // ── 信号枪 ──
        [HarmonyPatch(typeof(Player.FirearmController.FlareGunFire), "OnFireEvent")]
        public static class FlareGunFirePatch
        {
            [HarmonyPrefix]
            public static void Prefix(Player.FirearmController.FlareGunFire __instance, out Ammo __state)
            {
                __state = null;
                if (!CanWork(__instance?.Player)) return;

                __state = CaptureChamberAmmo(__instance.Weapon);
            }

            [HarmonyPostfix]
            public static void Postfix(Player.FirearmController.FlareGunFire __instance, Ammo __state)
            {
                if (__state == null || !CanWork(__instance?.Player)) return;
                RefillChamber(__instance.Weapon, __state);
            }
        }

        // ── 辅助 ──

        /// <summary>开关 + 环境就绪 + 本地玩家 判定</summary>
        private static bool CanWork(Player player)
        {
            return player != null && player.IsYourPlayer && IsReady;
        }

        /// <summary>
        /// 取下开火前枪膛里的那一发（Postfix 里要用它的模板复制一发新的）
        /// </summary>
        private static Ammo CaptureChamberAmmo(Weapon weapon)
        {
            return weapon?.FirstLoadedChamberSlot?.ContainedItem?.TryCast<Ammo>();
        }

        /// <summary>
        /// 往弹匣里补一发。
        ///
        /// ⚠ Magazine.Cartridges 的类型是 StackSlot，**不是 Slot** ——
        ///   二者是彼此独立的容器实现（StackSlot 的基类是 Il2CppSystem.Object，
        ///   并不继承 Slot），不要因为名字相近就套用 Slot 的调用约定。
        ///
        ///   区别就在这里：Slot.Add 有 (Item, bool simulate, bool ignoreMalfunction) 三参重载，
        ///   而 StackSlot.Add 只有 (Item item, bool simulate) 两参。
        ///   照抄转轮/枪膛那两处的三参写法会直接 CS1501。
        ///   （这点与 4.1 一致，原版这一行本来就只有两个参数。）
        /// </summary>
        private static void RefillMagazine(Magazine magazine, Item ammoTemplate)
        {
            StackSlot cartridges = magazine?.Cartridges;
            if (cartridges == null) return;

            Item newAmmo = CreateAmmo(ammoTemplate);
            if (newAmmo == null) return;

            // 先校验弹种兼容性再放：不兼容时 Add 会返回失败的 OperationResult 且静默无效
            if (!cartridges.CheckCompatibility(newAmmo)) return;

            cartridges.Add(newAmmo, false);
        }

        /// <summary>
        /// 把枪膛补回一发同模板的新弹。
        /// 只在枪膛已被游戏自身清空时动手，避免和扣弹逻辑打架。
        /// </summary>
        private static void RefillChamber(Weapon weapon, Ammo ammo)
        {
            Slot chamber = weapon?.FirstFreeChamberSlot;
            if (chamber == null || chamber.ContainedItem != null) return;

            chamber.Add(CreateAmmo(ammo), false, true);
        }

        /// <summary>开关 + 环境就绪判定</summary>
        private static bool IsReady
        {
            get
            {
                if (!OracleGameState.InRaid) return false;
                if (!Singleton<ItemFactory>.Instantiated) return false;
                return InfiniteAmmoCfg.EnableInfiniteAmmo != null
                    && InfiniteAmmoCfg.EnableInfiniteAmmo.Value;
            }
        }

        /// <summary>
        /// 取下挂发射器的枪膛（Launcher.Chamber 恒等于 Chambers[0]，这里做一次越界保护）
        /// </summary>
        private static Slot GetLauncherChamber(Launcher launcher)
        {
            var chambers = launcher?.Chambers;
            if (chambers == null || chambers.Length == 0) return null;
            return chambers[0];
        }

        /// <summary>
        /// 复制一发子弹（同模板、新 ID）
        /// </summary>
        internal static Item CreateAmmo(Item ammo)
        {
            var template = ammo?.Template;
            if (template == null) return null;

            string fakeId = OracleIdGenerator.GenerateSafeHexId(template.StringId, OracleIdGenerator.NewSalt());
            return Singleton<ItemFactory>.Instance.CreateItem(fakeId, ammo.TemplateId, null);
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(1)]
    public class InfiniteAmmoCfg : IOracleCfg
    {
        internal static ConfigEntry<bool> EnableInfiniteAmmo { get; set; }

        public void Initialize(ConfigFile config)
        {
            EnableInfiniteAmmo = config.Bind(
                "1. 天堂支点 / Combat Module",
                "无限子弹",
                false,
                new ConfigDescription(
                    "cfg_combat_module_infinity_ammo_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_infinity_ammo_name".i18n(),
                        IsAdvanced = false,
                        Order = 260
                    }
                )
            );
        }
    }
}
