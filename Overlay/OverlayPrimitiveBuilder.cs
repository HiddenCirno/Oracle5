using EFT;
using Oracle.Combat;
using Oracle.Data;
using Oracle.ESP;
using Oracle.Utils;
using System;
using UnityEngine;

namespace Oracle.Overlay
{
    /// <summary>
    /// 叠加层数据桥主线程预计算模块。
    /// 把 Loot/Corpse/Tripwire/Player/Aimbot 的三维数据投影为屏幕空间 2D 原语，
    /// 渲染线程只消费原语、不做任何投影/深度/排序计算。
    /// 坐标系统一为「左上原点、y 向下」的窗口像素坐标（与 GDI 一致）。
    /// 颜色统一打包为 ARGB 32 位。所有深度检查（z > 0.01）在投影时完成。
    /// </summary>
    public static class OverlayPrimitiveBuilder
    {
        private static Camera _cam;
        private static OverlayPrimitiveBlock _block;
        private static float _screenW;
        private static float _screenH;
        private static Vector2 _screenCenter;

        //Build 开关状态/构建结果日志节流（每 1 秒一条）
        private static long _lastBuildLogMs;

        /// <summary>
        /// 构建一帧原语（仅叠加层模式调用，主线程）
        /// </summary>
        public static void Build(Camera cam, OverlayPrimitiveBlock block)
        {
            _cam = cam;
            _block = block;
            _screenW = Screen.width;
            _screenH = Screen.height;
            _screenCenter = new Vector2(_screenW / 2f, _screenH / 2f);

            //每 1 秒打一次各 ESP 开关状态，确认构建路径是否被开关拦截
            long nowMs = Environment.TickCount;
            if (nowMs - _lastBuildLogMs >= 1000)
            {
                System.Console.WriteLine($"[Oracle][Overlay] Builder.Build: PlayerESP={PlayerESPCfg.EnablePlayerESP?.Value} 骨骼={PlayerESPCfg.EnablePlayerBoneESP?.Value} 信息={PlayerESPCfg.EnablePlayerInfoESP?.Value} 血条={PlayerESPCfg.EnablePlayerHealthBarESP?.Value} LootFov={LootESPCfg.EnableLootESPFov?.Value} 尸体={CorpseESPCfg.EnableCorpseESP?.Value} 绊雷={TripwireESPCfg.EnableTripwireESP?.Value}");
                _lastBuildLogMs = nowMs;
            }

            //绘制顺序与 OnGUI 的 OracleEvent.Draw() 一致：ESP → Aimbot
            BuildPlayerBones();
            BuildPlayerText();
            BuildPlayerHealthBars();
            BuildLootText();
            BuildLootFovCircle();
            BuildCorpseText();
            BuildTripwire();
            BuildAimbotFovCircle();
            BuildTargetLine();

            //每 1 秒打一次构建结果（确认原语是否有内容）
            if (nowMs - _lastBuildLogMs >= 1000)
            {
                System.Console.WriteLine($"[Oracle][Overlay] Builder.Build 完成: lines={block.LineCount} texts={block.TextCount} rects={block.RectCount}");
                _lastBuildLogMs = nowMs;
            }
        }

        // ═══════════════════ 玩家骨骼（y 翻转：与 PlayerESP 默认 GL 矩阵一致） ═══════════════════

