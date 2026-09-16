using EFT;
using EFT.HealthSystem;
using Oracle.Utils;
using UnityEngine;

namespace Oracle.Data
{
    /// <summary>
    /// 玩家数据总线
    ///
    /// IL2CPP 移植要点（均已对 5.0 interop 实测确认）：
    ///
    /// 1. PlayerBones 位于【全局命名空间】（不是 EFT.PlayerBones）。
    ///    骨骼类型不统一：Head/Spine3/Pelvis/LeftShoulder/RightShoulder/LeftThigh1/LeftThigh2/
    ///    RightThigh1/RightThigh2 是 BifacialTransform；Neck/LeftPalm/RightPalm 是 Transform；
    ///    Upperarms/Forearms 是 Il2CppReferenceArray&lt;Transform&gt;。因此保留了双重重载。
    ///
    /// 2. 生命值：Player.HealthController 返回【接口】IHealthController，而该接口
    ///    （4.1 与 5.0 皆然）并不声明 GetBodyPartHealth —— 它只在 BaseHealthController&lt;T&gt; 上。
    ///    5.0 的 Player 额外提供 ActiveHealthController 具体类型，分部位数据必须走它。
    ///    拿不到时回退到接口层的 HealthRate（归一化总血量），保证不崩。
    ///
    /// 3. 射线检测 Physics.Linecast(Vector3, Vector3, int) 在 5.0 中可用。
    /// </summary>
    public static class OraclePlayerDataManager
    {
        //FOV计算参数
        public const float BotFovThreshold = 0.5f;

        /// <summary>射线遮挡的层级掩码，通过对 LayerMask 位运算得到</summary>
        public static readonly int HighPolyWithTerrainMask =
            (1 << LayerMask.NameToLayer("Terrain")) |
            (1 << LayerMask.NameToLayer("HighPolyCollider"));

        /// <summary>用于汇总总血量的部位列表</summary>
        private static readonly EBodyPart[] AllHealthParts =
        {
            EBodyPart.Head,
            EBodyPart.Chest,
            EBodyPart.Stomach,
            EBodyPart.LeftArm,
            EBodyPart.RightArm,
            EBodyPart.LeftLeg,
            EBodyPart.RightLeg
        };

        /// <summary>
        /// 判断是否为 PMC（USEC / BEAR）。
        ///
        /// ⚠ 不能只看 Info.Side —— 这是 SPT5 下的一个真实坑：
        ///   bot 档案的基础模板 bots/base.json 把 Info.Side 硬编码为 "Savage"，
        ///   而 types/ 下的 pmcbear.json / pmcusec.json **并不覆盖该字段**
        ///   （全仓库 "Side" 仅出现于 base.json 一处）。
        ///   因此 PMC bot 在运行时 Info.Side 仍是 Savage，真正的身份标识在 Role 上：
        ///   WildSpawnType.pmcBEAR(51) / pmcUSEC(52)。
        ///   （佐证：bots/core.json 的 botRolesWithDogTags = ['pmcbear','pmcusec']）
        ///
        ///   只看 Side 的后果：所有 PMC 落入 Scav 分支，显示成 "Scav"；
        ///   而 Boss 因为本就在该分支内按 Role 名匹配，反而看起来正常。
        /// </summary>
        public static bool IsPmc(ProfileInfo info)
        {
            if (info == null) return false;

            EPlayerSide side = info.Side;
            if (side == EPlayerSide.Usec || side == EPlayerSide.Bear) return true;

            // 兜底：从 Role 推断
            var settings = info.Settings;
            if (settings != null)
            {
                WildSpawnType role = settings.Role;
                if (role == WildSpawnType.pmcBEAR || role == WildSpawnType.pmcUSEC) return true;
            }

            return false;
        }

        /// <summary>
        /// 取「有效阵营」—— 修正 PMC bot 的 Side 被基础模板固定为 Savage 的问题。
        ///
        /// 返回值的用途是【显示与配色】，因此必须反映玩家真实阵营，
        /// 否则 PMC 会显示成 "Savage" 并使用 Scav 的配色。
        /// </summary>
        public static EPlayerSide GetEffectiveSide(ProfileInfo info)
        {
            if (info == null) return EPlayerSide.Savage;

            EPlayerSide side = info.Side;
            if (side == EPlayerSide.Usec || side == EPlayerSide.Bear) return side;

            // Side 不可信时用 Role 反推
            var settings = info.Settings;
            if (settings != null)
            {
                WildSpawnType role = settings.Role;
                if (role == WildSpawnType.pmcUSEC) return EPlayerSide.Usec;
                if (role == WildSpawnType.pmcBEAR) return EPlayerSide.Bear;
            }

            return side;
        }

