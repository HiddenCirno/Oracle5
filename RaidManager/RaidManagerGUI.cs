using BepInEx.Configuration;
using Oracle.Data;
using Oracle.Utils;
using System;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.RaidManager
{
    /// <summary>
    /// 创世引擎 —— 战局综合控制台（模块 5）。
    ///
    /// ══════════════ 与 4.1 的结构关系 ══════════════
    ///
    /// 本类是**宿主窗口**：一个带页签的 IMGUI 窗口（F8 开关），
    /// 每个页签对应一个子面板，子面板自己负责内容与布局。
    ///
    /// 4.1 有 5 个页签：战利品 / AI / Bot 生成器 / 技能 / 晨昏线。
    /// 5.0 采用**增量移植**：页签数组只列出已完成的模块，
    /// 移植完一个就加一项（并同步 DrawWindow 里的分支）。
    ///
    /// 刻意不把未完成的页签摆出来当占位 —— 空页签会让玩家以为功能坏了。
    ///
    /// 复用既有基础设施（不重复造轮子）：
    ///   · 窗口/页签/按钮样式 → UIStyleManager
    ///   · 底部署名区         → SupportFooter
    ///   · 光标显示/隐藏      → MouseManager.RegisterMenu（避免各模块互相硬编码引用）
    ///   · 面板自动装配       → 实现 IOracleManagerGUI 即可，OracleEvent 会反射实例化并订阅
    /// </summary>
    public class RaidManagerGUI : IOracleManagerGUI
    {
        /// <summary>全局唯一主菜单开关</summary>
        public static bool _isMenuOpen = false;

        /// <summary>
        /// 窗口矩形。
        /// 高度 = 原内容高度 + 页脚高度 —— 页脚是独立区块，不挤压原有内容的布局空间。
        /// </summary>
        public static Rect _windowRect = new Rect(820, 20, 550, 650 + SupportFooter.GetHeight());

        private int _selectedTab = 0;

        /// <summary>
        /// 页签标题的本地化键。
        ///
        /// ⚠ 只列出**已经移植完成**的面板。
        ///   4.1 的其余四个（AI / Bot 生成器 / 技能 / 晨昏线）尚未移植，
        ///   每个都是上千行的独立功能，不能用一个空壳页签糊弄过去。
        /// </summary>
        private readonly string[] _tabKeys =
        {
            "text_tab_loot_manager_title",
            "text_tab_ai_manager_title",
            "text_tab_bot_generator_title",
            "text_tab_skill_manager_title",
            "text_tab_chrono_manager_title",
        };

        /// <summary>本地化后的页签标题缓存</summary>
        private string[] _tabs;

        // 实例化子面板（与 _tabKeys 顺序一一对应）
        private readonly LootManagerGUI _lootPanel = new LootManagerGUI();
        private readonly AIManagerGUI _aiPanel = new AIManagerGUI();
        private readonly BotGeneratorGUI _botGenPanel = new BotGeneratorGUI();
        private readonly SkillManagerGUI _skillPanel = new SkillManagerGUI();
        private readonly ChronoManagerGUI _chronoPanel = new ChronoManagerGUI();

        public void SubscribeEvent()
        {
            OracleEvent.OnDrawManagerGUI += OnGUI;
            OracleEvent.OnUpdate += Update;

            // 语言切换时刷新本地化缓存
            LocaleManager.CurrentLanguage.SettingChanged += (sender, args) => RefreshLocalizedCache();

            // 把"面板是否打开"注册给光标管理器 —— 本类不被别处硬编码引用
            MouseManager.RegisterMenu(() => _isMenuOpen);

            RefreshLocalizedCache();
        }

        /// <summary>刷新本地化缓存（初始化时与语言切换时各调一次）</summary>
        public void RefreshLocalizedCache()
        {
            if (_tabs == null || _tabs.Length != _tabKeys.Length)
            {
                _tabs = new string[_tabKeys.Length];
            }

            for (int i = 0; i < _tabKeys.Length; i++)
            {
                _tabs[i] = _tabKeys[i].i18n();
            }

            // 子面板自己的页签/文案缓存也要刷新（技能面板有内部子页签）
            _skillPanel.RefreshLocalizedCache();
            SupportFooter.RefreshLocalizedCache();
        }

        public void Update()
        {
            // ⚠ 晨昏线的每帧驱动必须放在"是否在战局内"的判定【之前】。
            //   它是故意的：出局时也需要跑一次 SyncWorld 把天气接管状态清干净，
            //   否则下一局开局会残留上一局的天气接管。
            Oracle.Chrono.ChronoManager.Update();

            if (RaidManagerGUICfg.RaidManagerKey == null) return;

            // ⚠ 必须校验"确实在战局内"。
            //
            //   这是从 4.1 抄漏的一处：
            //       if (PluginsCore.CorrectGameWorld == null || PluginsCore.CorrectPlayer == null) return;
            //   漏掉它的后果是主菜单里也能把控制台按出来 —— 而控制台的内容
            //   （战利品列表等）完全依赖战局数据，此时必然是一片空白，
            //   看起来就像功能坏了。
            //
            //   OracleGameState.InRaid 内部用的是 CurrentGameWorld / LocalPlayer 的
            //   Unity 重载判空，战局结束后会自动变回 false，无需销毁回调。
            if (!OracleGameState.InRaid)
            {
                // 战局结束（或尚未进入）时把窗口收掉：
                // 否则窗口会跨场景残留，而且光标会一直卡在解锁状态、视角也动不了。
                // ToggleCursor 是依据 AnyMenuOpen 重算的**幂等**操作，不是盲翻开关。
                if (_isMenuOpen)
                {
                    _isMenuOpen = false;
                    MouseManager.ToggleCursor();
                }
                return;
            }

            if (Input.GetKeyDown(RaidManagerGUICfg.RaidManagerKey.Value))
            {
                _isMenuOpen = !_isMenuOpen;
                MouseManager.ToggleCursor();
            }
        }

        public void OnGUI()
        {
            if (!_isMenuOpen) return;

            UIStyleManager.EnsureInitialized();
            GUI.backgroundColor = OracleColorManager.ManagerGUIBackground;

            // ⚠ IL2CPP 陷阱：GUI.Window 的第 3 参不能直接写方法组。
            //   interop 的 UnityEngine.GUI.WindowFunction 继承自 Il2CppSystem.MulticastDelegate，
            //   不是 C# 原生委托，方法组无法转换（CS1503）。
            //   该类型带 op_Implicit(System.Action<int>)，故先落到托管 Action<int> 再交给它。
            Action<int> drawWindow = DrawWindow;

            _windowRect = GUI.Window(
                8855,
                _windowRect,
                drawWindow,
                "text_raid_manager_title".i18n(),
                UIStyleManager.WindowStyle);
        }

        public void DrawWindow(int windowID)
        {
            // ── 关闭按钮 ──
            if (GUI.Button(
                    new Rect(_windowRect.width - 55, 4, 50, 20),
                    "text_button_manger_close".i18n(),
                    UIStyleManager.RedButtonStyle))
            {
                _isMenuOpen = false;
                MouseManager.ToggleCursor();
            }

            GUILayout.Space(10);

            // ── 页签（手工绘制，不能用 GUILayout.Toolbar）──
            _selectedTab = DrawTabBar(_selectedTab, _tabs);

            GUILayout.Space(10);

            // ── 滚动条样式替换（面板内容可滚动）──
            GUIStyle origScroll = GUI.skin.verticalScrollbar;
            GUIStyle origThumb = GUI.skin.verticalScrollbarThumb;
            GUI.skin.verticalScrollbar = UIStyleManager.ScrollbarStyle;
            GUI.skin.verticalScrollbarThumb = UIStyleManager.ScrollbarThumbStyle;

            // ── 分发到子面板 ──
            //
            // ⚠ 整体包 try/catch：DrawWindow 是由 Unity 的 GUI.Window 经
            //   il2cpp delegate trampoline 回调的，**不经过 OracleBehaviour.OnGUI 的
            //   异常出口**。子面板一旦抛异常，会一路穿透到 IL2CPP 原生侧
            //   （日志表现为 "Exception in IL2CPP-to-Managed trampoline"），
            //   而且每帧重来一次 —— 曾经就这样把日志刷到 1.3MB 且面板全黑。
            //   在这里兜住：至少保证"一个面板坏了，窗口和其余部分还在"。
            try
            {
                // 索引必须与 _tabKeys 严格对应；新增页签时两边一起改，否则会串页
                switch (_selectedTab)
                {
                    case 0: _lootPanel.DrawPanel(); break;
                    case 1: _aiPanel.DrawPanel(); break;
                    case 2: _botGenPanel.DrawPanel(); break;
                    case 3: _skillPanel.DrawPanel(); break;
                    case 4: _chronoPanel.DrawPanel(); break;
                    default: _selectedTab = 0; break;
                }
            }
            catch (Exception ex)
            {
                // 节流：绘制异常每帧都会复现，不节流会把日志撑爆
                OracleLog.Throttled("raidmanager_draw", $"[Oracle] 战局管理面板绘制异常: {ex}");
            }

            GUI.skin.verticalScrollbar = origScroll;
            GUI.skin.verticalScrollbarThumb = origThumb;

            // ── 底部署名区 ──
            SupportFooter.DrawLayout(_windowRect.width);

            // 拖拽区域避开右上角的关闭按钮
            GUI.DragWindow(new Rect(0, 0, _windowRect.width - 50, 25));
        }

        /// <summary>
        /// 手工绘制的页签栏。
        ///
        /// ══════════════ 为什么不用 GUILayout.Toolbar ══════════════
        ///
        /// 实机崩溃栈：
        ///     System.NotSupportedException: Method unstripping failed
        ///       at UnityEngine.GUIContent.Temp(Il2CppStringArray texts)
        ///       at UnityEngine.GUILayout.Toolbar(Int32, Il2CppStringArray, GUIStyle, ...)
        ///       at Oracle.RaidManager.RaidManagerGUI.DrawWindow(Int32)
        ///
        /// Toolbar 内部会调 GUIContent.Temp(Il2CppStringArray)，而该方法体在
        /// IL2CPP 构建中被**裁剪**掉了 —— interop 找不到原生入口，直接抛异常。
        /// 由于 Toolbar 位于 DrawWindow 开头，异常中断了整个绘制流程，
        /// 导致窗口打开后一行内容都画不出来（"死在绘制阶段"）。
        ///
        /// ★ 这是**本环境的已知缺陷**，同一根因已经废掉了：
        ///     GUI.SelectionGrid / GUI.DoButtonGrid
        ///     → 连带 BepInEx 自己的 ConfigurationManager 下拉框全部崩溃
        ///
        ///   规律：**凡是接收 string[] 的 IMGUI 便捷方法都不可用**，
        ///   必须改用逐个绘制的基础方法（Button / Label 的单字符串重载）。
        /// </summary>
        /// <summary>
        /// 供各子面板复用的页签栏绘制（技能面板的子页签也用它）。
        /// 刻意做成 internal static：任何一个面板都不要再直接调用 GUILayout.Toolbar。
        /// </summary>
        internal static int DrawTabBar(int selected, string[] tabs)
        {
            if (tabs == null || tabs.Length == 0) return selected;

            GUILayout.BeginHorizontal();

            for (int i = 0; i < tabs.Length; i++)
            {
                // 选中态用蓝、未选中用普通色 —— 与 4.1 的 Toolbar + TabStyle 观感一致
                GUIStyle style = (i == selected)
                    ? UIStyleManager.BlueButtonStyle
                    : UIStyleManager.NormalButtonStyle;

                if (GUILayout.Button(tabs[i], style, GUILayout.Height(30)))
                {
                    selected = i;
                }
            }

            GUILayout.EndHorizontal();

            return selected;
        }
    }

    /// <summary>配置项定义</summary>
    public class RaidManagerGUICfg : IOracleCfg
    {
        internal static ConfigEntry<KeyCode> RaidManagerKey { get; set; }

        public void Initialize(ConfigFile config)
        {
            RaidManagerKey = config.Bind(
                "5. 创世引擎 / Raid Manage Module",
                "打开战局综合控制台",
                KeyCode.F8,
                new ConfigDescription(
                    "cfg_raid_manage_module_open_manager_key_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_raid_manage_module_open_manager_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 120
                    }
                )
            );
        }
    }
}