        private static void BuildPlayerBones()
        {
            if (!PlayerESPCfg.EnablePlayerESP.Value || !PlayerESPCfg.EnablePlayerBoneESP.Value) return;
            if (OracleGameState.CurrentGameWorld?.AllAlivePlayersList == null) return;

            Vector3 myPos = OracleGameState.LocalPlayer.Transform.position;
            float maxDist = PlayerESPCfg.PlayerESPMaxDistance.Value;

            foreach (Player player in OracleGameState.CurrentGameWorld.AllAlivePlayersList)
            {
                if (player == null || player == OracleGameState.LocalPlayer || player.PlayerBones == null) continue;
                if (!OracleCommon.IsInRange((int)maxDist, myPos, player.Transform.position)) continue;

                //射线遮挡判定 → 基础色（与 DrawPlayerBone 一致）
                bool canPlayerSeeBot = OraclePlayerDataManager.IsPlayerVisible(_cam.transform.position, player, OraclePlayerDataManager.HighPolyWithTerrainMask);
                bool canBotSeePlayer = OraclePlayerDataManager.IsBotVisible(player, OracleGameState.LocalPlayer, OraclePlayerDataManager.HighPolyWithTerrainMask);
                Color finalColor = canBotSeePlayer ? OracleColorManager.EnemyDangerous
                    : canPlayerSeeBot ? OracleColorManager.EnemyWarning
                    : OracleColorManager.EnemySafe;

                var bones = player.PlayerBones;
                Vector3? head = OraclePlayerDataManager.GetBonePos(bones.Head);
                Vector3? neck = OraclePlayerDataManager.GetBonePos(bones.Neck);
                Vector3? spine3 = OraclePlayerDataManager.GetBonePos(bones.Spine3);
                Vector3? pelvis = OraclePlayerDataManager.GetBonePos(bones.Pelvis);
                Vector3? lShoulder = OraclePlayerDataManager.GetBonePos(bones.LeftShoulder);
                Vector3? rShoulder = OraclePlayerDataManager.GetBonePos(bones.RightShoulder);
                Vector3? lUpperarm = (bones.Upperarms != null && bones.Upperarms.Length > 0) ? OraclePlayerDataManager.GetBonePos(bones.Upperarms[0]) : null;
                Vector3? rUpperarm = (bones.Upperarms != null && bones.Upperarms.Length > 1) ? OraclePlayerDataManager.GetBonePos(bones.Upperarms[1]) : null;
                Vector3? lForearm = (bones.Forearms != null && bones.Forearms.Length > 0) ? OraclePlayerDataManager.GetBonePos(bones.Forearms[0]) : null;
                Vector3? rForearm = (bones.Forearms != null && bones.Forearms.Length > 1) ? OraclePlayerDataManager.GetBonePos(bones.Forearms[1]) : null;
                Vector3? lPalm = OraclePlayerDataManager.GetBonePos(bones.LeftPalm);
                Vector3? rPalm = OraclePlayerDataManager.GetBonePos(bones.RightPalm);
                Vector3? lThigh1 = OraclePlayerDataManager.GetBonePos(bones.LeftThigh1);
                Vector3? lKnee = OraclePlayerDataManager.GetBonePos(bones.LeftThigh2);
                Vector3? lCalf = null;
                Vector3? lFoot = null;
                if (bones.LeftThigh2?.Original?.childCount > 0)
                {
                    Transform calfT = bones.LeftThigh2.Original.GetChild(0);
                    lCalf = calfT.position;
                    if (calfT.childCount > 0) lFoot = calfT.GetChild(0).position;
                }
                Vector3? rThigh1 = OraclePlayerDataManager.GetBonePos(bones.RightThigh1);
                Vector3? rKnee = OraclePlayerDataManager.GetBonePos(bones.RightThigh2);
                Vector3? rCalf = null;
                Vector3? rFoot = null;
                if (bones.RightThigh2?.Original?.childCount > 0)
                {
                    Transform calfT = bones.RightThigh2.Original.GetChild(0);
                    rCalf = calfT.position;
                    if (calfT.childCount > 0) rFoot = calfT.GetChild(0).position;
                }

                //动态颜色叠加（与 DrawPlayerBone 一致，肢体血量渐变）
                AddBoneLine(player, EBodyPart.Head, finalColor, head, neck);
                AddBoneLine(player, EBodyPart.Chest, finalColor, neck, spine3);
                AddBoneLine(player, EBodyPart.Stomach, finalColor, spine3, pelvis);
                AddBoneLine(player, EBodyPart.LeftArm, finalColor, neck, lShoulder);
                AddBoneLine(player, EBodyPart.LeftArm, finalColor, lShoulder, lUpperarm);
                AddBoneLine(player, EBodyPart.LeftArm, finalColor, lUpperarm, lForearm);
                AddBoneLine(player, EBodyPart.LeftArm, finalColor, lForearm, lPalm);
                AddBoneLine(player, EBodyPart.RightArm, finalColor, neck, rShoulder);
                AddBoneLine(player, EBodyPart.RightArm, finalColor, rShoulder, rUpperarm);
                AddBoneLine(player, EBodyPart.RightArm, finalColor, rUpperarm, rForearm);
                AddBoneLine(player, EBodyPart.RightArm, finalColor, rForearm, rPalm);
                AddBoneLine(player, EBodyPart.LeftLeg, finalColor, pelvis, lThigh1);
                AddBoneLine(player, EBodyPart.LeftLeg, finalColor, lThigh1, lKnee);
                AddBoneLine(player, EBodyPart.LeftLeg, finalColor, lKnee, lCalf);
                AddBoneLine(player, EBodyPart.LeftLeg, finalColor, lCalf, lFoot);
                AddBoneLine(player, EBodyPart.RightLeg, finalColor, pelvis, rThigh1);
                AddBoneLine(player, EBodyPart.RightLeg, finalColor, rThigh1, rKnee);
                AddBoneLine(player, EBodyPart.RightLeg, finalColor, rKnee, rCalf);
                AddBoneLine(player, EBodyPart.RightLeg, finalColor, rCalf, rFoot);
            }
        }

