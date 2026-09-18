using EFT;
using EFT.UI.SessionEnd;
using HarmonyLib;
using Oracle.Data;
using Oracle.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Oracle.Patches
{
    /// <summary>
    /// 战局结束（撤离 / 死亡 / 迷失）时，把战局级状态归零。
    ///
    /// ══════════════ 为什么需要它 ══════════════
    ///
    /// 原先 Oracle5 只有「战局开始」的钩子（`GameStartPatch` 挂
    /// `GameWorld.OnGameStarted`），**没有任何"战局结束"的处理**，
    /// 状态全靠"下一次开局时自然重置"。
    ///
    /// 这会出两个问题：
    ///
    ///   1. **叠加层定格**：离开战局后 `OracleGameState.InRaid` 仍为 true，
    ///      绘制路径继续用上一局的缓存数据；一旦某帧 Build 抛异常把原语块弄丢
    ///      （那块只有 3 个），叠加层就永久停在最后一帧画面上，
    ///      且因为是 WS_EX_TOPMOST 窗口，会盖住结算界面、主菜单和之后所有战局。
    ///
    ///   2. **跨局陈旧引用**：静态字段仍指向上一局的 GameWorld / Player，
    ///      其骨骼 Transform 已销毁 → `BifacialTransform.get_position()` NRE。
    ///      这**正是**上面那条异常的来源。
    ///
    /// ══════════════ 为什么挂这里而不是 GameWorld.OnDestroy ══════════════
    ///
    /// `SessionResultExitStatus.Show(...)` 是**结算界面**的入口 —— 它是
    /// 「玩家离开这一局」这个事件本身，而不是「世界正在被拆」。
    ///
    ///   · 时机正确：结算界面弹出时战局逻辑已结束，但托管侧上下文完好
    ///   · 语义正确：这就是"战局结束"，不是"某个对象被销毁"
    ///   · 无折叠风险：签名独特，不像 `OnDestroy` 那类极简方法会被
    ///     IL2CPP 折叠到共享入口（本项目真实事故：挂 GameWorld.OnDestroy
    ///     导致 64 万次调用 / 40MB 日志）
    ///
    /// 这个入口点的思路来自 SPT-DGLab 插件（作者 HiddenCirno）。
    /// </summary>
    [HarmonyPatch]
    internal static class RaidEndPatch
    {
        /// <summary>
        /// ⚠ **按「名字 + 参数个数」枚举，不按参数类型匹配签名。**
        ///
        ///   4.x 时代该方法的签名是：
        ///       Show(Profile, LastPlayerStateClass, ESideType, ExitStatus,
        ///            TimeSpan, ISession, bool)
        ///   而 5.0 变成了：
        ///       Show(Profile, PlayerVisualRepresentation, ESideType, ExitStatus,
        ///            Il2CppSystem.TimeSpan, IEftSession, bool)
        ///
        ///   **三个参数类型全变了。** 照抄旧签名会 `AccessTools.Method` 返回 null，
        ///   而这个补丁就静默失效 —— 我们在 ClearPrepareScreen 上刚被同一个坑咬过
        ///   （`System.DateTime` vs `Il2CppSystem.DateTime`）。
        ///
        ///   该方法有 1 参 / 4 参 / 7 参三个重载，取 7 参那个即可，不碰任何类型名。
        /// </summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var found = new List<MethodBase>();
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(SessionResultExitStatus)))
            {
                if (m.Name == "Show" && m.GetParameters().Length == 7) found.Add(m);
            }

            if (found.Count == 0)
            {
                // 绝不静默 —— 返回空集合会让 PatchAll 通过而实际零效果
                throw new MissingMethodException(
                    "SessionResultExitStatus.Show(7 参数) 未找到，战局结束清理将不会生效");
            }
            return found;
        }

        /// <summary>
        /// Postfix：结算界面显示后把战局标记置为 false。
        ///
        /// 只需翻转这一个标记 —— 其余清理都由既有逻辑接管：
        ///   · 绘制路径（OracleBehaviour.OnGUI）在 !InRaid 时发布【空帧】→ 屏幕被清空
        ///   · 四个数据桥管理器本来就在 !InRaid 时清自己的缓存
        ///     （OracleLootDataManager / OracleCorpseDataManager /
        ///       OracleTripwireManager / OracleWishlistDataManager）
        ///
        /// 用 Postfix 而非 Prefix：界面已经显示出来，说明这一局确实结束了，
        /// 此时再清最稳妥（Prefix 阶段万一 Show 抛异常会留下半清状态）。
        /// </summary>
        [HarmonyPostfix]
        private static void Postfix(ExitStatus __3)
        {
            try
            {
                // ⚠ `InRaid` 是**派生属性**（CurrentGameWorld 与 LocalPlayer 都非空即 true），
                //   不是可写标志。所以这里调 Clear() 把两个引用置空 —— InRaid 随之变 false，
                //   绘制路径（OnGUI）就会开始发布空帧，屏幕被清空。
                OracleGameState.Clear();
                OracleLog.Info($"[Oracle] 战局结束（ExitStatus={__3}），已清空战局状态与绘制缓存");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Oracle] 战局结束清理失败: {ex}");
            }
        }
    }
}
