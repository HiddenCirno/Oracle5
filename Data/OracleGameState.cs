using EFT;
using System;

namespace Oracle.Data
{
    /// <summary>
    /// 游戏运行时状态缓存。
    ///
    /// IL2CPP 移植要点：
    ///
    /// 1. 【销毁检测】不挂钩 OnDestroy，改用 Unity 的「伪 null」语义。
    ///    UnityEngine.Object 重载了 == / != ：对象被销毁后，即使托管包装器仍非 null，
    ///    与 null 比较也会返回 true。所以 `world == null` 足以判定世界已失效，
    ///    不需要任何销毁回调 —— 这正是我们移除 GameEndPatch 后仍能正确清理的依据。
    ///    （参见 Patches/GameStartPatch.cs 末尾关于方法体合并的事故记录。）
    ///
    /// 2. 【空引用】这些类型来自 interop 代理程序集，对 Il2CppInterop 而言
    ///    == null 会检查原生指针，因此判空是有效的。
    ///
    /// 3. 【持有游戏对象】这些是游戏自己创建的对象，由游戏侧强引用，长期持有安全。
    ///    （Il2CppInterop 警告的「可能被回收」针对的是【我们自己注入】的类型。）
    /// </summary>
    public static class OracleGameState
    {
        /// <summary>
        /// 藏身处场景的 LocationId。
        ///
        /// **这是游戏自己用的判据**，不是我们发明的常量 —— 见
        /// `EFT/Player.cs`：`if (Singleton&lt;GameWorld&gt;.Instance.LocationId == "hideout" &amp;&amp; ...)`。
        /// 5.0 的 interop 里 `EFT.GameWorld.LocationId` 仍是 public 属性，可直接读。
        /// </summary>
        private const string HideoutLocationId = "hideout";

        /// <summary>当前战局的 GameWorld；不在战局或已销毁时为 null</summary>
        public static GameWorld CurrentGameWorld { get; private set; }

        /// <summary>本地玩家；不在战局或已销毁时为 null</summary>
        public static Player LocalPlayer { get; private set; }

        /// <summary>本地玩家所在小队 ID；单人为空串</summary>
        public static string LocalGroupId { get; private set; } = string.Empty;

        /// <summary>本地玩家昵称</summary>
        public static string LocalNickname { get; private set; } = string.Empty;

        /// <summary>
        /// 仓库界面的库存控制器（战局外造物用）。
        ///
        /// 由 ItemSpawnStashPatch 在 InventoryScreen.Show 时捕获。
        /// 刻意不放进 Clear()：它是"界面级"状态而非"战局级"状态，
        /// 打开仓库发生在战局之外，若随战局清理反而会丢失。
        /// </summary>
        public static EFT.InventoryLogic.InventoryController StashController { get; set; }

        /// <summary>
        /// 是否处于战局中。
        ///
        /// 这里的 `!= null` 走的是 Unity 重载的运算符，对【已销毁】对象同样返回 true 的否定，
        /// 所以战局结束后本属性会自动变回 false，无需销毁回调。
        /// </summary>
        public static bool InRaid
        {
            get
            {
                // 显式比较以绑定 Unity 的 op_Inequality；不要用 ?. 或 ReferenceEquals，
                // 后者会绕过 Unity 的销毁检测，把已销毁对象当成有效对象。
                if (CurrentGameWorld == null) return false;

                // ★ 非战局的本地场景一律不算战局 —— 藏身处（HideoutGameWorld）与
                //   商人访问（NarrateGameWorld）都属于这一类。它们同样会跑起 GameWorld、
                //   同样触发 OnGameStarted、同样有 MainPlayer，少了这两句就会把
                //   ESP / 能力 / 自瞄等全部点亮（实机表现："藏身处/商人界面里功能被错误激活"）。
                //
                //   第一个是便宜的快路径，第二个是权威判据（见各自注释）。
                if (IsHideoutWorld(CurrentGameWorld)) return false;
                if (IsNonRaidLocalGame(CurrentGameWorld)) return false;

                if (LocalPlayer == null) return false;
                return true;
            }
        }

        /// <summary>当前存活玩家数；不在战局或列表为空时为 0</summary>
        public static int AlivePlayerCount
        {
            get
            {
                if (!InRaid) return 0;
                var list = CurrentGameWorld.AllAlivePlayersList;
                if (list == null) return 0;
                return list.Count;
            }
        }

