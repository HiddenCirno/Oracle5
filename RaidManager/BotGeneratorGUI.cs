using Comfort.Common;
using EFT;
using Oracle.Data;
using Oracle.Utils;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AI;

namespace Oracle.RaidManager
{
    /// <summary>
    /// Bot 生成器面板（创世引擎的页签之一）。
    ///
    /// 功能：选一个 WildSpawnType 角色，指定数量，在**准星指向的位置**生成。
    ///
    /// ══════════════ 与 4.1 的结构性差异 ══════════════
    ///
    /// 4.1 最后一步调的是 botSpawner.method_10(zone, data, null, CancellationToken.None) ——
    /// `method_10` 是**反编译产物名**（原始的混淆名已丢失），5.0 下必然不存在同名方法。
    /// 定位办法：用**参数签名反查** —— interop 的存根字段名里编码了完整签名，
    /// 在 Assembly-CSharp.dll 里搜 "Void_BotZone_BotCreationData_Action_1_BotOwner_CancellationToken"
    /// 即可。5.0 的对应方法是：
    ///     ActivateBotFromPool(BotZone, BotCreationData, Action&lt;BotOwner&gt;,
    ///                         CancellationToken, BotSpawnBossAndGroupParams)
    /// 前四个形参与 4.1 完全一致，多出的第五个参数是 5.0 新引入的类型（4.1 时代该信息
    /// 藏在 data.SpawnParams.ShallBeGroup 里）。
    ///
    /// ⚠ 第五个参数 BotSpawnBossAndGroupParams 是**值类型（struct）**。
    ///   绝不能传 default(...) —— interop 对值类型参数走 Il2CppObjectBaseToPtrNotNull，
    ///   而 default 的指针是 IntPtr.Zero，会直接抛 NullReferenceException 崩在封送层
    ///   （本工程踩过的坑，见 LoadBundlesAndCreatePools 的 CancellationToken）。
    ///   必须用真实构造：new BotSpawnBossAndGroupParams(false, false)。
    ///
    ///   两个字段都传 false 是刻意的：4.1 特意保持 ShallBeGroup 为空，
    ///   因为一旦为 true，BotCreatorClient.TryChangeRoleToAssaultGroup() 会把
    ///   Boss 角色改写成普通 assault —— 生成器就不再是玩家选的那个角色了。
    /// </summary>
    public class BotGeneratorGUI
    {
        public Vector2 _scrollPos;

        /// <summary>生成数量（原始输入串，需自行解析）</summary>
        private string _spawnAmountStr = "1";

        private WildSpawnType _selectedRole = WildSpawnType.assault;
        private List<WildSpawnType> _allAvailableRoles;
        private Vector2 _rolesScrollPos;

        /// <summary>生成中（防止重复点击）</summary>
        private bool _isSpawning = false;

        /// <summary>角色表只构建一次</summary>
        private void EnsureRolesLoaded()
        {
            if (_allAvailableRoles != null) return;

            _allAvailableRoles = new List<WildSpawnType>();
            foreach (WildSpawnType role in Enum.GetValues(typeof(WildSpawnType)))
            {
                _allAvailableRoles.Add(role);
            }
        }

