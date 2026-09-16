using EFT;
using HarmonyLib;
using Oracle.Data;
using Oracle.Utils;

namespace Oracle.Patches
{
    /// <summary>
    /// 战局开始 —— 捕获 GameWorld / 本地玩家 / 小队信息。
    ///
    /// 这是整个插件进入游戏世界的唯一入口，后续所有战局内功能都依赖它填充的 OracleGameState。
    ///
    /// ⚠ 这里【只有一个】补丁。曾经还有一个 GameEndPatch 挂在 GameWorld.OnDestroy 上做状态清理，
    ///   已移除，原因见下方说明。
    /// </summary>
    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
    public static class GameStartPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GameWorld __instance)
        {
            OracleGameState.SetInRaid(__instance);
            OracleLog.Once(nameof(GameStartPatch),
                $"[Oracle] 进入战局: player={OracleGameState.LocalNickname} " +
                $"group={(string.IsNullOrEmpty(OracleGameState.LocalGroupId) ? "<solo>" : OracleGameState.LocalGroupId)} " +
                $"alive={OracleGameState.AlivePlayerCount}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 已移除：GameEndPatch（挂在 GameWorld.OnDestroy 上清空状态）
    //
    // 移除原因（实测事故记录）：
    //   该补丁在主菜单、从未进入战局的情况下被调用 640,224 次（约每帧 9 次），
    //   产生 40MB 日志并导致严重掉帧。
    //
    //   机制是 IL2CPP 的【方法体合并】：编译期会把 IL 相同的多个方法折叠到同一个
    //   原生函数入口。OnDestroy 这类空方法在全游戏有数百个，被折叠到同一指针，
    //   于是挂钩 GameWorld.OnDestroy 实际等同于挂钩所有被折叠的方法。
    //
    //   ⚠ 一般性教训：在 IL2CPP 下，**不要挂钩实现为空或极简的方法**
    //     （OnDestroy / OnEnable / Awake 等 Unity 生命周期空实现是高危对象），
    //     它们最容易被折叠，挂钩会波及全游戏。
    //
    // 替代方案（已采用，且更简单）：
    //   1. OracleGameState.SetInRaid 首行就调用 Clear()，开局时状态自然重置；
    //   2. OracleGameState.InRaid 在读取时用 Unity 的「已销毁对象」语义做二次校验
    //      —— 被销毁的 UnityEngine.Object 包装器与 null 比较会返回 true，
    //      因此不需要任何销毁回调也能感知世界失效。
    // ─────────────────────────────────────────────────────────────────────────
}
