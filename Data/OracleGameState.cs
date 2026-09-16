using EFT;

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
        /// 清空状态。
        ///
        /// 由 SetInRaid 在开局时调用；战局结束的情况由 InRaid 的伪 null 检测覆盖，
        /// 不需要额外的销毁回调。保持幂等，可安全重复调用。
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