        public void DrawPanel()
        {
            EnsureRolesLoaded();

            GUILayout.Space(10);
            _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            // ── 数量 ──
            GUILayout.BeginVertical(UIStyleManager.BoxStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label("text_bot_generator_generate_count".i18n(), GUILayout.Width(110));
            _spawnAmountStr = GUILayout.TextField(_spawnAmountStr, UIStyleManager.TextFieldStyle, GUILayout.Width(80));
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.Space(10);

            // ⚠ 文案在进分组前算好（string.Format 抛异常会破坏 IMGUI 布局栈）
            string typeHeader = "";
            string spawnBtnText = "";
            try
            {
                typeHeader = string.Format("text_bot_generator_generate_type".i18n(), _allAvailableRoles.Count);
                spawnBtnText = _isSpawning
                    ? "text_button_bot_generator_generating".i18n()
                    : string.Format("text_button_bot_generator_generate".i18n(), _selectedRole.ToString().i18n());
            }
            catch (Exception ex)
            {
                OracleLog.Throttled("botgen_text", $"[Oracle] Bot 生成器文案格式化失败: {ex.Message}");
            }

            GUILayout.Label(typeHeader);

            // ── 角色列表（内嵌滚动区）──
            GUIStyle origScroll = GUI.skin.verticalScrollbar;
            GUIStyle origThumb = GUI.skin.verticalScrollbarThumb;
            GUI.skin.verticalScrollbar = UIStyleManager.ScrollbarStyle;
            GUI.skin.verticalScrollbarThumb = UIStyleManager.ScrollbarThumbStyle;

            _rolesScrollPos = GUILayout.BeginScrollView(_rolesScrollPos, UIStyleManager.BoxStyle);

            for (int i = 0; i < _allAvailableRoles.Count; i++)
            {
                WildSpawnType role = _allAvailableRoles[i];
                bool isSelected = (_selectedRole == role);

                GUIStyle btnStyle = isSelected
                    ? UIStyleManager.BlueButtonStyle
                    : (UIStyleManager.NormalButtonStyle ?? GUI.skin.button);

                string roleText;
                try { roleText = role.ToString().i18n(); }
                catch { roleText = role.ToString(); }

                if (GUILayout.Button(roleText, btnStyle, GUILayout.Height(35), GUILayout.ExpandWidth(true)))
                {
                    _selectedRole = role;
                }

                GUILayout.Space(4);
            }

            GUILayout.EndScrollView();

            GUI.skin.verticalScrollbar = origScroll;
            GUI.skin.verticalScrollbarThumb = origThumb;

            GUILayout.Space(15);

            // ── 生成按钮 ──
            GUI.enabled = !_isSpawning;
            if (GUILayout.Button(spawnBtnText, UIStyleManager.BlueButtonStyle, GUILayout.Height(40)))
            {
                SpawnBotTask();
            }
            GUI.enabled = true;

            GUILayout.EndScrollView();
        }

        /// <summary>
        /// 生成入口（async void，必须自己兜住异常 —— 否则异常会被吞掉且无法排查）
        /// </summary>
        private async void SpawnBotTask()
        {
            if (_isSpawning) return;

            int amount;
            if (!int.TryParse(_spawnAmountStr, out amount)) amount = 1;
            amount = Mathf.Clamp(amount, 1, 20);

            try
            {
                _isSpawning = true;

                var gameWorld = OracleGameState.CurrentGameWorld;
                var mainPlayer = OracleGameState.LocalPlayer;
                var botGame = Singleton<IBotGame>.Instance;

                if (botGame?.BotsController == null || mainPlayer == null)
                {
                    OracleLog.Warning("[Oracle] Bot 生成失败：刷怪器未就绪或玩家不存在");
                    return;
                }

                // 阵营判定：对齐游戏/SPT 原生约定 —— AI 档案请求一律用 Savage，不要按角色名猜 Bear/Usec。
                // 原因：服务端 BotController.TryGenerateSingleBot 会把 PMC 的 Info.Side 强制改写成 "Savage"，
                // 客户端再由 SPT 的 PmcBotSidePatch(挂在 BotCreationData.ChooseProfile 上的 Postfix)
                // 依据 Role 编号 51=pmcBEAR / 52=pmcUSEC 还原出真实阵营。
                // 若此处传 Bear/Usec，GetProfileDataParams.ChooseProfile 的
                // (Side + Role + BotDifficulty) 三重精确匹配将永远选不中档案，PMC 直接生成失败。
                const EPlayerSide side = EPlayerSide.Savage;

                int successCount = 0;
                for (int i = 0; i < amount; i++)
                {
                    if (await AdvancedBotSpawner.SpawnBotPerfectly(
                            botGame.BotsController, mainPlayer, _selectedRole, side))
                    {
                        successCount++;
                    }
                }

                OracleLog.Info($"[Oracle] Bot 生成完成：{successCount}/{amount} 名 {_selectedRole}");

                if (successCount == 0)
                {
                    OracleNotify.Warning("text_bot_generator_generate_failed".i18n());
                }
            }
            catch (Exception ex)
            {
                OracleCommon.ShowError(ex, "BotGeneratorGUI.SpawnBotTask");
                try { OracleNotify.Warning("text_bot_generator_generate_failed".i18n()); } catch { }
            }
            finally
            {
                _isSpawning = false;
            }
        }
    }