        /// <summary>
        /// 取玩家名字
        /// </summary>
        public static string GetPlayerName(ProfileInfo info)
        {
            if (info == null) return "Nikita Buyanov";

            var role = info.Settings?.Role.ToString().ToLower() ?? "assault";

            //迷宫小弟、玩家和全英文名不经过转换，反向筛选西里尔字母
            if (IsPmc(info) || role == "tagillahelperagro" || OracleCommon.IsAllEnglish(info.Nickname))
                return info.Nickname;

            return DebugGroupStruct.ConvertToLatinic(info.Nickname);
        }

        /// <summary>
        /// 敌我识别
        /// </summary>
        public static bool IsTeammate(ProfileInfo info)
        {
            if (info == null) return false;
            string targetGroupId = info.GroupId ?? "";
            string myGroupId = OracleGameState.LocalGroupId;
            return !string.IsNullOrEmpty(myGroupId) && targetGroupId == myGroupId;
        }

        /// <summary>
        /// 组合玩家数据
        /// </summary>
        public static EntityDisplayInfo GetEntityInfo(Player player, bool isTeammate, bool includeName = true)
        {
            string name = "Nikita Buyanov";
            string sideText = "Unheard";
            string level = "";

            int distance = GetDistanceToLocal(player);

            var info = player.Profile?.Info;
            if (info != null)
            {
                name = GetPlayerName(info);
                DeterminePlayerText(info, name, isTeammate, includeName, out sideText, out level);
            }

            return new EntityDisplayInfo(name, sideText, level, distance);
        }

        /// <summary>本地玩家到目标玩家的距离（米，四舍五入）</summary>
        public static int GetDistanceToLocal(Player player)
        {
            Player me = OracleGameState.LocalPlayer;
            if (me == null || player == null) return 0;
            return Mathf.RoundToInt(Vector3.Distance(me.Transform.position, player.Transform.position));
        }

        /// <summary>
        /// 获取玩家数据富文本
        /// </summary>
        public static void DeterminePlayerText(ProfileInfo info, string name, bool isTeammate, bool includeName,
            out string sideText, out string levelText)
        {
            sideText = "Unheard";
            levelText = "";

            if (info == null) return;

            // 用修正后的有效阵营判断，而不是原始 Side —— PMC bot 的 Side 被
            // bots/base.json 固定为 Savage，只有 Role 能分辨（见 IsPmc / GetEffectiveSide）
            EPlayerSide effectiveSide = GetEffectiveSide(info);

            if (effectiveSide == EPlayerSide.Savage)
            {
                var role = info.Settings?.Role.ToString().ToLower() ?? "assault";

                string roleLabel = "text_esp_player_tag_scav".i18n();
                string colorHex = OracleColorManager.Scav;

                // 角色优先级判定
                if (role.Contains("boss") || IsSpecialBoss(role)) { roleLabel = "text_esp_player_tag_boss".i18n(); colorHex = OracleColorManager.Boss; }
                else if (role == "bossboarsniper" || role == "marksman") { roleLabel = "text_esp_player_tag_sniper".i18n(); colorHex = OracleColorManager.Sniper; }
                else if (role == "pmcbot") { roleLabel = "text_esp_player_tag_raider".i18n(); colorHex = OracleColorManager.Raider; }
                else if (role == "exusec") { roleLabel = "text_esp_player_tag_rogue".i18n(); colorHex = OracleColorManager.Raider; }
                else if (role.Contains("follower") || role == "tagillahelperagro") { roleLabel = "text_esp_player_tag_follower".i18n(); colorHex = OracleColorManager.Follower; }
                else if (role.Contains("sectant")) { roleLabel = "text_esp_player_tag_sectant".i18n(); colorHex = OracleColorManager.Sectant; }
                else if (role == "gifter") { roleLabel = "text_esp_player_tag_santa".i18n(); colorHex = OracleColorManager.Santa; }
                else if (role.Contains("btr")) { roleLabel = "text_esp_player_tag_btr".i18n(); colorHex = OracleColorManager.BTR; }
                else if (role.Contains("black")) { roleLabel = "text_esp_player_tag_bd".i18n(); colorHex = OracleColorManager.BlackDiv; }

                string displayString = includeName ? $"{roleLabel} {name}" : roleLabel;
                string finalRes = $"<color={colorHex}>{displayString}</color>";

                sideText = isTeammate
                    ? $"<color={OracleColorManager.AllyPlayer}>{"text_esp_player_tag_teammate".i18n()} </color>{finalRes}"
                    : finalRes;
            }
            else
            {
                levelText = string.Format("text_esp_player_level".i18n(), OracleColorManager.PlayerLevel, info.Level);

                // 显示与配色都用修正后的阵营：
                // 若直接用 info.Side，PMC bot 会显示成 "Savage" 且套用 Scav 配色
                bool isUsec = effectiveSide == EPlayerSide.Usec;
                string color = isUsec ? OracleColorManager.PMCUSEC : OracleColorManager.PMCBEAR;
                string sideLabel = isUsec ? "Usec" : "Bear";

                string displayContent = includeName ? $"{sideLabel} {name}" : sideLabel;
                string baseText = $"<color={color}>{displayContent}</color>";

                sideText = isTeammate
                    ? $"<color={OracleColorManager.AllyPlayer}>{"text_esp_player_tag_teammate".i18n()} </color>{baseText}"
                    : baseText;
            }
        }