        private static void AddBoneLine(Player player, EBodyPart part, Color baseColor, Vector3? p1, Vector3? p2)
        {
            if (!p1.HasValue || !p2.HasValue) return;
            Vector3 s1 = _cam.WorldToScreenPoint(p1.Value);
            Vector3 s2 = _cam.WorldToScreenPoint(p2.Value);
            //深度检查：防止贴脸骨骼满天飞
            if (s1.z <= 0.01f || s2.z <= 0.01f) return;
            _block.AddLine(new OverlayLine
            {
                X1 = s1.x,
                Y1 = _screenH - s1.y,
                X2 = s2.x,
                Y2 = _screenH - s2.y,
                Color = ColorToArgb(PlayerESP.GetDynamicLimbColor(player, part, baseColor)),
                Alpha = 255
            });
        }

        // ═══════════════════ 玩家文本（头顶悬浮，多色段） ═══════════════════

        private static void BuildPlayerText()
        {
            if (!PlayerESPCfg.EnablePlayerESP.Value || !PlayerESPCfg.EnablePlayerInfoESP.Value) return;
            if (OracleGameState.CurrentGameWorld?.AllAlivePlayersList == null) return;

            Vector3 myPos = OracleGameState.LocalPlayer.Transform.position;
            float maxDist = PlayerESPCfg.PlayerESPMaxDistance.Value;

            foreach (Player player in OracleGameState.CurrentGameWorld.AllAlivePlayersList)
            {
                if (player == null || player == OracleGameState.LocalPlayer || player.PlayerBones == null) continue;
                if (!OracleCommon.IsInRange((int)maxDist, myPos, player.Transform.position)) continue;

                bool isTeammate = OraclePlayerDataManager.IsTeammate(player.Profile?.Info);
                Vector3? headPos = OraclePlayerDataManager.GetBonePos(player.PlayerBones.Head);
                if (!headPos.HasValue) continue;

                //向头顶偏移防止与骨骼重叠
                Vector3 textScreenPos = _cam.WorldToScreenPoint(headPos.Value + new Vector3(0, 0.3f, 0));
                if (textScreenPos.z <= 0.01f) continue;

                //纯文本段 + 颜色（数据桥专用，不拆解 OnGUI 富文本）
                OraclePlayerDataManager.GetPlayerOverlayLabel(player.Profile?.Info,
                    OraclePlayerDataManager.GetPlayerName(player.Profile?.Info), isTeammate, true,
                    out string levelText, out OracleColor levelColor,
                    out string teammateText, out OracleColor teammateColor,
                    out string sideText, out OracleColor sideColor);

                int dist = Mathf.RoundToInt(Vector3.Distance(myPos, player.Transform.position));
                string distText = string.Format("text_esp_overlay_unit_distance".i18n(), dist);

                float screenX = textScreenPos.x;
                float screenY = _screenH - textScreenPos.y;

                _block.AddText(BuildPlayerLabelRect(screenX, screenY, levelText, levelColor, teammateText, teammateColor, sideText, sideColor, distText));

                //玩家身上的愿望单战利品（叠加层数据桥，仅过滤愿望单）
                BuildPlayerWishlist(player, screenX, screenY);
            }
        }

