using BepInEx.Configuration;
using EFT;
using EFT.Communications;
using HarmonyLib;
using Oracle.Data;
using Oracle.Utils;
using System;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.Ability
{
    /// <summary>
    /// 自由飞行（无视重力）
    ///
    /// 实现依据（EFT 4.1 反编译源码，SPT5 下已逐项复核）：
    ///   · 重力唯一实现：MovementContext.ApplyGravity —— 所有姿态状态共用
    ///   · 位移唯一咽喉：MovementContext.DirectApplyMotion 里的 CharacterController.Move
    ///   · PlatformMotion 是 public 属性，引擎每帧把它叠加进位移，
    ///     且非零时自动临时放开 SpeedLimit —— 现成的"外部速度"注入通道
    ///   · 坠落伤害基准：_startFallingHeight，由 ResetFlying() 重置到当前高度
    ///
    /// 所以飞行 = 跳过重力 + 每帧注入位移 + 每帧重置坠落基准，三者缺一不可。
    ///
    /// ⚠ SPT5 签名漂移（已核对 interop 元数据）：
    ///   ApplyGravity     4.1: ()                         5.0: (ref Vector3, float, bool)
    ///   DirectApplyMotion 4.1: (float)                   5.0: (Vector3, float)
    ///   CheckFlying      4.1: ()                         5.0: (float)
    ///   本模块的 patch 都只读 __instance，因此新增参数不影响逻辑，
    ///   但 **Harmony 的参数匹配按位置进行**，DirectApplyMotion 的
    ///   `float deltaTime` 必须改成按位置对齐的写法（见下方 patch 注释）。
    /// </summary>
    public static class FlyMode
    {
        /// <summary>
        /// 无重力状态（运行时，由"双击上升键"切换，不持久化）
        ///
        /// 两层状态：
        ///   EnableFlyMode（配置开关）= 能力总开关，关闭时一切照常
        ///   NoGravity（运行时）      = 双击上升键切换，开启才真正摘掉重力
        /// </summary>
        public static bool NoGravity { get; private set; }

        /// <summary>双击判定窗口（秒），参考 Minecraft 的 7 tick ≈ 0.35s</summary>
        private const float DoubleTapWindow = 0.3f;

        private static float _lastUpKeyDownTime = float.NegativeInfinity;

        /// <summary>本次无重力期间是否曾经离地（用于"触地关闭"：贴着地面起飞不算触地）</summary>
        private static bool _wasAirborne;

        /// <summary>
        /// 当前是否处于"无重力飞行" —— 必须只对本地玩家成立，
        /// 否则 AI 会被一起摘掉重力（ApplyGravity 是全体角色共用的）
        /// </summary>
        public static bool IsFlying(MovementContext mc)
        {
            if (mc == null) return false;
            if (!FlyModeCfg.EnableFlyMode.Value || !NoGravity) return false;

            Player me = OracleGameState.LocalPlayer;
            if (me == null) return false;

            return ReferenceEquals(mc, me.MovementContext);
        }

        /// <summary>
        /// 每帧钩子（由 OracleBehaviour.Update 驱动）：
        ///   1. 双击上升键切换无重力
        ///   2. 无重力期间用原生触地判定实现"触地关闭飞行"
        /// 仅在能力总开关开启、且在战局内时生效。
        /// </summary>
        public static void UpdateTick()
        {
            if (!FlyModeCfg.EnableFlyMode.Value) return;

            Player me = OracleGameState.LocalPlayer;
            if (me == null || me.MovementContext == null)
            {
                // 出局/切图时清掉运行时状态，避免残留
                ResetState();
                return;
            }

            CheckDoubleTap();

            if (NoGravity)
                CheckTouchGround(me.MovementContext);
        }

        private static void CheckDoubleTap()
        {
            KeyCode upKey = FlyModeCfg.FlyUpKey.Value;
            if (upKey == KeyCode.None) return;
            if (!Input.GetKeyDown(upKey)) return;

            float now = Time.unscaledTime;
            if (now - _lastUpKeyDownTime <= DoubleTapWindow)
            {
                _lastUpKeyDownTime = float.NegativeInfinity;
                ToggleNoGravity();
            }
            else
            {
                _lastUpKeyDownTime = now;
            }
        }

        /// <summary>
        /// 触地关闭飞行。
        ///
        /// 用游戏原生的 IsGrounded（射线判定结果），不自己发射线，判定与游戏完全一致。
        ///
        /// 必须要求"本次飞行期间曾经离地"（_wasAirborne）：
        /// 否则在地面上双击空格起飞的瞬间 IsGrounded 就是 true，会立刻被关掉。
        /// </summary>
        private static void CheckTouchGround(MovementContext mc)
        {
            if (!mc.IsGrounded)
            {
                _wasAirborne = true;
                return;
            }

            if (!_wasAirborne) return;
            _wasAirborne = false;

            if (!FlyModeCfg.ExitOnLanding.Value) return;

            ToggleNoGravity(true);
        }

        /// <summary>切换无重力状态</summary>
        /// <param name="byLanding">是否由"触地"触发（用不同的提示文案）</param>
        public static void ToggleNoGravity(bool byLanding = false)
        {
            NoGravity = !NoGravity;
            _wasAirborne = false;
            ApplyGrounderState(NoGravity);

            if (byLanding && !NoGravity)
            {
                OracleNotify.Message(
                    "message_fly_landing_off".i18n(),
                    ENotificationIconType.Alert,
                    GlobalCfg.MuteNotice.Value);
                return;
            }

            OracleNotify.Message(
                string.Format("message_fly_gravity_state".i18n(),
                    NoGravity ? "text_enable".i18n() : "text_disable".i18n()),
                NoGravity ? ENotificationIconType.Default : ENotificationIconType.Alert,
                GlobalCfg.MuteNotice.Value);
        }

        /// <summary>强制复位到"正常重力"（关闭总开关 / 离开战局时调用）</summary>
        public static void ResetState()
        {
            _lastUpKeyDownTime = float.NegativeInfinity;
            _wasAirborne = false;
            if (!NoGravity) return;

            NoGravity = false;
            ApplyGrounderState(false);
        }

        /// <summary>
        /// 计算本帧飞行位移。
        ///
        /// 注意 PlatformMotion 是"每帧位移"而不是速度：
        /// DirectApplyMotion 里是 motion / deltaTime 才还原成速度，
        /// 且 PlatformMotion 也会被逐帧清零 —— 因此每帧都要重新写。
        /// </summary>
        public static Vector3 ComputeDisplacement(MovementContext mc, float deltaTime)
        {
            float speed = FlyModeCfg.FlySpeed.Value;

            if (FlyModeCfg.SpeedUpKey.Value != KeyCode.None
                && Input.GetKey(FlyModeCfg.SpeedUpKey.Value))
            {
                speed *= Mathf.Max(1f, FlyModeCfg.SpeedUpMultiplier.Value);
            }

            Vector3 dir = Vector3.zero;

            // 水平：直接取玩家自己的移动输入（世界空间水平方向）。
            // 好处是完全跟随游戏内的按键绑定，不需要硬编码 WASD
            Vector3 horizontal = mc.AbsoluteMovementDirection;
            if (horizontal.sqrMagnitude > 1f) horizontal.Normalize();
            dir += horizontal;

            // 垂直：上升键 / 下降键
            if (FlyModeCfg.FlyUpKey.Value != KeyCode.None
                && Input.GetKey(FlyModeCfg.FlyUpKey.Value))
            {
                dir += Vector3.up;
            }

            if (FlyModeCfg.FlyDownKey.Value != KeyCode.None
                && Input.GetKey(FlyModeCfg.FlyDownKey.Value))
            {
                dir -= Vector3.up;
            }

            if (dir.sqrMagnitude > 1f) dir.Normalize();

            return dir * speed * deltaTime;
        }

        /// <summary>
        /// 进入/退出飞行时的收尾：
        ///   · 关/开地面吸附（开着的话地面求解器会把角色往地上拽）
        ///   · 退出时清掉残留的 PlatformMotion
        ///   · 重置坠落高度基准
        /// </summary>
        public static void ApplyGrounderState(bool flying)
        {
            Player me = OracleGameState.LocalPlayer;
            if (me == null) return;

            MovementContext mc = me.MovementContext;
            if (mc == null) return;

            try
            {
                mc.GrounderSetActive(!flying);
                if (!flying) mc.PlatformMotion = Vector3.zero;
                mc.ResetFlying();
            }
            catch (Exception err)
            {
                OracleLog.ErrorOnce("fly_grounder_failed",
                    $"[Oracle] 切换飞行状态失败: {err.Message}");
            }
        }
    }

    // ==================== Harmony Patch ====================

    /// <summary>
    /// 飞行时跳过重力。
    /// ApplyGravity 是所有姿态状态施加重力的唯一出口。
    /// 5.0 签名变为 (ref Vector3, float, bool)，但我们只用 __instance，故不受影响。
    /// </summary>
    [HarmonyPatch(typeof(MovementContext), nameof(MovementContext.ApplyGravity))]
    public static class FlyModeNoGravityPatch
    {
        public static bool Prefix(MovementContext __instance)
        {
            return !FlyMode.IsFlying(__instance);
        }
    }

    /// <summary>
    /// 注入飞行位移，并重置坠落高度基准（防落地摔伤）。
    ///
    /// ⚠ 签名漂移处理：4.1 是 DirectApplyMotion(float)，5.0 变成
    ///   DirectApplyMotion(Vector3, float)。Harmony 按位置匹配参数，
    ///   因此 __1 才是 deltaTime（__0 是新增的 Vector3）。
    ///   这里用 __1 显式取名，避免误接。
    ///
    /// 为什么每帧都要 ResetFlying：CheckFlying 会持续把 _startFallingHeight
    /// 顶到本次"飞行"的最高点，一旦落地就按高度差算腿伤，飞得越高摔得越惨。
    /// 把基准钉在当前高度后，落地高度差恒为 0。
    /// </summary>
    [HarmonyPatch(typeof(MovementContext), nameof(MovementContext.DirectApplyMotion))]
    public static class FlyModeMotionPatch
    {
        // ReSharper 会在意未使用的 __0，但它是指位占位符，必须保留
        public static void Prefix(MovementContext __instance, Vector3 __0, float __1)
        {
            if (!FlyMode.IsFlying(__instance)) return;

            __instance.PlatformMotion = FlyMode.ComputeDisplacement(__instance, __1);
            __instance.ResetFlying();
        }
    }

    /// <summary>
    /// 飞行期间把坠落高度基准钉在当前高度，彻底杜绝落地伤害。
    ///
    /// 在方法体执行前先 ResetFlying()，于是高度差恒为 0、无伤害，
    /// 同时不做 return false —— 保证 OnGrounded / FallHeight / JumpHeight
    /// 等落地回调仍然照常触发。
    /// </summary>
    [HarmonyPatch(typeof(MovementContext), nameof(MovementContext.CheckFlying))]
    public static class FlyModeNoFallDamagePatch
    {
        public static void Prefix(MovementContext __instance)
        {
            if (!FlyMode.IsFlying(__instance)) return;
            __instance.ResetFlying();
        }
    }

    /// <summary>
    /// 飞行时屏蔽跳跃 —— 上升键已占用空格，
    /// 不拦的话会同时触发跳跃状态机
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Jump))]
    public static class FlyModeBlockJumpPatch
    {
        public static bool Prefix(Player __instance)
        {
            if (__instance?.MovementContext == null) return true;
            return !FlyMode.IsFlying(__instance.MovementContext);
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(2)]
    public class FlyModeCfg : IOracleCfg, IOracleKeyUpdate
    {
        internal static ConfigEntry<bool> EnableFlyMode { get; set; }
        internal static ConfigEntry<bool> ExitOnLanding { get; set; }
        internal static ConfigEntry<float> FlySpeed { get; set; }
        internal static ConfigEntry<KeyCode> FlyUpKey { get; set; }
        internal static ConfigEntry<KeyCode> FlyDownKey { get; set; }
        internal static ConfigEntry<KeyCode> SpeedUpKey { get; set; }
        internal static ConfigEntry<float> SpeedUpMultiplier { get; set; }

        private const string Section = "2. 生命之树 / Ability Module";

        public void Initialize(ConfigFile config)
        {
            EnableFlyMode = config.Bind(
                Section, "启用飞行", false,
                new ConfigDescription("cfg_ability_module_fly_enable_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_ability_module_fly_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 199
                    }));

            ExitOnLanding = config.Bind(
                Section, "触地关闭飞行", true,
                new ConfigDescription("cfg_ability_module_fly_exit_on_landing_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_ability_module_fly_exit_on_landing_name".i18n(),
                        IsAdvanced = false,
                        Order = 193
                    }));

            FlySpeed = config.Bind(
                Section, "飞行速度", 10f,
                new ConfigDescription("cfg_ability_module_fly_speed_desc".i18n(),
                    new AcceptableValueRange<float>(1f, 100f),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_ability_module_fly_speed_name".i18n(),
                        IsAdvanced = false,
                        Order = 198
                    }));

            FlyUpKey = config.Bind(
                Section, "飞行上升键", KeyCode.Space,
                new ConfigDescription("cfg_ability_module_fly_up_key_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_ability_module_fly_up_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 197
                    }));

            FlyDownKey = config.Bind(
                Section, "飞行下降键", KeyCode.LeftControl,
                new ConfigDescription("cfg_ability_module_fly_down_key_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_ability_module_fly_down_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 196
                    }));

            SpeedUpKey = config.Bind(
                Section, "飞行加速键", KeyCode.LeftShift,
                new ConfigDescription("cfg_ability_module_fly_speedup_key_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_ability_module_fly_speedup_key_name".i18n(),
                        IsAdvanced = true,
                        Order = 195
                    }));

            SpeedUpMultiplier = config.Bind(
                Section, "飞行加速倍率", 3f,
                new ConfigDescription("cfg_ability_module_fly_speedup_mult_desc".i18n(),
                    new AcceptableValueRange<float>(1f, 20f),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_ability_module_fly_speedup_mult_name".i18n(),
                        IsAdvanced = true,
                        Order = 194
                    }));

            // 开关切换时收尾：关/开地面吸附 + 清理残留位移
            EnableFlyMode.SettingChanged += OnFlyModeChanged;
        }

        /// <summary>每帧钩子：检测"双击上升键"切换无重力</summary>
        public void RegisterKeyUpdate()
        {
            OracleEvent.OnUpdate += KeyUpdate;
        }

        public static void KeyUpdate()
        {
            FlyMode.UpdateTick();
        }

        private static void OnFlyModeChanged(object sender, EventArgs e)
        {
            bool enabled = EnableFlyMode.Value;

            if (!enabled)
            {
                // 关闭能力 → 立即恢复正常重力
                FlyMode.ResetState();
            }
            else
            {
                // 开启能力 → 先确保处于正常重力状态（无重力需双击上升键触发）
                FlyMode.ApplyGrounderState(false);
            }

            OracleNotify.Message(
                string.Format("message_fly_mode_enable".i18n(),
                    enabled ? "text_enable".i18n() : "text_disable".i18n()),
                enabled ? ENotificationIconType.Default : ENotificationIconType.Alert,
                GlobalCfg.MuteNotice.Value);
        }
    }
}