        /// <summary>
        /// 取玩家标签的「纯文本 + 独立颜色」分段 —— **叠加层数据桥专用**。
        ///
        /// 与 DeterminePlayerText 的区别：那个直接拼好富文本给 OnGUI 用；
        /// 这里是每段文字配一个颜色，供 GDI 渲染线程按段绘制
        /// （渲染线程拿不到富文本解析，也不该做字符串拆分）。
        ///
        /// 角色优先级判定与 DeterminePlayerText 完全一致，只是输出形态不同。
        /// 阵营同样用 GetEffectiveSide 而非原始 Side：PMC bot 的 Side 被
        /// bots/base.json 固定为 Savage，只有 Role 能分辨（见 IsPmc）。
        /// </summary>
        public static void GetPlayerOverlayLabel(ProfileInfo info, string name, bool isTeammate, bool includeName,
            out string levelText, out OracleColor levelColor,
            out string teammateText, out OracleColor teammateColor,
            out string sideText, out OracleColor sideColor)
        {
            levelText = "";
            levelColor = OracleColorManager.PlayerLevel;
            teammateText = isTeammate ? "text_esp_player_tag_teammate".i18n() : "";
            teammateColor = OracleColorManager.AllyPlayer;
            sideText = name;
            sideColor = OracleColorManager.Scav;

            if (info == null) return;

            EPlayerSide effectiveSide = GetEffectiveSide(info);

            if (effectiveSide == EPlayerSide.Savage)
            {
                var role = info.Settings?.Role.ToString().ToLower() ?? "assault";

                string roleLabel = "text_esp_player_tag_scav".i18n();
                OracleColor color = OracleColorManager.Scav;

                if (role.Contains("boss") || IsSpecialBoss(role)) { roleLabel = "text_esp_player_tag_boss".i18n(); color = OracleColorManager.Boss; }
                else if (role == "bossboarsniper" || role == "marksman") { roleLabel = "text_esp_player_tag_sniper".i18n(); color = OracleColorManager.Sniper; }
                else if (role == "pmcbot") { roleLabel = "text_esp_player_tag_raider".i18n(); color = OracleColorManager.Raider; }
                else if (role == "exusec") { roleLabel = "text_esp_player_tag_rogue".i18n(); color = OracleColorManager.Raider; }
                else if (role.Contains("follower") || role == "tagillahelperagro") { roleLabel = "text_esp_player_tag_follower".i18n(); color = OracleColorManager.Follower; }
                else if (role.Contains("sectant")) { roleLabel = "text_esp_player_tag_sectant".i18n(); color = OracleColorManager.Sectant; }
                else if (role == "gifter") { roleLabel = "text_esp_player_tag_santa".i18n(); color = OracleColorManager.Santa; }
                else if (role.Contains("btr")) { roleLabel = "text_esp_player_tag_btr".i18n(); color = OracleColorManager.BTR; }
                else if (role.Contains("black")) { roleLabel = "text_esp_player_tag_bd".i18n(); color = OracleColorManager.BlackDiv; }

                sideText = includeName ? $"{roleLabel} {name}" : roleLabel;
                sideColor = color;
            }
            else
            {
                // ⚠ 用 overlay 变体的键（"{0}级"，单占位符）——
                //   ESP 那个 text_esp_player_level 是富文本（"<color={0}>{1}级</color>"，两占位符），
                //   颜色在这里由 sideColor 单独传，不需要内联进字符串。
                levelText = string.Format("text_esp_overlay_player_level".i18n(), info.Level);

                bool isUsec = effectiveSide == EPlayerSide.Usec;
                sideColor = isUsec ? OracleColorManager.PMCUSEC : OracleColorManager.PMCBEAR;

                string sideLabel = isUsec ? "Usec" : "Bear";
                sideText = includeName ? $"{sideLabel} {name}" : sideLabel;
            }
        }

