using BepInEx.Configuration;
using EFT;
using EFT.Ballistics;
using EFT.Communications;
using HarmonyLib;
using Oracle.Data;
using Oracle.Utils;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.Combat
{
    /// <summary>
    /// 自瞄（魔法子弹）+ 后坐力控制
    ///
    /// ═══════════════ SPT5 签名适配（本项目最关键的移植点）═══════════════
    ///
    /// BallisticsCalculator.CreateShot 的「射手身份」参数换了类型，但**没有消失**：
    ///
    ///   4.1: CreateShot(Ammo, Vector3 origin, Vector3 direction, int fireIndex,
    ///                    **string player**（ProfileId）, Item weapon, float speedFactor, int fragmentIndex)
    ///
    ///   5.0: CreateShot(Ammo, Vector3 origin, Vector3 direction, int fireIndex,
    ///                    **Vector3 abc**, **int playerRaidId**, Item weapon, float speedFactor, int fragmentIndex)
    ///
    /// 即：string ProfileId → int RaidId，并在 fireIndex 之后插入了一个新的 Vector3。
    /// （佐证：CreateMultiProjectileShot 做了完全相同的改动。）
    /// 因此射手校验从「比较 ProfileId 字符串」变成「比较 RaidId 整数」，逻辑等价。
    ///
    /// ⚠ 不要声明无法确定语义的参数（如 abc）。Harmony 允许补丁只声明自己需要的参数，
    ///   按【参数名】绑定，未声明的参数自动忽略 —— 这正好绕开了 abc 的不确定性。
    ///
    /// 注意参数是【按值传递】，但 Harmony 支持对 byval 参数声明 ref 并写回，
    /// 这正是原版补丁修改 origin/direction 的方式（已由用户实测验证可行）。
    /// </summary>
    public class Aimbot : IOracleAimbot
    {
        /// <summary>自瞄目标的更新 Tick 间隔</summary>
        private static float _targetUpdateRate = 1f / 20f;
        private static float _lastUpdateTime;

        /// <summary>当前锁定的目标</summary>
        public static Player LockedTarget { get; private set; }

        /// <summary>绘制自瞄约束范围</summary>
        public static void DrawAimbotFOVCircle()
        {
            if (!AimbotCfg.EnableAimbot.Value || !AimbotCfg.DrawAimbotFov.Value) return;

            Vector2 screenCenter = new Vector2(Screen.width / 2f, Screen.height / 2f);
            float fovRadius = AimbotCfg.AimbotFovRadius.Value;
            OracleRendering.DrawCircle(screenCenter, fovRadius, new Color(1f, 0f, 0f, 0.3f), 64);
        }

        public void SubscribeEvent()
        {
            OracleEvent.OnUpdate += OnLogicUpdate;
            OracleEvent.OnDrawAimbot += OnDrawGUI;
        }

        private void OnLogicUpdate()
        {
            Camera cam = Camera.main;
            if (cam != null) UpdateTarget(cam);
        }

        private void OnDrawGUI()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            DrawAimbotFOVCircle();
            DrawTargetLine(cam);
        }

        /// <summary>
        /// 更新自瞄目标：在屏幕中心 FOV 内选出距离准心最近的可见敌人
        /// </summary>
        public static void UpdateTarget(Camera cam)
        {
            Player me = OracleGameState.LocalPlayer;
            var players = OracleGameState.CurrentGameWorld?.AllAlivePlayersList;
            if (me == null || players == null) return;

            // 限制更新频率
            if (Time.time - _lastUpdateTime < _targetUpdateRate) return;
            _lastUpdateTime = Time.time;

            // 关闭自瞄即释放目标
            if (!AimbotCfg.EnableAimbot.Value)
            {
                LockedTarget = null;
                return;
            }

            Vector2 screenCenter = new Vector2(Screen.width / 2f, Screen.height / 2f);
            float fovRadius = AimbotCfg.AimbotFovRadius.Value;
            int maxDist = AimbotCfg.AimbotMaxDistance.Value;
            Vector3 myPos = me.Transform.position;
            string myGroupId = OracleGameState.LocalGroupId;

            Player bestTarget = null;
            float minDistance = float.MaxValue;

            int count = OracleCollections.SafeCount(players);
            for (int i = 0; i < count; i++)
            {
                Player player = OracleCollections.SafeGet(players, i);
                if (player == null || player == me || player.PlayerBones == null) continue;

                // 过滤队友
                var info = player.Profile?.Info;
                if (info != null && !string.IsNullOrEmpty(myGroupId) && info.GroupId == myGroupId) continue;

                // 距离过滤
                if (!OracleCommon.IsInRange(maxDist, myPos, player.Transform.position)) continue;

                // 取瞄准部位（头 / 胸）
                Vector3? aimPos = GetAimBonePosition(player);
                if (!aimPos.HasValue) continue;

                // 深度过滤：背对时 screenPos.z <= 0
                Vector3 screenPos = cam.WorldToScreenPoint(aimPos.Value);
                if (screenPos.z <= 0.01f) continue;

                float distToCenter = Vector2.Distance(screenCenter, new Vector2(screenPos.x, screenPos.y));
                if (distToCenter > fovRadius) continue;

                if (distToCenter < minDistance)
                {
                    // 可见性判定（三点射线，任一点可见即可）
                    if (OraclePlayerDataManager.IsPlayerVisible(cam.transform.position, player,
                            OraclePlayerDataManager.HighPolyWithTerrainMask))
                    {
                        minDistance = distToCenter;
                        bestTarget = player;
                    }
                }
            }

            LockedTarget = bestTarget;
        }

        /// <summary>按配置取瞄准骨骼位置</summary>
        private static Vector3? GetAimBonePosition(Player player)
        {
            var bones = player.PlayerBones;
            if (bones == null) return null;

            return AimbotCfg.AimbotPartSetting.Value == EAimingPart.Head
                ? OraclePlayerDataManager.GetBonePos(bones.Head)
                : OraclePlayerDataManager.GetBonePos(bones.Spine3);
        }

        /// <summary>绘制目标锁定线</summary>
        public static void DrawTargetLine(Camera cam)
        {
            if (!AimbotCfg.EnableAimbot.Value || !AimbotCfg.DrawTargetLine.Value) return;

            Player target = LockedTarget;
            if (target == null || target.PlayerBones == null) return;

            Vector3? aimPos = GetAimBonePosition(target);
            if (!aimPos.HasValue) return;

            Vector3 screenPos = cam.WorldToScreenPoint(aimPos.Value);
            if (screenPos.z <= 0.01f) return;

            Vector2 screenCenter = new Vector2(Screen.width / 2f, Screen.height / 2f);

            // ⚠ 这里【不能】做 Screen.height - y 翻转。
            //
            // 本工程并存着两套 GL 坐标空间，混用会导致整条线上下镜像：
            //
            //   · 环境矩阵（不调用 LoadPixelMatrix，继承 OnGUI 的矩阵）
            //       → GUI 空间，原点在左上、Y 轴向下，需要 Screen.height - y。
            //       PlayerESP.DrawBoneLine 属于这一类，所以它翻转是对的。
            //
            //   · GL.LoadPixelMatrix()
            //       → Unity 官方文档明确写着「The coordinate (0,0) is at the bottom left
            //         corner of current camera's viewport」，即原点在左下、Y 轴向上，
            //         与 Camera.WorldToScreenPoint 的约定【完全一致】，因此直接用原值。
            //       本方法与 DrawCircle、TripwireESP 的连线都属于这一类。
            //
            // 之前误把骨骼线的翻转惯例套用到这里，导致锁定线上下镜像。
            // 之所以迟迟没暴露：FOV 圈与准星都是中心对称的，翻转与否看不出差别。
            var endPos = new Vector2(screenPos.x, screenPos.y);

            OracleRendering.DrawLine(screenCenter, endPos, OracleColorManager.AimbotCircle);
        }
    }

    // ══════════════════════ Harmony Patch ══════════════════════

    /// <summary>
    /// 后坐力控制。
    ///
    /// SPT5 中 ShotEffector.Process 的签名为 void Process(float str)，
    /// 参数名 `str` 与原版补丁完全一致，可直接按名绑定。
    /// </summary>
    [HarmonyPatch(typeof(ShotEffector), nameof(ShotEffector.Process))]
    public static class NoRecoilPatch
    {
        private static bool Prefix(ShotEffector __instance, ref float str)
        {
            if (AimbotCfg.NoRecoil.Value)
            {
                // 完全消后坐：跳过原方法
                return false;
            }

            if (AimbotCfg.LowRecoil.Value)
            {
                // 降低后坐：按倍率缩放力值后交给原方法
                str *= AimbotCfg.LowRecoilMuti.Value;
            }

            return true;
        }
    }

    /// <summary>
    /// 魔法子弹 —— 改写弹道起点/方向，使子弹飞向锁定目标。
    ///
    /// 只声明需要的参数（按名绑定），完全避开语义不明的 abc 参数。
    /// </summary>
    [HarmonyPatch(typeof(BallisticsCalculator), nameof(BallisticsCalculator.CreateShot))]
    public static class MagicBulletPatch
    {
        public static void Prefix(
            ref Vector3 origin,
            ref Vector3 direction,
            int playerRaidId,      // SPT5：替代 4.1 的 string player（ProfileId）
            ref float speedFactor)
        {
            if (!AimbotCfg.EnableAimbot.Value) return;

            Player target = Aimbot.LockedTarget;
            if (target == null) return;

            Player me = OracleGameState.LocalPlayer;
            if (me == null) return;

            // 只有本地玩家开的枪才改写弹道，否则 AI 的子弹也会被吸过来。
            //
            // playerRaidId 的语义已由实机日志证实为【射手的 RaidId】：
            //   [Oracle] 进入战局: player=500test group=<solo> alive=14   ← 场上有 AI
            //   自瞄诊断: 开火者 RaidId=1088, 本机 RaidId=1008 —— 不匹配   ← AI 的枪，被拦下
            //   自瞄诊断: RaidId 匹配成功 (=1008)                          ← 本机，成功改写
            // 即 AI 的 RaidId 与本地玩家不同，天然被这个判定挡住。
            // （两条日志都用 OracleLog.Once 去重，每局各最多一条，不会刷屏。）
            if (playerRaidId != me.RaidId)
            {
                OracleLog.Once("aimbot_raidid_mismatch",
                    $"[Oracle] 自瞄诊断: 开火者 RaidId={playerRaidId}, 本机 RaidId={me.RaidId} —— 不匹配，弹道未被改写");
                return;
            }

            OracleLog.Once("aimbot_raidid_match",
                $"[Oracle] 自瞄诊断: RaidId 匹配成功 (={me.RaidId})，魔法子弹生效");

            Vector3? aimPos = GetAimBonePosition(target);
            if (!aimPos.HasValue) return;

            if (AimbotCfg.SuperMagicBullet.Value)
            {
                // 超级模式：从目标正上方往下打
                origin = aimPos.Value + Vector3.up * 0.2f;
                direction = Vector3.down;
            }
            else
            {
                // 常规模式：直接指向目标
                direction = (aimPos.Value - origin).normalized;
                speedFactor = AimbotCfg.MagicBulletSpeed.Value;
            }
        }

        private static Vector3? GetAimBonePosition(Player player)
        {
            var bones = player?.PlayerBones;
            if (bones == null) return null;

            return AimbotCfg.AimbotPartSetting.Value == EAimingPart.Head
                ? OraclePlayerDataManager.GetBonePos(bones.Head)
                : OraclePlayerDataManager.GetBonePos(bones.Spine3);
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(1)]
    public class AimbotCfg : IOracleCfg, IOracleKeyUpdate
    {
        internal static ConfigEntry<KeyCode> AimbotKey { get; set; }
        internal static ConfigEntry<KeyCode> ChangeAimTargetKey { get; set; }
        internal static ConfigEntry<bool> EnableAimbot { get; set; }
        internal static ConfigEntry<int> AimbotTargetUpdateRate { get; set; }
        internal static ConfigEntry<bool> SuperMagicBullet { get; set; }
        internal static ConfigEntry<bool> DrawAimbotFov { get; set; }
        internal static ConfigEntry<bool> DrawTargetLine { get; set; }
        internal static ConfigEntry<bool> NoRecoil { get; set; }
        internal static ConfigEntry<bool> LowRecoil { get; set; }
        internal static ConfigEntry<float> AimbotFovRadius { get; set; }
        internal static ConfigEntry<float> MagicBulletSpeed { get; set; }
        internal static ConfigEntry<float> LowRecoilMuti { get; set; }
        internal static ConfigEntry<int> AimbotMaxDistance { get; set; }
        internal static ConfigEntry<EAimingPart> AimbotPartSetting { get; set; }

        private const string Section = "1. 天堂支点 / Combat Module";

        public void Initialize(ConfigFile config)
        {
            EnableAimbot = config.Bind(
                Section, "启用自瞄逻辑", true,
                new ConfigDescription("cfg_combat_module_aimbot_enable_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_aimbot_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 300
                    }));

            AimbotKey = config.Bind(
                Section, "自瞄快捷键", KeyCode.F6,
                new ConfigDescription("cfg_combat_module_aimbot_enable_key_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_aimbot_enable_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 299
                    }));

            // ⚠ 枚举类型配置项在 F12 中会渲染成下拉框，而本环境的
            //   ConfigurationManager 下拉渲染依赖 GUI.SelectionGrid（IL2CPP unstrip 失败），
            //   展开该项会刷异常。这是环境缺陷（BepInEx.cfg 自身的下拉项同样受影响），
            //   非本插件问题。保持与原版一致，待 SPT 修复后自然恢复。
            AimbotPartSetting = config.Bind(
                Section, "自瞄位置选择", EAimingPart.Head,
                new ConfigDescription("cfg_combat_module_aimbot_part_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_aimbot_part_name".i18n(),
                        IsAdvanced = false,
                        Order = 298
                    }));

            ChangeAimTargetKey = config.Bind(
                Section, "切换瞄准部位", KeyCode.KeypadMultiply,
                new ConfigDescription("cfg_combat_module_aimbot_change_part_key_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_aimbot_change_part_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 297
                    }));

            AimbotMaxDistance = config.Bind(
                Section, "自瞄最大距离", 200,
                new ConfigDescription("cfg_combat_module_aimbot_max_distance_desc".i18n(),
                    new AcceptableValueRange<int>(10, 2000),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_aimbot_max_distance_name".i18n(),
                        IsAdvanced = false,
                        Order = 296
                    }));

            DrawAimbotFov = config.Bind(
                Section, "显示自瞄 FOV", true,
                new ConfigDescription("cfg_combat_module_aimbot_show_fov_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_aimbot_show_fov_name".i18n(),
                        IsAdvanced = false,
                        Order = 295
                    }));

            AimbotFovRadius = config.Bind(
                Section, "自瞄 FOV 半径", 150f,
                new ConfigDescription("cfg_combat_module_aimbot_fov_radius_desc".i18n(),
                    new AcceptableValueRange<float>(0f, 1000f),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_aimbot_fov_radius_name".i18n(),
                        IsAdvanced = false,
                        Order = 294
                    }));

            DrawTargetLine = config.Bind(
                Section, "显示目标锁定线", true,
                new ConfigDescription("cfg_combat_module_aimbot_show_target_line_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_aimbot_show_target_line_name".i18n(),
                        IsAdvanced = false,
                        Order = 293
                    }));

            AimbotTargetUpdateRate = config.Bind(
                Section, "自瞄目标更新频率", 20,
                new ConfigDescription("cfg_combat_module_aimbot_target_update_rate_desc".i18n(),
                    new AcceptableValueRange<int>(10, 50),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_aimbot_target_update_rate_name".i18n(),
                        IsAdvanced = false,
                        Order = 292
                    }));

            SuperMagicBullet = config.Bind(
                Section, "超级魔法子弹", false,
                new ConfigDescription("cfg_combat_module_aimbot_super_magic_bullet_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_aimbot_super_magic_bullet_name".i18n(),
                        IsAdvanced = false,
                        Order = 291
                    }));

            MagicBulletSpeed = config.Bind(
                Section, "魔法子弹加速度", 20f,
                new ConfigDescription("cfg_combat_module_aimbot_magic_bullet_speed_desc".i18n(),
                    new AcceptableValueRange<float>(10f, 100f),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_aimbot_magic_bullet_speed_name".i18n(),
                        IsAdvanced = false,
                        Order = 290
                    }));

            NoRecoil = config.Bind(
                Section, "消除武器后座", true,
                new ConfigDescription("cfg_combat_module_aimbot_disable_recoil_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_aimbot_disable_recoil_name".i18n(),
                        IsAdvanced = false,
                        Order = 289
                    }));

            LowRecoil = config.Bind(
                Section, "超低武器后座", true,
                new ConfigDescription("cfg_combat_module_aimbot_low_recoil_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_aimbot_low_recoil_name".i18n(),
                        IsAdvanced = false,
                        Order = 288
                    }));

            LowRecoilMuti = config.Bind(
                Section, "武器后坐倍率", 0.2f,
                new ConfigDescription("cfg_combat_module_aimbot_low_recoil_rate_desc".i18n(),
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_aimbot_low_recoil_rate_name".i18n(),
                        IsAdvanced = false,
                        Order = 287
                    }));
        }

        public void RegisterKeyUpdate()
        {
            OracleEvent.OnUpdate += KeyUpdate;
        }

        /// <summary>按键监听：切换自瞄 / 切换瞄准部位</summary>
        public static void KeyUpdate()
        {
            if (Input.GetKeyDown(AimbotKey.Value))
            {
                EnableAimbot.Value = !EnableAimbot.Value;
                var value = EnableAimbot.Value;
                OracleNotify.Message(
                    string.Format("message_aimbot_enable".i18n(),
                        value ? "text_enable".i18n() : "text_disable".i18n()),
                    value ? ENotificationIconType.Default : ENotificationIconType.Alert,
                    GlobalCfg.MuteNotice.Value);
            }

            if (Input.GetKeyDown(ChangeAimTargetKey.Value))
            {
                AimbotPartSetting.Value = AimbotPartSetting.Value == EAimingPart.Head
                    ? EAimingPart.Chest
                    : EAimingPart.Head;

                var value = AimbotPartSetting.Value;
                OracleNotify.Message(
                    string.Format("message_aimbot_change_part".i18n(),
                        value == EAimingPart.Head
                            ? "text_aimbot_part_head".i18n()
                            : "text_aimbot_part_chest".i18n()),
                    ENotificationIconType.Default,
                    GlobalCfg.MuteNotice.Value);
            }
        }
    }
}
