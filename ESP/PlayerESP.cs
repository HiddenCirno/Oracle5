using BepInEx.Configuration;
using EFT;
using EFT.Communications;
using Oracle.Data;
using Oracle.Utils;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.ESP
{
    /// <summary>
    /// 玩家透视
    ///
    /// IL2CPP 移植要点：
    ///   · 玩家列表改【索引循环】而非 foreach —— Il2Cpp 活集合的枚举器开销大且在
    ///     游戏侧增删时会抛异常（AllAlivePlayersList 每局都在变动）。
    ///   · 骨骼绘制必须在 EventType.Repaint 阶段，否则 GL 调用会被 Unity 拒绝。
    ///   · 坐标换算依赖 Screen.height 反转 Y 轴（IMGUI 原点在左上，屏幕坐标为左下）。
    /// </summary>
    public class PlayerESP : IOracleESP
    {
        public void SubscribeEvent()
        {
            OracleEvent.OnDrawESP += OnDrawESP;
        }

        /// <summary>
        /// 绘制部分
        /// </summary>
        private void OnDrawESP()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            //文本和血条
            DrawPlayerText(cam, OracleRendering.EspTextStyle);
            DrawAllPlayerHealthBars(cam);

            //火柴人（仅重绘阶段）
            if (Event.current.type == EventType.Repaint && OracleRendering.IsReady)
            {
                OracleRendering.EspMaterial.SetPass(0);
                GL.PushMatrix();
                GL.Begin(GL.LINES);
                DrawPlayerBone(cam);
                GL.End();
                GL.PopMatrix();
            }
        }

        /// <summary>
        /// 绘制玩家骨骼
        /// </summary>
        public static void DrawPlayerBone(Camera cam)
        {
            if (!PlayerESPCfg.EnablePlayerESP.Value) return;
            if (!PlayerESPCfg.EnablePlayerBoneESP.Value) return;

            Player me = OracleGameState.LocalPlayer;
            var players = OracleGameState.CurrentGameWorld?.AllAlivePlayersList;
            int count = OracleCollections.SafeCount(players);
            if (me == null || count == 0) return;

            Vector3 myPos = me.Transform.position;
            int maxDist = PlayerESPCfg.PlayerESPMaxDistance.Value;
            Vector3 camPos = cam.transform.position;

            for (int i = 0; i < count; i++)
            {
                Player player = OracleCollections.SafeGet(players, i);
                if (player == null || player == me || player.PlayerBones == null) continue;
                if (!OracleCommon.IsInRange(maxDist, myPos, player.Transform.position)) continue;

                //遮挡关系判定：决定火柴人配色
                bool canPlayerSeeBot = OraclePlayerDataManager.IsPlayerVisible(camPos, player, OraclePlayerDataManager.HighPolyWithTerrainMask);
                bool canBotSeePlayer = OraclePlayerDataManager.IsBotVisible(player, me, OraclePlayerDataManager.HighPolyWithTerrainMask);

                Color finalColor;
                if (canBotSeePlayer) finalColor = OracleColorManager.EnemyDangerous;      // AI 看得见你
                else if (canPlayerSeeBot) finalColor = OracleColorManager.EnemyWarning;   // 你看得见 AI
                else finalColor = OracleColorManager.EnemySafe;                           // 互相看不见

                var bones = player.PlayerBones;

                //头颈腰臀
                Vector3? head = OraclePlayerDataManager.GetBonePos(bones.Head);
                Vector3? neck = OraclePlayerDataManager.GetBonePos(bones.Neck);
                Vector3? spine3 = OraclePlayerDataManager.GetBonePos(bones.Spine3);
                Vector3? pelvis = OraclePlayerDataManager.GetBonePos(bones.Pelvis);
                //肩膀
                Vector3? lShoulder = OraclePlayerDataManager.GetBonePos(bones.LeftShoulder);
                Vector3? rShoulder = OraclePlayerDataManager.GetBonePos(bones.RightShoulder);
                //大臂 / 小臂（Il2CppReferenceArray，用 Length 而非 Count）
                Vector3? lUpperarm = (bones.Upperarms != null && bones.Upperarms.Length > 0) ? OraclePlayerDataManager.GetBonePos(bones.Upperarms[0]) : null;
                Vector3? rUpperarm = (bones.Upperarms != null && bones.Upperarms.Length > 1) ? OraclePlayerDataManager.GetBonePos(bones.Upperarms[1]) : null;
                Vector3? lForearm = (bones.Forearms != null && bones.Forearms.Length > 0) ? OraclePlayerDataManager.GetBonePos(bones.Forearms[0]) : null;
                Vector3? rForearm = (bones.Forearms != null && bones.Forearms.Length > 1) ? OraclePlayerDataManager.GetBonePos(bones.Forearms[1]) : null;
                //手掌
                Vector3? lPalm = OraclePlayerDataManager.GetBonePos(bones.LeftPalm);
                Vector3? rPalm = OraclePlayerDataManager.GetBonePos(bones.RightPalm);

                //左腿：大腿->膝->小腿->脚（腿部结构与常规骨骼不同，需顺位取子节点）
                Vector3? lThigh1 = OraclePlayerDataManager.GetBonePos(bones.LeftThigh1);
                Vector3? lKnee = OraclePlayerDataManager.GetBonePos(bones.LeftThigh2);
                Vector3? lCalf = null;
                Vector3? lFoot = null;
                if (bones.LeftThigh2?.Original != null && bones.LeftThigh2.Original.childCount > 0)
                {
                    Transform calfT = bones.LeftThigh2.Original.GetChild(0);
                    lCalf = calfT.position;
                    if (calfT != null && calfT.childCount > 0)
                    {
                        lFoot = calfT.GetChild(0).position;
                    }
                }

                //右腿
                Vector3? rThigh1 = OraclePlayerDataManager.GetBonePos(bones.RightThigh1);
                Vector3? rKnee = OraclePlayerDataManager.GetBonePos(bones.RightThigh2);
                Vector3? rCalf = null;
                Vector3? rFoot = null;
                if (bones.RightThigh2?.Original != null && bones.RightThigh2.Original.childCount > 0)
                {
                    Transform calfT = bones.RightThigh2.Original.GetChild(0);
                    rCalf = calfT.position;
                    if (calfT != null && calfT.childCount > 0)
                    {
                        rFoot = calfT.GetChild(0).position;
                    }
                }

                //按部位血量动态着色
                GL.Color(GetDynamicLimbColor(player, EBodyPart.Head, finalColor));
                DrawBoneLine(cam, head, neck);
                GL.Color(GetDynamicLimbColor(player, EBodyPart.Chest, finalColor));
                DrawBoneLine(cam, neck, spine3);
                GL.Color(GetDynamicLimbColor(player, EBodyPart.Stomach, finalColor));
                DrawBoneLine(cam, spine3, pelvis);

                GL.Color(GetDynamicLimbColor(player, EBodyPart.LeftArm, finalColor));
                DrawBoneLine(cam, neck, lShoulder);
                DrawBoneLine(cam, lShoulder, lUpperarm);
                DrawBoneLine(cam, lUpperarm, lForearm);
                DrawBoneLine(cam, lForearm, lPalm);

                GL.Color(GetDynamicLimbColor(player, EBodyPart.RightArm, finalColor));
                DrawBoneLine(cam, neck, rShoulder);
                DrawBoneLine(cam, rShoulder, rUpperarm);
                DrawBoneLine(cam, rUpperarm, rForearm);
                DrawBoneLine(cam, rForearm, rPalm);

                GL.Color(GetDynamicLimbColor(player, EBodyPart.LeftLeg, finalColor));
                DrawBoneLine(cam, pelvis, lThigh1);
                DrawBoneLine(cam, lThigh1, lKnee);
                DrawBoneLine(cam, lKnee, lCalf);
                DrawBoneLine(cam, lCalf, lFoot);

                GL.Color(GetDynamicLimbColor(player, EBodyPart.RightLeg, finalColor));
                DrawBoneLine(cam, pelvis, rThigh1);
                DrawBoneLine(cam, rThigh1, rKnee);
                DrawBoneLine(cam, rKnee, rCalf);
                DrawBoneLine(cam, rCalf, rFoot);
            }
        }

        /// <summary>
        /// 绘制玩家信息（悬浮于头顶）
        /// </summary>
        public static void DrawPlayerText(Camera cam, GUIStyle textStyle)
        {
            if (textStyle == null) return;
            if (!PlayerESPCfg.EnablePlayerESP.Value) return;
            if (!PlayerESPCfg.EnablePlayerInfoESP.Value) return;

            Player me = OracleGameState.LocalPlayer;
            var players = OracleGameState.CurrentGameWorld?.AllAlivePlayersList;
            int count = OracleCollections.SafeCount(players);
            if (me == null || count == 0) return;

            Vector3 myPos = me.Transform.position;
            int maxDist = PlayerESPCfg.PlayerESPMaxDistance.Value;
            textStyle.richText = true;

            for (int i = 0; i < count; i++)
            {
                Player player = OracleCollections.SafeGet(players, i);
                if (player == null || player == me || player.PlayerBones == null) continue;
                if (!OracleCommon.IsInRange(maxDist, myPos, player.Transform.position)) continue;

                bool isTeammate = OraclePlayerDataManager.IsTeammate(player.Profile?.Info);

                Vector3? headPos = OraclePlayerDataManager.GetBonePos(player.PlayerBones.Head);
                if (!headPos.HasValue) continue;

                //向头顶偏移，避免与骨骼重叠
                Vector3 textScreenPos = cam.WorldToScreenPoint(headPos.Value + new Vector3(0, 0.3f, 0));
                if (textScreenPos.z <= 0.01f) continue; // 背后不画

                var info = OraclePlayerDataManager.GetEntityInfo(player, isTeammate);
                float screenX = textScreenPos.x;
                float screenY = Screen.height - textScreenPos.y;

                GUI.Label(new Rect(screenX - 100, screenY - 20, 200, 40), info.ToEspString(), textStyle);
            }
        }

        /// <summary>
        /// 绘制所有玩家血条
        /// </summary>
        public static void DrawAllPlayerHealthBars(Camera cam)
        {
            if (!PlayerESPCfg.EnablePlayerESP.Value) return;
            if (!PlayerESPCfg.EnablePlayerHealthBarESP.Value) return;

            Player me = OracleGameState.LocalPlayer;
            var players = OracleGameState.CurrentGameWorld?.AllAlivePlayersList;
            int count = OracleCollections.SafeCount(players);
            if (me == null || count == 0) return;

            Vector3 myPos = me.Transform.position;
            int maxDist = PlayerESPCfg.PlayerESPMaxDistance.Value;

            for (int i = 0; i < count; i++)
            {
                Player player = OracleCollections.SafeGet(players, i);
                if (player == null || player == me) continue;
                if (!OracleCommon.IsInRange(maxDist, myPos, player.Transform.position)) continue;

                DrawPlayerHealthBar(cam, player);
            }
        }

        /// <summary>
        /// 绘制单个玩家血条（脚底位置）
        /// </summary>
        public static void DrawPlayerHealthBar(Camera cam, Player player)
        {
            if (player == null) return;

            Vector3 feetScreenPos = cam.WorldToScreenPoint(player.Transform.position);
            if (feetScreenPos.z <= 0.01f) return;

            OraclePlayerDataManager.GetPlayerTotalHealth(player, out float curHp, out float maxHp);
            if (maxHp <= 0f) return;

            float hpPercent = Mathf.Clamp01(curHp / maxHp);

            float screenX = feetScreenPos.x;
            float screenY = Screen.height - feetScreenPos.y;
            const float barWidth = 60f;
            const float barHeight = 4f;
            float barX = screenX - (barWidth / 2f);
            float barY = screenY + 5f;

            Color oldGuiColor = GUI.color;

            //底槽
            GUI.color = OracleColorManager.HealthBarBG;
            GUI.DrawTexture(new Rect(barX, barY, barWidth, barHeight), Texture2D.whiteTexture);

            //颜色随血量渐变
            Color hpColor;
            if (hpPercent > 0.5f)
            {
                float t = (hpPercent - 0.5f) * 2f;
                hpColor = Color.Lerp(OracleColorManager.HealthBarHalf, OracleColorManager.HealthBarFull, t);
            }
            else
            {
                float t = hpPercent * 2f;
                hpColor = Color.Lerp(OracleColorManager.HealthBarQuarter, OracleColorManager.HealthBarHalf, t);
            }

            GUI.color = hpColor;
            GUI.DrawTexture(new Rect(barX, barY, barWidth * hpPercent, barHeight), Texture2D.whiteTexture);

            GUI.color = oldGuiColor;
        }

        /// <summary>
        /// 绘制骨骼连线：世界坐标 -> 屏幕坐标，并做深度剔除。
        /// 深度检查不能省 —— 否则贴脸的 AI 骨骼线会满天飞。
        /// </summary>
        public static void DrawBoneLine(Camera cam, Vector3? p1, Vector3? p2)
        {
            if (!p1.HasValue || !p2.HasValue) return;

            Vector3 s1 = cam.WorldToScreenPoint(p1.Value);
            Vector3 s2 = cam.WorldToScreenPoint(p2.Value);

            if (s1.z > 0.01f && s2.z > 0.01f)
            {
                //反转 Y 轴适配 IMGUI 坐标系
                GL.Vertex3(s1.x, Screen.height - s1.y, 0);
                GL.Vertex3(s2.x, Screen.height - s2.y, 0);
            }
        }

        /// <summary>
        /// 按部位血量动态计算骨骼颜色（血量越低越偏向"损毁色"）
        /// </summary>
        public static Color GetDynamicLimbColor(Player player, EBodyPart part, Color baseColor)
        {
            if (!PlayerESPCfg.EnablePlayerBoneESPHealthMode.Value) return baseColor;

            float healthPercent = OraclePlayerDataManager.GetBodyPartHealthPercent(player, part);
            if (healthPercent < 0f) return baseColor;      // 数据不可用，保留基础色
            if (healthPercent <= 0.01f) return OracleColorManager.EnemyPartDestroy; // 部位废掉

            //根据基础色选渐变目标
            Color targetColor;
            if (baseColor == (Color)OracleColorManager.EnemySafe) targetColor = OracleColorManager.EnemySafeDestroy;
            else if (baseColor == (Color)OracleColorManager.EnemyWarning) targetColor = OracleColorManager.EnemyWarningDestroy;
            else targetColor = OracleColorManager.EnemyDangerousDestroy;

            float lerpFactor = Mathf.Lerp(0.5f, 1.0f, healthPercent);
            return Color.Lerp(targetColor, baseColor, lerpFactor);
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(3)]
    public class PlayerESPCfg : IOracleCfg, IOracleKeyUpdate
    {
        internal static ConfigEntry<bool> EnablePlayerESP { get; set; }
        internal static ConfigEntry<bool> EnablePlayerInfoESP { get; set; }
        internal static ConfigEntry<bool> EnablePlayerBoneESP { get; set; }
        internal static ConfigEntry<bool> EnablePlayerHealthBarESP { get; set; }
        internal static ConfigEntry<bool> EnablePlayerBoneESPHealthMode { get; set; }
        internal static ConfigEntry<int> PlayerESPMaxDistance { get; set; }
        internal static ConfigEntry<KeyCode> PlayerESPKey { get; set; }

        public void Initialize(ConfigFile config)
        {
            const string section = "3. 巡天星轨 / ESP Module";

            EnablePlayerESP = config.Bind(
                section, "启用玩家透视", true,
                new ConfigDescription("cfg_esp_module_player_esp_enable_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_player_esp_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 160
                    }));

            PlayerESPKey = config.Bind(
                section, "玩家透视快捷键", KeyCode.F2,
                new ConfigDescription("cfg_esp_module_player_esp_enable_key_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_player_esp_enable_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 159
                    }));

            PlayerESPMaxDistance = config.Bind(
                section, "透视范围", 200,
                new ConfigDescription("cfg_esp_module_player_esp_max_distance_desc".i18n(),
                    new AcceptableValueRange<int>(50, 2000),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_player_esp_max_distance_name".i18n(),
                        IsAdvanced = false,
                        Order = 158
                    }));

            EnablePlayerInfoESP = config.Bind(
                section, "启用玩家信息透视", true,
                new ConfigDescription("cfg_esp_module_player_esp_show_info_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_player_esp_show_info_name".i18n(),
                        IsAdvanced = false,
                        Order = 157
                    }));

            EnablePlayerHealthBarESP = config.Bind(
                section, "启用玩家血条透视", true,
                new ConfigDescription("cfg_esp_module_player_esp_show_health_bar_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_player_esp_show_health_bar_name".i18n(),
                        IsAdvanced = false,
                        Order = 156
                    }));

            EnablePlayerBoneESP = config.Bind(
                section, "启用玩家骨骼透视", true,
                new ConfigDescription("cfg_esp_module_player_esp_show_bone_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_player_esp_show_bone_name".i18n(),
                        IsAdvanced = false,
                        Order = 155
                    }));

            EnablePlayerBoneESPHealthMode = config.Bind(
                section, "启用玩家骨骼透视血量叠加", true,
                new ConfigDescription("cfg_esp_module_player_esp_show_bone_color_overlay_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_esp_module_player_esp_show_bone_color_overlay_name".i18n(),
                        IsAdvanced = false,
                        Order = 154
                    }));
        }

        public void RegisterKeyUpdate()
        {
            OracleEvent.OnUpdate += KeyUpdate;
        }

        public static void KeyUpdate()
        {
            if (Input.GetKeyDown(PlayerESPKey.Value))
            {
                EnablePlayerESP.Value = !EnablePlayerESP.Value;
                var value = EnablePlayerESP.Value;
                OracleNotify.Message(
                    string.Format(
                        "message_player_esp_enable".i18n(),
                        value ? "text_enable".i18n() : "text_disable".i18n()
                    ),
                    value ? ENotificationIconType.Default : ENotificationIconType.Alert,
                    GlobalCfg.MuteNotice.Value
                );
            }
        }
    }
}