    /// <summary>
    /// 高级 Bot 生成引擎（视线焦点生成）。
    ///
    /// 完整走游戏原生管线，只是把落点换成"准星指向的位置"：
    ///   1. 摄像机射线求交 → 得到世界坐标（打空则取正前方 30 米）
    ///   2. NavMesh.SamplePosition 吸附到可行走表面（避免生成在墙里/虚空）
    ///   3. 找最近的 BotZone（AI 必须归属于某个 Zone 才能工作）
    ///   4. 从该 Zone 的生成点里偷一个合法的 CorePointId（唤醒 AI 大脑用）
    ///   5. BotCreationData.Create 走原生档案管线
    ///   6. AddPosition 塞入我们算好的坐标与节点 ID，再交给原生激活
    /// </summary>
    public static class AdvancedBotSpawner
    {
        public static async Task<bool> SpawnBotPerfectly(
            BotsController botsController, Player mainPlayer, WildSpawnType role, EPlayerSide side)
        {
            try
            {
                if (botsController == null || mainPlayer == null) return false;

                var botSpawner = botsController.BotSpawner;
                if (botSpawner == null) return false;

                Vector3 playerPos = mainPlayer.Transform.position;

                // ── 1. 取准星焦点坐标 ──
                // 默认降级方案：玩家朝向正前方 15 米
                Vector3 targetPos = playerPos + (mainPlayer.LookDirection * 15f);

                Camera cam = Camera.main;
                if (cam != null)
                {
                    if (Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit rayHit, 300f))
                    {
                        targetPos = rayHit.point;
                    }
                    else
                    {
                        // 对着天空看、没打中任何地形：取空中 30 米处，让后续的 NavMesh 吸附去处理
                        targetPos = cam.transform.position + cam.transform.forward * 30f;
                    }
                }

                // ── 2. 吸附到 NavMesh ──
                Vector3 safePos = targetPos;
                if (NavMesh.SamplePosition(targetPos, out NavMeshHit navHit, 30f, NavMesh.AllAreas))
                {
                    safePos = navHit.position;
                }
                else if (NavMesh.SamplePosition(playerPos, out NavMeshHit playerNavHit, 30f, NavMesh.AllAreas))
                {
                    // 准星指向的地方太离谱（地图边界外等），回退到玩家脚下
                    safePos = playerNavHit.position;
                }

                // ── 3. 找最近的 BotZone ──
                BotZone closestZone = botSpawner.GetClosestZone(safePos, out float _);
                if (closestZone == null)
                {
                    OracleLog.Warning("[Oracle] Bot 生成失败：准星所指区域附近找不到合法的 BotZone");
                    return false;
                }

                // ── 4. 偷一个合法的 CorePointId 唤醒 AI 大脑 ──
                int validCorePointId = 0;
                var markers = closestZone.SpawnPointMarkers;
                // ⚠ 用 Count 而不是 Length：存根字段名里写的是 Il2CppReferenceArray，
                //   但实际暴露的属性类型是 Il2CppSystem.Collections.Generic.List。
                //   以编译器的判定为准（4.1 用的同样是 .Count）。
                if (markers != null && markers.Count > 0)
                {
                    float minDist = float.MaxValue;
                    for (int i = 0; i < markers.Count; i++)
                    {
                        var marker = markers[i];
                        if (marker == null || marker.SpawnPoint == null) continue;

                        float d = Vector3.Distance(safePos, marker.Position);
                        if (d < minDist)
                        {
                            minDist = d;
                            validCorePointId = marker.SpawnPoint.CorePointId;
                        }
                    }
                }

                // ── 5. 构造 Profile 数据（走游戏原生管线）──
                //
                // ⚠ BotSpawnParams 必须真实传入。原生 ActivateBotsByWave 也是在 Create 之后
                //   补上 new BotSpawnParams()，传 null 会在 GetGroupAndSetEnemies 里被无保护解引用
                //   （那里读 SpawnParams.ShallBeGroup）而抛 NRE —— 且该异常发生在
                //   PreActivate 与 SwitchBotVisual 之前，结果是
                //   "Player 已生成（可受击可死亡），但没有模型、没有 BotAI"。
                //
                //   ShallBeGroup 保持 null 是刻意的：一旦设值会让分组判定为 true，
                //   进而触发角色被改写成 assault，与玩家选的角色不符。
                var spawnParams = new BotSpawnParams { TriggerType = SpawnTriggerType.none };
                var profileData = new GetProfileDataParams(side, role, BotDifficulty.normal, 0f, spawnParams);

                BotCreationData botCreationData = await BotCreationData.Create(
                    profileData,
                    botSpawner._botCreator,
                    1,
                    botSpawner);   // BotSpawner 实现 ITokenGetter

                if (botCreationData == null)
                {
                    OracleLog.Warning("[Oracle] Bot 生成失败：BotCreationData.Create 返回空");
                    return false;
                }

                // 把算好的坐标与偷来的合法节点 ID 一起塞进去
                botCreationData.AddPosition(safePos, validCorePointId);

                // ── 6. 原生激活 ──
                // callback 传 null（4.1 亦然）；第五参必须是**真实实例**，不能用 default（见类注释）
                var bossAndGroup = new BotSpawnBossAndGroupParams(false, false);

                botSpawner.ActivateBotFromPool(
                    closestZone,
                    botCreationData,
                    null,
                    Il2CppSystem.Threading.CancellationToken.None,
                    bossAndGroup);

                return true;
            }
            catch (Exception ex)
            {
                // 单个失败不应中断整批生成，所以这里只记录并返回 false
                OracleLog.Throttled("botgen_spawn",
                    $"[Oracle] Bot 生成失败：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
