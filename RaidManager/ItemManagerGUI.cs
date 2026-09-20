using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using Oracle.Data;
using Oracle.ItemSpawn;
using Oracle.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.RaidManager
{
    /// <summary>
    /// 物品实例管理器（奇迹之门 / 模块 4 的面板）。
    ///
    /// ══════════════ 与 4.1 的结构性差异 ══════════════
    ///
    /// 4.1 这个类**同时**是数据持有者（ActiveList / _workspaces / generatedItem /
    /// SpawnedInSession 全是它的静态字段）和视图。这导致造物逻辑
    /// （ItemSpawner / ItemCatcher / ItemSpawnStashPatch）必须反向依赖本 GUI 类。
    ///
    /// 5.0 中这部分状态已迁到 OracleItemWorkspace，本类退化为**纯视图**：
    ///   · 物品表   → OracleItemWorkspace.ActiveList
    ///   · 带勾开关 → OracleItemWorkspace.SpawnedInSession
    ///   · 视图选择 → OracleItemWorkspace.SelectedView
    ///
    /// 另外两处适配：
    ///   · 存档读写改走 OracleItemPresetIO（原因见该文件：泛型方法在 IL2CPP 下不可用）
    ///   · 光标管理改走 MouseManager.RegisterMenu（原先硬编码查询本类与 RaidManagerGUI，
    ///     会让两个模块互相纠缠）
    /// </summary>
    public class ItemManagerGUI : IOracleManagerGUI
    {
        // ==================== UI 状态 ====================
        public static bool _isMenuOpen = false;

        // 窗口高度 = 原内容高度 + 页脚高度（页脚是新增区块，不挤压原有内容）
        public Rect _windowRect = new Rect(20, 20, 800, 650 + SupportFooter.GetHeight());

        public Vector2 _scrollPos;
        public Vector2 _fileScrollPos = Vector2.zero;

        /// <summary>文件框里当前输入的名字</summary>
        private static string _inputFileName = "Default";

        /// <summary>磁盘上已有的预设名（不含扩展名）</summary>
        private static readonly List<string> _savedFiles = new List<string>();

        /// <summary>物品图标缓存（键为 TemplateId）</summary>
        private readonly Dictionary<string, Texture2D> _iconCache = new Dictionary<string, Texture2D>();

        // ==================== 生命周期 ====================

        public void SubscribeEvent()
        {
            OracleEvent.OnDrawManagerGUI += OnGUI;
            OracleEvent.OnUpdate += Update;

            // 把"面板是否打开"注册给光标管理器 —— 本类不再被别处硬编码引用
            MouseManager.RegisterMenu(() => _isMenuOpen);
        }

        public void Update()
        {
            if (ItemManagerGUICfg.ItemManagerKey == null) return;

            if (Input.GetKeyDown(ItemManagerGUICfg.ItemManagerKey.Value))
            {
                _isMenuOpen = !_isMenuOpen;
                MouseManager.ToggleCursor();

                if (_isMenuOpen)
                {
                    RefreshFileList();

                    // 预设已经删除, 回到内存表
                    if (!OracleItemWorkspace.IsSessionView && !_savedFiles.Contains(OracleItemWorkspace.SelectedView))
                    {
                        // 切表失焦
                        ItemCatcher.SavedItem = null;
                        OracleItemWorkspace.SwitchToSession();
                        _inputFileName = "Default";
                    }
                }
            }
        }

        // ==================== 绘制 ====================

        public void OnGUI()
        {
            if (!_isMenuOpen) return;

            UIStyleManager.EnsureInitialized();

            GUI.backgroundColor = OracleColorManager.ManagerGUIBackground;

            // ⚠ IL2CPP 陷阱：不能直接把方法组写进 Window 的第 3 参。
            //
            //   interop 生成的 UnityEngine.GUI.WindowFunction 继承自
            //   Il2CppSystem.MulticastDelegate，**不是** C# 原生委托类型，
            //   因此编译器无法做方法组转换（报 CS1503「无法从方法组转换」）。
            //
            //   好在该类型带一个 op_Implicit(System.Action<int>)，所以先落到
            //   托管 Action<int> 上，再由隐式转换运算符接手。
            //
            //   （本工程后续所有 GUI.Window 调用都要照此写法。）
            Action<int> drawWindow = DrawWindow;

            _windowRect = GUI.Window(
                8848,
                _windowRect,
                drawWindow,
                "text_item_instance_manager_title".i18n(),
                UIStyleManager.WindowStyle);
        }

        public void DrawWindow(int windowID)
        {
            // ================== 顶部全局状态栏 ==================
            //
            // 关于带勾开关：这里刻意用"按钮"而非 Toggle。
            // 原版注释里写得很清楚 —— Toggle 的勾选框与文本耦合，
            // 按下去只变透明、没有 hover 反馈，观感很差。
            // 按钮负责视觉、文本负责语义，两者分开。
            if (GUI.Button(
                    new Rect(_windowRect.width - 110, 4, 50, 20),
                    "text_button_item_instance_manager_fir".i18n(),
                    OracleItemWorkspace.SpawnedInSession ? UIStyleManager.BlueButtonStyle : UIStyleManager.RedButtonStyle))
            {
                OracleItemWorkspace.SpawnedInSession = !OracleItemWorkspace.SpawnedInSession;
            }

            // 关闭
            if (GUI.Button(
                    new Rect(_windowRect.width - 55, 4, 50, 20),
                    "text_button_manger_close".i18n(),
                    UIStyleManager.RedButtonStyle))
            {
                _isMenuOpen = false;
                MouseManager.ToggleCursor();
            }

            GUILayout.Space(15);

            // ================== 文件操作次顶栏 ==================
            GUILayout.BeginHorizontal(UIStyleManager.BoxStyle);

            GUILayout.Label("text_item_instance_manager_file_name".i18n(), GUILayout.Width(60));

            string rawInput = GUILayout.TextField(_inputFileName, UIStyleManager.TextFieldStyle, GUILayout.ExpandWidth(true));
            if (rawInput != _inputFileName)
            {
                _inputFileName = SanitizeFileName(rawInput);
            }

            // 清空当前列表
            if (GUILayout.Button("text_button_item_instance_manager_clear".i18n(), UIStyleManager.RedButtonStyle, GUILayout.Width(60)))
            {
                ItemCatcher.SavedItem = null;
                OracleItemWorkspace.ActiveList.Clear();
            }

            // 刷新文件
            if (GUILayout.Button("text_button_item_instance_manager_refresh".i18n(), UIStyleManager.BlueButtonStyle, GUILayout.Width(60)))
            {
                RefreshFileList();
            }

            // 读写
            if (GUILayout.Button("text_button_item_instance_manager_load_items".i18n(), UIStyleManager.BlueButtonStyle, GUILayout.Width(60)))
            {
                ItemCatcher.SavedItem = null;
                LoadPresetIntoCache(_inputFileName);
            }

            if (GUILayout.Button("text_button_item_instance_manager_save_items".i18n(), UIStyleManager.BlueButtonStyle, GUILayout.Width(60)))
            {
                SaveSavedItemsToFile(_inputFileName);
            }

            GUILayout.EndHorizontal();

            GUILayout.Space(5);

            // GUILayout 的滚动条只认 GUI.skin 上的样式，因此这里临时替换再还原
            GUIStyle origScroll = GUI.skin.verticalScrollbar;
            GUIStyle origThumb = GUI.skin.verticalScrollbarThumb;
            GUI.skin.verticalScrollbar = UIStyleManager.ScrollbarStyle;
            GUI.skin.verticalScrollbarThumb = UIStyleManager.ScrollbarThumbStyle;

            // ================== 左右分栏区域 ==================
            GUILayout.BeginHorizontal();

            // ---------- 左：文件列表 ----------
            GUIStyle origHScroll = GUI.skin.horizontalScrollbar;
            GUIStyle origHThumb = GUI.skin.horizontalScrollbarThumb;
            GUI.skin.horizontalScrollbar = UIStyleManager.HScrollbarStyle;
            GUI.skin.horizontalScrollbarThumb = UIStyleManager.HScrollbarThumbStyle;

            _fileScrollPos = GUILayout.BeginScrollView(_fileScrollPos, UIStyleManager.BoxStyle, GUILayout.Width(200));

            bool isCurrentView = OracleItemWorkspace.IsSessionView;
            GUILayout.BeginHorizontal(isCurrentView ? UIStyleManager.SelectedBoxStyle : UIStyleManager.BoxStyle);

            if (GUILayout.Button(
                    "text_item_instance_manager_current_session".i18n(),
                    isCurrentView ? UIStyleManager.BlueButtonStyle : UIStyleManager.NormalButtonStyle,
                    GUILayout.ExpandWidth(true)))
            {
                // 切表自动失焦
                ItemCatcher.SavedItem = null;
                OracleItemWorkspace.SwitchToSession();
                _inputFileName = "Default";
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(5);

            for (int i = 0; i < _savedFiles.Count; i++)
            {
                string fileName = _savedFiles[i];
                bool isThisFileSelected = (OracleItemWorkspace.SelectedView == fileName);

                GUILayout.BeginHorizontal(isThisFileSelected ? UIStyleManager.SelectedBoxStyle : UIStyleManager.BoxStyle);

                if (GUILayout.Button(
                        fileName,
                        isThisFileSelected ? UIStyleManager.BlueButtonStyle : UIStyleManager.NormalButtonStyle,
                        GUILayout.ExpandWidth(true)))
                {
                    ItemCatcher.SavedItem = null;
                    OracleItemWorkspace.SelectedView = fileName;
                    _inputFileName = fileName;

                    // 首次进入该文件时才从磁盘载入
                    if (!OracleItemWorkspace.HasPreset(fileName))
                    {
                        LoadPresetIntoCache(fileName);
                    }
                }

                // 删除表
                if (GUILayout.Button("X", UIStyleManager.RedButtonStyle, GUILayout.Width(25)))
                {
                    string pathToDelete = Path.Combine(GetSaveDirectory(), fileName + ".json");
                    if (File.Exists(pathToDelete))
                    {
                        File.Delete(pathToDelete);
                        RefreshFileList();

                        OracleItemWorkspace.RemovePreset(fileName);

                        if (isThisFileSelected)
                        {
                            ItemCatcher.SavedItem = null;
                            OracleItemWorkspace.SwitchToSession();
                            _inputFileName = "Default";
                        }
                    }
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();
            GUI.skin.horizontalScrollbar = origHScroll;
            GUI.skin.horizontalScrollbarThumb = origHThumb;

            // ---------- 右：物品实例列表 ----------
            _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            // ⚠ 取一次活引用即可：视图切换只发生在上面左栏，本帧内不会变
            List<Item> activeList = OracleItemWorkspace.ActiveList;

            if (activeList.Count == 0)
            {
                GUILayout.Label("text_item_instance_manager_no_result".i18n(), UIStyleManager.BoxStyle);
            }
            else
            {
                // 倒序：最新捕获的在最上面
                for (int i = activeList.Count - 1; i >= 0; i--)
                {
                    // 列表可能在本帧的按钮回调里被改动（删除），因此每轮都要复核下标
                    if (i >= activeList.Count) continue;

                    Item item = activeList[i];
                    if (item == null) continue;

                    bool isCurrent = ItemCatcher.SavedItem == item;

                    GUILayout.BeginHorizontal(isCurrent ? UIStyleManager.SelectedBoxStyle : UIStyleManager.BoxStyle);

                    // 物品图标
                    Texture2D icon = GetCachedIcon(item);
                    if (icon != null)
                    {
                        GUILayout.Label(icon, GUILayout.Width(64), GUILayout.Height(64));
                    }
                    else
                    {
                        GUILayout.Label("text_item_instance_manager_no_icon".i18n(), GUILayout.Width(64), GUILayout.Height(64));
                    }

                    // 信息栏
                    GUILayout.BeginVertical();
                    GUILayout.Label($"<b>{item.Name.Localized()}</b>");
                    GUILayout.Label(string.Format("text_item_instance_manager_item_info".i18n(), OracleColorManager.TextGray, item.TemplateId));

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(string.Format("text_item_instance_manager_item_stack".i18n(), OracleColorManager.TextGray), GUILayout.Width(45));
                    string currentStackStr = item.StackObjectsCount.ToString();
                    string newStackStr = GUILayout.TextField(currentStackStr, 7, UIStyleManager.TextFieldStyle, GUILayout.Width(60));
                    if (newStackStr != currentStackStr)
                    {
                        if (string.IsNullOrEmpty(newStackStr)) item.StackObjectsCount = 0;
                        else if (int.TryParse(newStackStr, out int parsedStack)) item.StackObjectsCount = parsedStack;
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.EndVertical();

                    // 按钮
                    GUILayout.BeginVertical();
                    GUILayout.BeginHorizontal();

                    // 生成和选择
                    if (GUILayout.Button("text_button_item_instance_manager_spawn".i18n(), UIStyleManager.BlueButtonStyle, GUILayout.Height(22), GUILayout.MinWidth(70)))
                    {
                        SpawnItem(item);
                    }

                    if (GUILayout.Button(
                            isCurrent ? "text_button_item_instance_manager_selected".i18n() : "text_button_item_instance_manager_select".i18n(),
                            isCurrent ? UIStyleManager.RedButtonStyle : UIStyleManager.BlueButtonStyle,
                            GUILayout.Height(22), GUILayout.MinWidth(70)))
                    {
                        ItemCatcher.SavedItem = isCurrent ? null : item;
                    }
                    GUILayout.EndHorizontal();

                    // 掉落和删除
                    GUILayout.BeginHorizontal();

                    if (GUILayout.Button("text_button_item_instance_manager_drop".i18n(), UIStyleManager.BlueButtonStyle, GUILayout.Height(22), GUILayout.MinWidth(70)))
                    {
                        DropItem(item);
                    }

                    if (GUILayout.Button("text_button_item_instance_manager_delete".i18n(), UIStyleManager.RedButtonStyle, GUILayout.Height(22), GUILayout.MinWidth(70)))
                    {
                        activeList.RemoveAt(i);
                        if (isCurrent) ItemCatcher.SavedItem = null;
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.EndVertical();
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndHorizontal();

            GUI.skin.verticalScrollbar = origScroll;
            GUI.skin.verticalScrollbarThumb = origThumb;

            // ================== 底部支持区（页脚） ==================
            SupportFooter.DrawLayout(_windowRect.width);

            GUI.DragWindow(new Rect(0, 0, _windowRect.width - 50, 25));
        }

        // ==================== 造物动作 ====================

        /// <summary>
        /// 生成一件物品实例。
        ///
        /// 两条路径（与 4.1 一致）：
        ///   · 战局内 —— 直接克隆并塞进背包（异步，涉及资源包加载）
        ///   · 战局外（藏身处/仓库）—— 克隆后走 SyncStashExtend 路由发往服务端
        /// </summary>
        private static void SpawnItem(Item item)
        {
            if (item == null) return;

            // 数量为 0 的物品生成出来会立刻消失，兜底成 1
            if (item.StackObjectsCount <= 0) item.StackObjectsCount = 1;

            // ⚠ 判据必须是 InRaid，**不能**只看 `LocalPlayer != null`。
            //   藏身处同样有 LocalPlayer，只看它会误走"塞进背包"的战局分支 ——
            //   物品被塞进藏身处那个玩家的背包里，看起来就是"仓库里生成没反应"。
            Player mainPlayer = OracleGameState.LocalPlayer;
            if (OracleGameState.InRaid && mainPlayer != null)
            {
                ItemSpawner.CloneAndSpawnItemIntoInventory(mainPlayer, item);
                return;
            }

            // 战局外：仓库路由。复用统一的克隆三步（含 ID 重分配与状态清洗）
            Item cloneItem = ItemSpawner.CloneFresh(item);
            if (cloneItem == null) return;

            ItemSpawner.CloneAndSpawnItemIntoStash(cloneItem);
        }

        /// <summary>在摄像机前方掉落一件该物品的副本（仅战局内可用）</summary>
        private static void DropItem(Item item)
        {
            if (item == null) return;

            if (item.StackObjectsCount <= 0) item.StackObjectsCount = 1;

            // 同上：藏身处不算战局，别在藏身处往地上丢东西
            Player mainPlayer = OracleGameState.LocalPlayer;
            if (OracleGameState.InRaid && mainPlayer != null)
            {
                ItemSpawner.CloneAndDropItem(mainPlayer, item);
            }
        }

        // ==================== 中间工具方法 ====================

        /// <summary>存档目录（不存在则创建）</summary>
        private static string GetSaveDirectory()
        {
            string dir = Path.Combine(OraclePaths.PluginDir, "itemsaves");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        /// <summary>刷新磁盘上的预设文件名列表</summary>
        private void RefreshFileList()
        {
            _savedFiles.Clear();

            try
            {
                string dir = GetSaveDirectory();
                foreach (string file in Directory.GetFiles(dir, "*.json"))
                {
                    _savedFiles.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
            catch (Exception ex)
            {
                OracleCommon.ShowError(ex);
            }
        }

        /// <summary>过滤掉文件名非法字符（保留玩家输入的中文名）</summary>
        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "";
            string invalidChars = new string(Path.GetInvalidFileNameChars());
            string regexSearch = string.Format("[{0}]", Regex.Escape(invalidChars));
            return Regex.Replace(fileName, regexSearch, "");
        }

        /// <summary>
        /// 从磁盘载入预设到工作区缓存，并把视图切过去。
        /// 文件不存在时建立一个空表（等价于"新建预设"）。
        /// </summary>
        private void LoadPresetIntoCache(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || fileName == OracleItemWorkspace.CurrentSessionId)
                return;

            string savePath = Path.Combine(GetSaveDirectory(), fileName + ".json");

            if (!File.Exists(savePath))
            {
                OracleItemWorkspace.SetPreset(fileName, new List<Item>());
                OracleItemWorkspace.SelectedView = fileName;
                return;
            }

            try
            {
                string json = File.ReadAllText(savePath, Encoding.UTF8);
                List<Item> loadedItems = OracleItemPresetIO.Deserialize(json);

                OracleItemWorkspace.SetPreset(fileName, loadedItems);
                OracleItemWorkspace.SelectedView = fileName;

                OracleLog.Info($"[Oracle] 已载入预设 {fileName}（{loadedItems.Count} 件）");
            }
            catch (Exception ex)
            {
                OracleCommon.ShowError(ex);
            }
        }

        /// <summary>
        /// 把当前视图的物品表写入磁盘。
        /// </summary>
        private void SaveSavedItemsToFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return;

            try
            {
                List<Item> itemsToSave = OracleItemWorkspace.ActiveList;
                if (itemsToSave == null) return;

                string json = OracleItemPresetIO.Serialize(itemsToSave);

                string savePath = Path.Combine(GetSaveDirectory(), fileName + ".json");
                File.WriteAllText(savePath, json, Encoding.UTF8);

                RefreshFileList();

                // 只有"另存为别的名字"时才自动跳过去；
                // 从内存表存盘不跳（保留 4.1 的取舍：内存表是常驻工作区）
                if (OracleItemWorkspace.SelectedView != fileName &&
                    !OracleItemWorkspace.IsSessionView)
                {
                    ItemCatcher.SavedItem = null;
                    LoadPresetIntoCache(fileName);
                    _inputFileName = fileName;
                }

                // 把"当前视图"一并记下来：存档写出的是【当前视图】的列表，
                // 若玩家停在某个空预设上按保存，就会写出 0 件 —— 日志里没有视图名时
                // 这种现象完全无法与"造物失败"区分。
                OracleLog.Info(
                    $"[Oracle] 已保存预设 {fileName}（来源视图 {OracleItemWorkspace.SelectedView}，{itemsToSave.Count} 件）");
            }
            catch (Exception ex)
            {
                OracleCommon.ShowError(ex);
            }
        }

        /// <summary>
        /// 获取物品图标（按 TemplateId 缓存）。
        ///
        /// ⚠ LoadItemIcon 每次调用都会走一遍图标生成/取用流程，开销不小，
        ///   因此必须缓存 —— 但**只缓存 Texture2D**，不缓存 ItemIcon 对象本身，
        ///   后者由游戏的图标系统托管，长期持有会拖住其回收。
        /// </summary>
        private Texture2D GetCachedIcon(Item item)
        {
            if (item == null) return null;

            // 缓存优先。伪 null 检测：被卸载的贴图同样视为需要重建
            if (_iconCache.TryGetValue(item.TemplateId, out Texture2D cached) && cached != null)
            {
                return cached;
            }

            try
            {
                // scaleFactor=1，forcedGeneration=false：优先取缓存好的图标
                var iconData = ItemViewFactory.LoadItemIcon(item, 1, false);
                if (iconData != null && iconData.Sprite != null && iconData.Sprite.texture != null)
                {
                    Texture2D tex = iconData.Sprite.texture;
                    _iconCache[item.TemplateId] = tex;
                    return tex;
                }
            }
            catch (Exception ex)
            {
                // 图标拿不到不应影响面板本身可用，降级为占位文本
                OracleLog.ErrorOnce($"item_icon_{item.TemplateId}", $"[Oracle] 物品图标加载失败 {item.TemplateId}: {ex.Message}");
            }

            return null;
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(4)]
    public class ItemManagerGUICfg : IOracleCfg
    {
        internal static ConfigEntry<KeyCode> ItemManagerKey { get; set; }

        private const string Section = "4. 奇迹之门 / Creation Module";

        public void Initialize(ConfigFile config)
        {
            ItemManagerKey = config.Bind(
                Section,
                "打开物品管理器",
                KeyCode.F10,
                new ConfigDescription(
                    "cfg_creation_module_item_open_manager_key_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_creation_module_item_open_manager_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 130
                    }
                )
            );
        }
    }
}