        /// <summary>
        /// 玩家身上的愿望单物品（数据桥原语，标签下方逐行堆叠）。
        /// 数据由 OracleWishlistDataManager 协程 2s 扫描装备栏填充，此处只做投影。
        /// </summary>
        private static void BuildPlayerWishlist(Player player, float labelScreenX, float labelScreenY)
        {
            if (!WishlistESPCfg.EnablePlayerWishlistESP.Value) return;
            if (OracleWishlistDataManager.CachedPlayerWishlist == null) return;
            if (!OracleWishlistDataManager.CachedPlayerWishlist.TryGetValue(player.ProfileId, out var list) || list == null || list.Count == 0) return;

            //从玩家标签下方逐行堆叠（与 OnGUI 一致：18px 行距）
            for (int i = 0; i < list.Count && i < 6; i++)
            {
                WishlistItemData item = list[i];
                _block.AddText(new OverlayText
                {
                    X = labelScreenX - 100,
                    Y = labelScreenY + 20 + i * 18,
                    W = 200,
                    H = 18,
                    SegmentCount = 1,
                    Seg0 = new OverlayTextSegment { Text = item.OverlayText, Color = item.Color.ToArgb() }
                });
            }
        }

        /// <summary>
        /// 按 OnGUI 富文本的显示顺序组装多色段：等级(绿) → 友军(蓝) → 阵营(角色色) → 距离(黄)。
        /// 空段跳过并压缩进连续槽位（渲染端按 SegmentCount 顺序遍历 Seg0..N，槽位必须无空洞）。
        /// </summary>
        private static OverlayText BuildPlayerLabelRect(float screenX, float screenY,
            string levelText, OracleColor levelColor,
            string teammateText, OracleColor teammateColor,
            string sideText, OracleColor sideColor, string distText)
        {
            var segs = new (string text, uint color)[4];
            int count = 0;

            //等级段（Scav 无等级，空串跳过）
            if (!string.IsNullOrEmpty(levelText)) segs[count++] = (levelText, levelColor.ToArgb());
            //友军段
            if (!string.IsNullOrEmpty(teammateText)) segs[count++] = (teammateText, teammateColor.ToArgb());
            //阵营段
            segs[count++] = (sideText, sideColor.ToArgb());
            //距离段（黄）
            segs[count++] = (distText, OracleColorManager.Distance.ToArgb());

            return PackText(screenX - 100, screenY - 20, 200, 40, segs, count);
        }

        /// <summary>
        /// 把段元组压缩进连续槽位并组装成 OverlayText
        /// </summary>
        private static OverlayText PackText(float x, float y, float w, float h, (string text, uint color)[] segs, int count)
        {
            OverlayText text = new OverlayText
            {
                X = x, Y = y, W = w, H = h,
                SegmentCount = (byte)count
            };
            if (count > 0) text.Seg0 = new OverlayTextSegment { Text = segs[0].text, Color = segs[0].color };
            if (count > 1) text.Seg1 = new OverlayTextSegment { Text = segs[1].text, Color = segs[1].color };
            if (count > 2) text.Seg2 = new OverlayTextSegment { Text = segs[2].text, Color = segs[2].color };
            if (count > 3) text.Seg3 = new OverlayTextSegment { Text = segs[3].text, Color = segs[3].color };
            return text;
        }

        // ═══════════════════ 玩家血条（脚底悬浮） ═══════════════════