        /// <summary>不含 boss 字符串的 boss 单位</summary>
        private static bool IsSpecialBoss(string role)
        {
            return role == "followerbirdeye" || role == "followerbigpipe" ||
                   role == "infectedtagilla" || role == "sectantoni" ||
                   role == "sectantpredvestnik" || role == "sectantprizark";
        }

        /// <summary>提取 Transform 的坐标</summary>
        public static Vector3? GetBonePos(Transform t)
        {
            if (t == null) return null;
            return t.position;
        }

        /// <summary>使用 .Original 安全提取 BifacialTransform 坐标</summary>
        public static Vector3? GetBonePos(BifacialTransform bt)
        {
            if (bt == null || bt.Original == null) return null;
            return bt.Original.position;
        }

        /// <summary>
        /// 判断目标是否对相机可见（三点射线检测，任一可见即返回 true）
        /// </summary>
        public static bool IsPlayerVisible(Vector3 camPosition, Player targetPlayer, int obstacleLayerMask)
        {
            if (targetPlayer == null || targetPlayer.PlayerBones == null) return false;

            var bones = targetPlayer.PlayerBones;
            Transform[] checkBones =
            {
                bones.Head?.Original,
                bones.Spine3?.Original,
                bones.Pelvis?.Original
            };

            foreach (Transform bone in checkBones)
            {
                if (bone == null) continue;
                if (!Physics.Linecast(camPosition, bone.position, obstacleLayerMask)) return true;
            }
            return false;
        }

        /// <summary>
        /// 判断 AI 是否能看到玩家（含 FOV 模拟，比上面更严谨）
        /// </summary>
        public static bool IsBotVisible(Player bot, Player localPlayer, int obstacleLayerMask)
        {
            if (bot == null || localPlayer == null || bot.PlayerBones == null) return false;
            if (localPlayer.PlayerBones == null) return false;

            Transform botEyePoint = bot.PlayerBones.Head?.Original;
            if (botEyePoint == null) return false;

            Transform localPlayerChest = localPlayer.PlayerBones.Spine3?.Original;
            if (localPlayerChest == null) return false;

            // FOV 模拟：点积 0.5 ≈ 120 度视野
            Vector3 targetDir = (localPlayerChest.position - botEyePoint.position).normalized;
            Vector3 botLookDir = bot.LookDirection;
            if (botLookDir == Vector3.zero)
            {
                botLookDir = botEyePoint.forward; // 极小概率的兜底
            }
            if (Vector3.Dot(botLookDir, targetDir) < BotFovThreshold) return false;

            Transform[] myCriticalParts =
            {
                localPlayer.PlayerBones.Head?.Original,
                localPlayerChest,
                localPlayer.PlayerBones.Pelvis?.Original
            };

            foreach (Transform myPart in myCriticalParts)
            {
                if (myPart == null) continue;
                if (!Physics.Linecast(botEyePoint.position, myPart.position, obstacleLayerMask)) return true;
            }
            return false;
        }

        /// <summary>
        /// 获取玩家总血量与上限。
        ///
        /// 优先走 ActiveHealthController（具体类型，能拿到分部位 Current/Maximum）；
        /// 拿不到时回退到接口层的 HealthRate（归一化值），此时以 1.0 作为上限，
        /// 调用方按比例使用即可，不会崩。
        /// </summary>
        public static void GetPlayerTotalHealth(Player player, out float currentHp, out float maxHp)
        {
            currentHp = 0f;
            maxHp = 0f;
            if (player == null) return;

            var ahc = player.ActiveHealthController;
            if (ahc != null)
            {
                foreach (EBodyPart part in AllHealthParts)
                {
                    var partHealth = ahc.GetBodyPartHealth(part, false);
                    currentHp += partHealth.Current;
                    maxHp += partHealth.Maximum;
                }
                return;
            }

            // 回退路径
            var hc = player.HealthController;
            if (hc == null) return;
            maxHp = 1f;
            currentHp = Mathf.Clamp01(hc.HealthRate);
        }

        /// <summary>
        /// 取单个部位的生命比例（0..1）。
        /// 返回 -1 表示数据不可用（调用方应保留基础色）。
        /// </summary>
        public static float GetBodyPartHealthPercent(Player player, EBodyPart part)
        {
            if (player == null) return -1f;

            var ahc = player.ActiveHealthController;
            if (ahc != null)
            {
                var h = ahc.GetBodyPartHealth(part, false);
                if (h.Maximum <= 0f) return -1f;
                return Mathf.Clamp01(h.Current / h.Maximum);
            }

            // 回退：接口层只能判断部位是否被打废
            var hc = player.HealthController;
            if (hc == null) return -1f;
            return hc.IsBodyPartDestroyed(part) ? 0f : -1f;
        }
    }
}
