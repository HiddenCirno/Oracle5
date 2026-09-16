using EFT.HealthSystem;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using Oracle.Data;
using Oracle.Utils;
using System;

namespace Oracle.ItemSpawn
{
    /// <summary>
    /// 仓库界面的 InventoryController 捕获。
    ///
    /// ══════════════ 这里【曾经】还有一个 AddResult 桥接补丁 ══════════════
    ///
    /// 4.1 的仓库造物链路是：
    ///     ItemManipulator.Add(simulate:true) → AddResult
    ///       → TryRunNetworkTransaction(addResult)
    ///         → ItemController.ConvertOperationResultToOperation(AddResult)  ← Harmony 掉包
    ///           → Execute(我们的操作) → ToBaseInventoryCommand → 发包
    ///
    /// 其中"掉包"那一步在 5.0 下不可能成立，原因是结构性的：
    ///
    ///   1. `AddResult` **根本不在** ConvertOperationResultToOperation 的派发表里
    ///      （4.1 与 5.0 都核验过：该方法处理 MoveResult/MergeResult/SplitResult/
    ///        QuestAcceptResult… 但没有任何 AddResult 分支）。
    ///      它抛的 "operationResult is of unexpected type EFT.InventoryLogic.AddResult"
    ///      是"设计如此"——正因为如此，4.1 才需要那个补丁。
    ///
    ///   2. 该方法的形参在 interop 程序集里被声明为**接口** `IOperationResult`。
    ///      Il2CppInterop 按【声明类型】而非实际类型包装原生对象，
    ///      所以 4.1 里那句 `operationResult.GetType().Name == "AddResult"`
    ///      在 IL2CPP 下恒为 false —— 补丁会静默放行，桥接完全不生效。
    ///
    ///   3. 放行之后：原方法抛 ArgumentException → 转换结果为 null →
    ///      紧接着 Execute(null) 抛空引用。实机日志里的两个报错正是同源。
    ///
    /// 现在这两个补丁都已移除，仓库造物改为在 ItemSpawner.CloneAndSpawnItemIntoStash
    /// 里**直接构造操作并 Execute** —— 不再产生 AddResult，也就不需要任何拦截。
    /// 详见该方法的注释。
    ///
    /// ⚠ 因此本文件不再挂钩 ItemController.ConvertOperationResultToOperation。
    ///   切勿因为"4.1 有这么一段"而把它加回来。
    /// </summary>
    public static class ItemSpawnStashPatch
    {
        /// <summary>
        /// 捕获仓库界面的 InventoryController。
        ///
        /// 战局外的造物走的是"直接写仓库"路径，需要这个 controller 来寻址与发包。
        /// </summary>
        [HarmonyPatch(typeof(InventoryScreen), nameof(InventoryScreen.Show), new Type[]
        {
            typeof(IHealthController),
            typeof(InventoryController),
            typeof(EFT.Quests.QuestController),
            typeof(EFT.Achievements.AchievementsController),
            typeof(EFT.Prestige.PrestigeController),
            typeof(CompoundItem),
            typeof(EInventoryTab),
            typeof(EFT.IEftSession),
            typeof(ItemContext),
            typeof(bool)
        })]
        public class InventoryScreenShowPatch
        {
            [HarmonyPostfix]
            public static void Postfix(InventoryController controller)
            {
                if (controller != null)
                {
                    OracleGameState.StashController = controller;
                }
                else
                {
                    OracleLog.Warning("[Oracle] 仓库界面打开但 InventoryController 为空");
                }
            }
        }
    }
}