        /// <summary>
        /// 战局开始时捕获关键实例（由 GameStartPatch 调用）。
        /// 逐层判空，任一环节缺失都中止并留下可诊断的状态。
        /// </summary>
        public static void SetInRaid(GameWorld gameWorld)
        {
            // 先清空，避免上一局的残留引用把本局状态污染成「半新半旧」。
            // 这也是不需要 GameEndPatch 的原因：开局时状态自然重置。
            Clear();

            if (gameWorld == null) return;

            // ★ 非战局的本地场景（藏身处 / 商人访问 / 旁白演出）直接保持
            //   上面 Clear() 之后的【空状态】。
            //
            //   它们同样会触发 GameWorld.OnGameStarted（实机日志坐实：一次此类加载
            //   掉进了 StartLoadHideoutBundles … UnloadHideout 的区间里，且
            //   `[转移点] 当前地图` 读出来是空的、alive=1）。若照收不误，后果有两个：
            //     1. 所有战局功能在这些场景被点亮；
            //     2. LocalPlayer 被填上 → 仓库造物误判成"战局内"，
            //        去走"塞进背包"那条分支 → 在仓库里生成物品没反应。
            if (IsHideoutWorld(gameWorld)) return;
            if (IsNonRaidLocalGame(gameWorld)) return;

            CurrentGameWorld = gameWorld;

            Player player = gameWorld.MainPlayer;
            if (player == null) return;
            LocalPlayer = player;

            Profile profile = player.Profile;
            if (profile == null) return;

            ProfileInfo info = profile.Info;
            if (info == null) return;

            string groupId = info.GroupId;
            LocalGroupId = string.IsNullOrEmpty(groupId) ? string.Empty : groupId;

            string nickname = info.Nickname;
            LocalNickname = string.IsNullOrEmpty(nickname) ? string.Empty : nickname;
        }

