using BepInEx.Configuration;
using EFT;
using EFT.Interactive;
using EFT.UI;
using HarmonyLib;
using Oracle.Data;
using Oracle.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.Ability
{
    /// <summary>
    /// 念力开锁 —— 对锁着的门补一个"解锁"交互项。
    ///
    /// IL2CPP 移植要点（两处都是硬性的）：
    ///
    /// 1. 【跨域委托 + GC 根引用】
    ///    InteractionAction.Action 的类型是 Il2CppSystem.Action，不是托管 System.Action。
    ///    Il2CppInterop 提供 op_Implicit(System.Action) 做转换，转换后会创建一个
    ///    GCHandle 指向托管委托 —— 一旦托管侧被 GC 回收，游戏调用该委托时就会
    ///    触发非法内存访问（0xC0000005），且**不会有任何托管异常**。
    ///
    ///    更麻烦的是：GetAvailableActions 每次调用都会重建交互菜单（看着门时约每帧一次），
    ///    每次都会新建一个闭包，于是委托数量增长极快。
    ///    因此这里用一个【滑动窗口】持有最近的若干个托管委托：
    ///    只丢弃最老的一个，而不是整批清空 —— 避免丢掉游戏可能仍在持有的那个。
    ///
    /// 2. 【LINQ 不可用】
    ///    __result.Actions 是 Il2CppSystem.Collections.Generic.List&lt;InteractionAction&gt;，
    ///    它不实现 System.Collections.Generic.IEnumerable&lt;T&gt;，
    ///    所以旧版的 .Any(x =&gt; ...) 无法编译，必须改为手写循环。
    /// </summary>
    public static class TelekinisisUnlock
    {
        /// <summary>
        /// 托管委托的滑动窗口（防止被 GC 回收导致游戏侧野指针）。
        /// 只保留最近的若干个；超出时丢最老的一个。
        /// </summary>
        private static readonly List<System.Action> _rootedDelegates = new List<System.Action>(MaxRootedDelegates);

        /// <summary>窗口大小。远超游戏同时持有的交互项数量，足够安全。</summary>
        private const int MaxRootedDelegates = 256;

        private static void RootDelegate(System.Action action)
        {
            if (_rootedDelegates.Count >= MaxRootedDelegates)
            {
                _rootedDelegates.RemoveAt(0);
            }
            _rootedDelegates.Add(action);
        }

        /// <summary>判断交互列表里是否已有解锁项</summary>
        private static bool HasUnlockAction(Il2CppSystem.Collections.Generic.List<InteractionAction> actions)
        {
            if (actions == null) return false;

            int count = actions.Count;
            for (int i = 0; i < count; i++)
            {
                var a = actions[i];
                if (a == null) continue;

                string name = a.Name;
                if (string.IsNullOrEmpty(name)) continue;
                if (name == "Unlock" || name.Contains("Unlock")) return true;
            }
            return false;
        }

        /// <summary>
        /// 拦截互动菜单构建，对锁着的门补一个"解锁"项。
        ///
        /// 5.0 中该重载仍为 (GamePlayerOwner, IInteractive)，已核对 interop 元数据。
        /// 注意用显式 Type[] 指定重载 —— 该方法是多重载的，不指定可能挂错。
        /// </summary>
        [HarmonyPatch(typeof(InteractionContextHelper), nameof(InteractionContextHelper.GetAvailableActions),
            new Type[] { typeof(GamePlayerOwner), typeof(IInteractive) })]
        public static class GetActionsPatch
        {
            public static void Postfix(GamePlayerOwner owner, IInteractive interactive,
                ref AvailableInteractionState __result)
            {
                if (!TelekinisisUnlockCfg.EnableTelekinisisUnlock.Value) return;
                if (interactive == null || __result == null) return;

                // IInteractive 是接口，用 TryCast 而非 as —— 代理类型跨层转型行为不一致
                Component comp = interactive.TryCast<Component>();
                if (comp == null) return;

                // 可交互物体：先看自身，再向上找
                WorldInteractiveObject wio = comp.GetComponent<WorldInteractiveObject>();
                if (wio == null) wio = comp.GetComponentInParent<WorldInteractiveObject>();
                if (wio == null) return;

                // 只处理锁着的门
                if (wio.DoorState != EDoorState.Locked) return;

                var actions = __result.Actions;
                if (actions == null) return;

                // 已有解锁项则不重复添加
                if (HasUnlockAction(actions)) return;

                Player ownerPlayer = owner?.Player;

                // 闭包捕获 wio 与 ownerPlayer
                System.Action unlockAction = () =>
                {
                    try
                    {
                        // 先 SetUser，防止 SAIN 等 AI 模组取用户时拿到 null
                        wio.SetUser(ownerPlayer);
                        wio.DoorState = EDoorState.Shut;
                    }
                    catch (Exception ex)
                    {
                        // 委托在游戏侧被调用，异常必须自己吞掉并记录，
                        // 否则会顺着原生调用栈抛出去，行为不可预期
                        OracleLog.ErrorOnce("telekinisis_invoke_failed",
                            $"[Oracle] 念力解锁执行失败: {ex.Message}");
                    }
                };

                RootDelegate(unlockAction);

                // 插入到首位使其成为默认选项
                actions.Insert(0, new InteractionAction
                {
                    Name = "Unlock",
                    Action = (Il2CppSystem.Action)unlockAction,
                    Disabled = false
                });
            }
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(2)]
    public class TelekinisisUnlockCfg : IOracleCfg
    {
        public static ConfigEntry<bool> EnableTelekinisisUnlock { get; set; }

        public void Initialize(ConfigFile config)
        {
            EnableTelekinisisUnlock = config.Bind(
                "2. 生命之树 / Ability Module",
                "念力解锁",
                false,
                new ConfigDescription(
                    "cfg_ability_module_telekinisis_unlock_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_ability_module_telekinisis_unlock_name".i18n(),
                        IsAdvanced = false,
                        Order = 190
                    }
                )
            );
        }
    }
}
