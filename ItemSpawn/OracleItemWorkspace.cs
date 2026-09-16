using EFT.InventoryLogic;
using System.Collections.Generic;

namespace Oracle.ItemSpawn
{
    /// <summary>
    /// 造物工作区 —— 奇迹之门（模块 4）与创世引擎（模块 5）的**共享状态**。
    ///
    /// ══════════════ 为什么单独抽出这个类 ══════════════
    ///
    /// 4.1 里这些状态是挂在 ItemManagerGUI（属于创世引擎的 GUI）上的静态字段，
    /// 导致 ItemSpawner / ItemCatcher / ItemSpawnStashPatch 三个"造物"文件
    /// 全部反向依赖 GUI 类 —— 编译顺序、初始化时机、GUI 未加载时的行为都受影响。
    ///
    /// IL2CPP 下这种耦合代价更高（GUI 涉及类型注入与绘制，未打开时也不该被牵扯），
    /// 因此这里把共享状态独立出来：
    ///   · 奇迹之门（造物逻辑）只依赖本类，不依赖任何 GUI
    ///   · 创世引擎的 ItemManagerGUI 之后作为**消费方**接入本类
    ///
    /// 两者的职责边界因此变得清晰：本类是数据，GUI 只是它的一个视图。
    /// </summary>
    public static class OracleItemWorkspace
    {
        /// <summary>
        /// 内存工作区的保留标识。
        ///
        /// ⚠ 取值必须不可能与文件名冲突 —— 文件名已被 SanitizeFileName 过滤掉冒号，
        ///   所以这个带双冒号的串永远是安全的哨兵值。
        /// </summary>
        public const string CurrentSessionId = "::CURRENT_SESSION::";

        /// <summary>
        /// 造物是否标记为"战局中获得"（带勾 / FiR）。
        /// GUI 上的开关按钮会翻转它，造物时读取。
        /// </summary>
        public static bool SpawnedInSession = true;

        /// <summary>
        /// 当前选中的视图（<see cref="CurrentSessionId"/> 或某个预设名）。
        /// </summary>
        public static string SelectedView = CurrentSessionId;

        /// <summary>
        /// 内存工作区（未保存的、本次会话捕获与生成的物品）。
        ///
        /// ⚠ 注意：捕获 / 生成的物品落到 <see cref="ActiveList"/>（即**当前选中视图**），
        ///   而不是固定落到这里 —— 这是刻意保留的 4.1 语义：
        ///   玩家选中某个预设时捕获物品，就是在往那个预设里收集，之后按"保存"落盘。
        /// </summary>
        public static List<Item> SessionItems { get; } = new List<Item>();

        /// <summary>已载入/已保存的具名预设（视图名 → 物品表）</summary>
        private static readonly Dictionary<string, List<Item>> _presets =
            new Dictionary<string, List<Item>>();

        /// <summary>
        /// 当前视图对应的物品表（活引用）。
        ///
        /// ⚠ 返回的是**列表本身**而非副本：调用方会直接 Clear / RemoveAt / Add。
        ///   这是刻意保留的 4.1 语义（GUI 与造物逻辑共用同一份数据）。
        /// </summary>
        public static List<Item> ActiveList
        {
            get
            {
                if (IsSessionView) return SessionItems;

                if (!_presets.TryGetValue(SelectedView, out List<Item> list))
                {
                    list = new List<Item>();
                    _presets[SelectedView] = list;
                }
                return list;
            }
        }

        /// <summary>当前是否停在内存工作区视图</summary>
        public static bool IsSessionView => SelectedView == CurrentSessionId;

        /// <summary>切回内存工作区</summary>
        public static void SwitchToSession()
        {
            SelectedView = CurrentSessionId;
        }

        /// <summary>该名字是否已有载入的预设</summary>
        public static bool HasPreset(string name)
        {
            return !string.IsNullOrEmpty(name) && _presets.ContainsKey(name);
        }

        /// <summary>
        /// 覆盖写入一个预设视图（载入文件、或保存后回填缓存）。
        /// <paramref name="items"/> 为 null 时写入空表。
        /// </summary>
        public static void SetPreset(string name, List<Item> items)
        {
            if (string.IsNullOrEmpty(name) || name == CurrentSessionId) return;
            _presets[name] = items ?? new List<Item>();
        }

        /// <summary>取出预设（不存在返回 null），用于从磁盘载入时填充</summary>
        public static List<Item> GetPresetOrNull(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return _presets.TryGetValue(name, out List<Item> list) ? list : null;
        }

        /// <summary>
        /// 丢弃某个预设的内存缓存（删除文件或切表失焦时调用）。
        /// </summary>
        public static void RemovePreset(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            _presets.Remove(name);
        }

    }
}