        private static void BuildPlayerHealthBars()
        {
            if (!PlayerESPCfg.EnablePlayerESP.Value || !PlayerESPCfg.EnablePlayerHealthBarESP.Value) return;
            if (OracleGameState.CurrentGameWorld?.AllAlivePlayersList == null) return;

            Vector3 myPos = OracleGameState.LocalPlayer.Transform.position;
            float maxDist = PlayerESPCfg.PlayerESPMaxDistance.Value;

            foreach (Player player in OracleGameState.CurrentGameWorld.AllAlivePlayersList)
            {
                if (player == null || player == OracleGameState.LocalPlayer || player.HealthController == null) continue;
                if (!OracleCommon.IsInRange((int)maxDist, myPos, player.Transform.position)) continue;

                Vector3 feetScreenPos = _cam.WorldToScreenPoint(player.Transform.position);
                if (feetScreenPos.z <= 0.01f) continue;

                OraclePlayerDataManager.GetPlayerTotalHealth(player, out float curHp, out float maxHp);
                if (maxHp <= 0) continue;
                float hpPercent = Mathf.Clamp01(curHp / maxHp);

                float screenX = feetScreenPos.x;
                float screenY = _screenH - feetScreenPos.y;
                float barWidth = 60f, barHeight = 4f;
                float barX = screenX - barWidth / 2f;
                float barY = screenY + 5f;

                //底槽背景（暗灰）
                _block.AddRect(new OverlayRect
                {
                    X = barX, Y = barY, W = barWidth, H = barHeight,
                    Color = OracleColorManager.HealthBarBG.ToArgb()
                });
                //渐变填充（与 DrawPlayerHealthBar 完全一致的插值）
                Color hpColor;
                if (hpPercent > 0.5f)
                {
                    hpColor = Color.Lerp(OracleColorManager.HealthBarHalf, OracleColorManager.HealthBarFull, (hpPercent - 0.5f) * 2f);
                }
                else
                {
                    hpColor = Color.Lerp(OracleColorManager.HealthBarQuarter, OracleColorManager.HealthBarHalf, hpPercent * 2f);
                }
                _block.AddRect(new OverlayRect
                {
                    X = barX, Y = barY, W = barWidth * hpPercent, H = barHeight,
                    Color = ColorToArgb(hpColor)
                });
            }
        }

        // ═══════════════════ 战利品文本 + FOV 约束圈 ═══════════════════

        private static void BuildLootText()
        {
            if (OracleLootDataManager.CachedLootList == null || OracleLootDataManager.CachedLootList.Count == 0) return;

            float fovRadius = LootESPCfg.LootESPFovRange.Value;
            int fovMinPrice = LootESPCfg.LootESPFovMinPrice.Value;
            int fovMinLevel = OracleLootDataManager.GetLevelByPrice(fovMinPrice);

            foreach (LootData loot in OracleLootDataManager.CachedLootList)
            {
                Vector3 screenPos = _cam.WorldToScreenPoint(loot.Position);
                if (screenPos.z <= 0.01f) continue;

                float screenX = screenPos.x;
                float screenY = _screenH - screenPos.y + loot.YOffset;

                //FOV 约束过滤（与 DrawLootText 一致）
                if (LootESPCfg.EnableLootESPFov.Value)
                {
                    if (loot.Price < fovMinPrice && loot.ItemLevel < fovMinLevel)
                    {
                        float dx = screenX - _screenCenter.x;
                        float dy = screenY - _screenCenter.y;
                        if ((dx * dx + dy * dy) > fovRadius * fovRadius) continue;
                    }
                }

                bool isContainer = loot.Container != null;
                if (isContainer && !LootESPCfg.EnableContainerLootESP.Value) continue;
                if (!isContainer && !LootESPCfg.EnableLooseLootESP.Value) continue;

                _block.AddText(new OverlayText
                {
                    X = screenX - 100, Y = screenY - 20, W = 200, H = 40,
                    SegmentCount = 2,
                    Seg0 = new OverlayTextSegment { Text = loot.OverlayText, Color = loot.ItemColor.ToArgb() },
                    Seg1 = new OverlayTextSegment { Text = loot.OverlayDistanceText, Color = OracleColorManager.Distance.ToArgb() }
                });
            }
        }