        /// <summary>
        /// 这个 GameWorld 是不是【藏身处】的（廉价快路径）。
        ///
        /// ⚠ 判据必须是 LocationId，**不能**用"有没有 MainPlayer"之类 ——
        ///   藏身处两样都有，那正是它被误当成战局的原因。
        ///
        /// ⚠⚠ **本函数不足以单独作为防线**，两个原因：
        ///   1. **时序**：`OnGameStarted` 触发那一刻 `LocationId` 可能还是空串 ——
        ///      同一时刻别的插件读到的 `[转移点] 当前地图` 就是空的。
        ///   2. **覆盖面**：它只认藏身处。**商人访问根本不走这里** ——
        ///      实机实测那条路径上 `LocationId` 读出来是 **null**，
        ///      因为那是 `NarrateGameWorld` 而不是藏身处（见 `IsNonRaidLocalGame`）。
        ///   所以权威判据是下面那个基于类型的 `IsNonRaidLocalGame()`，
        ///   本函数只当"便宜的先手筛选"。
        ///
        /// 读 LocationId 包一层 try：属性来自 interop，正常的游戏世界里
        /// 不会抛，但整条状态链路不值得为了它冒一次异常的风险。
        /// </summary>
        public static bool IsHideoutWorld(GameWorld world)
        {
            if (world == null) return false;

            string locationId;
            try { locationId = world.LocationId; }
            catch { return false; }

            return string.Equals(locationId, HideoutLocationId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// ★★ **权威判据**：这个 GameWorld 属于"非战局场景"。
        ///
        /// ══════════════ 判据是怎么定下来的（两次失败之后才落到这里）══════════════
        ///
        /// 前两版判据都**实测失效**，实机日志留下了证据：
        ///
        ///   ① `GameWorld.LocationId == "hideout"` —— **失败**。
        ///      商人访问场景下 `LocationId` 读出来是 **null**，不是 "hideout"。
        ///      （当初是照抄游戏自己在 `EFT/Player.cs` 里的写法，但那只覆盖"进藏身处"，
        ///        不覆盖商人访问。）
        ///
        ///   ② `Singleton&lt;AbstractGame&gt;.Instance.InRaid` —— **失败**。
        ///      实机诊断打印：`AbstractGame: &lt;null&gt;`。
        ///      这个泛型实例在 il2cpp 侧解析不出来，`Instance` 永远是 null，
        ///      于是"读不到就放行"的失败安全设计让它**一次都没否决过**。
        ///      （⚠ 一般教训：失败安全会把"判据没生效"伪装成"判据判定为否"，
        ///        两种情况的修法完全相反 —— 所以判据必须有可观测的日志，不能纯靠推理。）
        ///
        ///   ③ **本版：看 GameWorld 自己的类型** —— 实机诊断一次就定住了。
        ///      诊断输出（点开商人时）：
        ///          world='NarrateWorld' | world类型: HideoutGW=False NarrateGW=True NetworkGW=False
        ///      GameWorld 的类型层次是：
        ///
        ///          EFT.GameWorld (MonoBehaviour)
        ///          └─ ClientGameWorld
        ///             ├─ ClientLocalGameWorld
        ///             │  ├─ HideoutGameWorld     ← 藏身处
        ///             │  └─ NarrateGameWorld     ← 商人访问 / 旁白演出  ★就是它
        ///             └─ ClientNetworkGameWorld  ← 线上战局
        ///
        ///      真战局落在 `ClientLocalGameWorld`（离线）或 `ClientNetworkGameWorld`（线上），
        ///      **既不是 HideoutGameWorld 也不是 NarrateGameWorld** —— 判据成立。
        ///
        /// ⚠ 用 `TryCast`（原生类型检查），绝不用 `is` / `GetType().Name`
        ///   —— 本项目已证实：跨边界传回的 interop 代理按**声明类型**包装，那两种写法会骗人。
        ///
        /// ⚠ 失败安全：读不到/抛异常一律返回 false（当战局处理）。
        ///   宁可漏判一个非战局场景，也绝不能把真战局毙掉。
        /// </summary>
        public static bool IsNonRaidLocalGame(GameWorld world)
        {
            if (world == null) return false;

            try
            {
                if (world.TryCast<NarrateGameWorld>() != null) return true;
                if (world.TryCast<HideoutGameWorld>() != null) return true;
            }
            catch
            {
                return false;
            }

            return false;
        }

        /// <summary>
        /// 每帧轻量校验（由 OracleBehaviour.Update 调用）。
        ///
        /// ══════════════ 为什么是"被动校验"而不是"退出钩子" ══════════════
        ///
        /// 本项目**不能再挂钩场景退出类回调**，两条路都试过：
        ///
        ///   · `GameWorld.OnDestroy` —— 被 IL2CPP 的方法体合并折叠成了全游戏共享
        ///     入口，实测触发 64 万次 / 40MB 日志 / 严重掉帧（详见 GameStartPatch 末尾）。
        ///   · `SessionResultExitStatus.Show` —— 时机正确、已在用，但它**只覆盖
        ///     正规结算**（撤离/死亡/迷失）。走出藏身处、或从菜单直接离开，
        ///     都不经过它。实机日志里"战局结束"一条都没有，就是这条。
        ///
        /// 所以改成每帧只做几次判空：世界没了、或换成了藏身处，就清理。
        /// 开销可忽略，且完全不依赖补丁 —— 不会再被折叠问题波及。
        /// </summary>
        public static void Validate()
        {
            GameWorld world = CurrentGameWorld;

            // Unity 的伪 null：世界被销毁后，包装器与 null 比较会返回 true
            if (world == null)
            {
                // 常态（主菜单里 CurrentGameWorld 本来就是 null）不必每帧重复写
                if (LocalPlayer != null) Clear();
                return;
            }

            // 场景换成了非战局的本地场景（藏身处 / 商人访问 / Narrate）：
            // 把战局状态清掉 —— 功能随之熄灭，仓库造物也回到仓库分支。
            //
            // ⚠ 这一条同时兜住 `SetInRaid` 的时序漏洞：那里读 LocationId 时它可能
            //   还是空串，于是当帧没拦住；下一帧起 AbstractGame 已经就位，
            //   这里就能认出来。两个判据都只"否决"，读不到时一律放行，
            //   所以真战局不会被误清。
            if (IsHideoutWorld(world) || IsNonRaidLocalGame(world)) Clear();
        }

        /// <summary>
        /// 清空状态。
        ///
        /// 三个调用点：SetInRaid 开头（开局重置）、RaidEndPatch（正规结算）、
        /// Validate（每帧兜底）。保持幂等，可安全重复调用。
        /// </summary>
        public static void Clear()
        {
            CurrentGameWorld = null;
            LocalPlayer = null;
            LocalGroupId = string.Empty;
            LocalNickname = string.Empty;
        }
    }
}