        private static void BuildLootFovCircle()
        {
            if (!LootESPCfg.ShowLootESPFov.Value) return;
            AddCircle(_screenCenter, LootESPCfg.LootESPFovRange.Value, OracleColorManager.LootCircle, 255, 64);
        }

        // ═══════════════════ 尸体文本 ═══════════════════

        private static void BuildCorpseText()
        {
            if (!CorpseESPCfg.EnableCorpseESP.Value) return;
            if (OracleCorpseDataManager.CachedCorpseList == null || OracleCorpseDataManager.CachedCorpseList.Count == 0) return;

            foreach (CorpseData corpse in OracleCorpseDataManager.CachedCorpseList)
            {
                Vector3 screenPos = _cam.WorldToScreenPoint(corpse.Position);
                if (screenPos.z <= 0.01f) continue;

                float screenX = screenPos.x;
                float screenY = _screenH - screenPos.y - 10f;

                //段按显示顺序压缩进连续槽位：死尸标记(Corpse色) → 等级(绿) → 友军(蓝) → 阵营(角色色)
                var segs = new (string text, uint color)[4];
                int count = 0;
                segs[count++] = (corpse.OverlayTag, OracleColorManager.Corpse.ToArgb());
                if (!string.IsNullOrEmpty(corpse.OverlayLevelText)) segs[count++] = (corpse.OverlayLevelText, OracleColorManager.PlayerLevel.ToArgb());
                if (!string.IsNullOrEmpty(corpse.OverlayTeammateText)) segs[count++] = (corpse.OverlayTeammateText, OracleColorManager.AllyPlayer.ToArgb());
                segs[count++] = (corpse.OverlaySideText, corpse.OverlayColor.ToArgb());

                _block.AddText(PackText(screenX - 100, screenY - 20, 200, 40, segs, count));

                //尸体身上的愿望单战利品（数据桥原语，标签下方逐行堆叠）
                if (WishlistESPCfg.EnableCorpseWishlistESP.Value && corpse.WishlistItems != null)
                {
                    for (int i = 0; i < corpse.WishlistItems.Count && i < 6; i++)
                    {
                        WishlistItemData item = corpse.WishlistItems[i];
                        _block.AddText(new OverlayText
                        {
                            X = screenX - 100,
                            Y = screenY + 20 + i * 18,
                            W = 200,
                            H = 18,
                            SegmentCount = 1,
                            Seg0 = new OverlayTextSegment { Text = item.OverlayText, Color = item.Color.ToArgb() }
                        });
                    }
                }
            }
        }

        // ═══════════════════ 绊雷（线不翻转 y：LoadPixelMatrix 语义；文本翻转 y） ═══════════════════

        private static void BuildTripwire()
        {
            if (!TripwireESPCfg.EnableTripwireESP.Value) return;
            if (OracleTripwireManager.CachedTripwires == null || OracleTripwireManager.CachedTripwires.Count == 0) return;
            if (OracleGameState.LocalPlayer == null) return;

            Vector3 playerPos = OracleGameState.LocalPlayer.Transform.position;
            const int maxDistance = 25;

            foreach (TripwireData trap in OracleTripwireManager.CachedTripwires)
            {
                if (!OracleCommon.IsInRange(maxDistance, playerPos, trap.CenterPos)) continue;

                //实体线（⚠ GDI 是左上原点、y 向下：WorldToScreenPoint 是左下原点，必须翻转 y）
                Vector3 sA = _cam.WorldToScreenPoint(trap.StartPos);
                Vector3 sB = _cam.WorldToScreenPoint(trap.EndPos);
                if (sA.z > 0.01f && sB.z > 0.01f)
                {
                    _block.AddLine(new OverlayLine
                    {
                        X1 = sA.x, Y1 = _screenH - sA.y,
                        X2 = sB.x, Y2 = _screenH - sB.y,
                        Color = OracleColorManager.Tripwire.ToArgb(),
                        Alpha = 255
                    });
                }

                //距离标签（y 翻转）
                Vector3 screenCenter = _cam.WorldToScreenPoint(trap.CenterPos);
                if (screenCenter.z <= 0.01f) continue;

                int dist = Mathf.RoundToInt(Vector3.Distance(playerPos, trap.CenterPos));
                string distText = string.Format("text_esp_overlay_unit_distance".i18n(), dist);

                _block.AddText(new OverlayText
                {
                    X = screenCenter.x - 50, Y = _screenH - screenCenter.y - 20, W = 100, H = 40,
                    SegmentCount = 2,
                    Seg0 = new OverlayTextSegment { Text = trap.OverlayLabel, Color = OracleColorManager.Tripwire.ToArgb() },
                    Seg1 = new OverlayTextSegment { Text = distText, Color = OracleColorManager.Distance.ToArgb() }
                });
            }
        }

        // ═══════════════════ 自瞄 FOV 圈（半透明红）+ 目标锁定线 ═══════════════════

        private static void BuildAimbotFovCircle()
        {
            if (!AimbotCfg.EnableAimbot.Value || !AimbotCfg.DrawAimbotFov.Value) return;
            //与 Aimbot.DrawAimbotFOVCircle 一致：rgba(1,0,0,0.3) → Alpha = 76
            AddCircle(_screenCenter, AimbotCfg.AimbotFovRadius.Value, ColorToArgb(new Color(1f, 0f, 0f, 1f)), 76, 64);
        }

        private static void BuildTargetLine()
        {
            if (!AimbotCfg.EnableAimbot.Value || !AimbotCfg.DrawTargetLine.Value) return;
            if (Aimbot.LockedTarget == null || Aimbot.LockedTarget.PlayerBones == null) return;

            Vector3? headPos = AimbotCfg.AimbotPartSetting.Value == EAimingPart.Head
                ? OraclePlayerDataManager.GetBonePos(Aimbot.LockedTarget.PlayerBones.Head)
                : OraclePlayerDataManager.GetBonePos(Aimbot.LockedTarget.PlayerBones.Spine3);
            if (!headPos.HasValue) return;

            Vector3 screenPos = _cam.WorldToScreenPoint(headPos.Value);
            if (screenPos.z <= 0.01f) return;

            //目标线（⚠ GDI 左上原点：终点 y 必须翻转；起点是屏幕中心，对称不受影响）
            _block.AddLine(new OverlayLine
            {
                X1 = _screenCenter.x, Y1 = _screenCenter.y,
                X2 = screenPos.x, Y2 = _screenH - screenPos.y,
                Color = OracleColorManager.AimbotCircle.ToArgb(),
                Alpha = 255
            });
        }

        // ═══════════════════ 工具方法 ═══════════════════

        /// <summary>屏幕空间圆（64 段折线），圆心对称不涉及 y 翻转</summary>
        private static void AddCircle(Vector2 center, float radius, OracleColor color, byte alpha, int segments)
        {
            AddCircle(center, radius, color.ToArgb(), alpha, segments);
        }

        private static void AddCircle(Vector2 center, float radius, uint color, byte alpha, int segments)
        {
            float angleStep = 2f * Mathf.PI / segments;
            for (int i = 0; i < segments; i++)
            {
                float a1 = i * angleStep;
                float a2 = (i + 1) * angleStep;
                _block.AddLine(new OverlayLine
                {
                    X1 = center.x + Mathf.Cos(a1) * radius,
                    Y1 = center.y + Mathf.Sin(a1) * radius,
                    X2 = center.x + Mathf.Cos(a2) * radius,
                    Y2 = center.y + Mathf.Sin(a2) * radius,
                    Color = color,
                    Alpha = alpha
                });
            }
        }

        /// <summary>Unity Color → ARGB 32 位（alpha 取整到 255 级）</summary>
        private static uint ColorToArgb(Color c)
        {
            Color32 cc = (Color32)c;
            return ((uint)cc.a << 24) | ((uint)cc.r << 16) | ((uint)cc.g << 8) | cc.b;
        }
    }
}
