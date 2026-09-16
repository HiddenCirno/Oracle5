using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using Oracle.Data;
using Oracle.Patches;
using Oracle.Utils;
using System;

namespace Oracle
{
    /// <summary>
    /// 插件入口。
    ///
    /// 注意 BepInEx 6 (IL2CPP) 与 BepInEx 5 (Mono) 的架构差异：
    ///   · 基类是 BasePlugin，**不是 MonoBehaviour** —— 没有 Awake/Start/Update/OnGUI，
    ///     也没有 StartCoroutine。这些能力由 OracleBehaviour 组件承载。
    ///   · 初始化入口是 Load()，对应旧版的 Awake()。
    ///   · 日志用基类自带的 Log 属性，无需再自建 Logger。
    ///
    /// 当前进度（Phase 2）：配置层 + 每帧驱动 + 战局状态捕获（GameWorld.OnGameStarted）。
    /// </summary>
    [BepInPlugin(PluginsInfo.GUID, PluginsInfo.NAME, PluginsInfo.VERSION)]
    [BepInProcess("EscapeFromTarkov.exe")]
    public class PluginsCore : BasePlugin
    {
        /// <summary>本插件的 Harmony 实例。静态持有以便后续模块动态增删补丁。</summary>
        internal static Harmony Harmony { get; private set; }

        /// <summary>
        /// 驱动组件实例。静态持有是刻意的：
        /// 防止托管侧失去引用后被 GC 回收（IL2CPP 下组件仍存活但托管包装失效，
        /// 会导致 Update 静默停止 —— 这正是可行性报告 §5 里标记的 GC 陷阱）。
        /// </summary>
        internal static OracleBehaviour Behaviour { get; private set; }

        /// <summary>
        /// 所有游戏侧补丁。用显式列表而非 harmony.PatchAll() 全程序集扫描：
        /// PatchAll 一旦遇到任一无法解析的目标就会整体抛出，导致【全部补丁都不生效】。
        /// 逐个注册可以让单个目标失效只影响它自己。
        ///
        /// ⚠ 选择挂钩目标时的硬性要求：**不要挂钩实现为空或极简的方法**。
        ///   IL2CPP 会在编译期做方法体合并，把 IL 相同的多个方法折叠到同一个原生入口，
        ///   此时挂钩一个方法等于挂钩所有被折叠的方法（GameWorld.OnDestroy 曾因此
        ///   被调用 64 万次，见 Patches/GameStartPatch.cs 末尾的事故记录）。
        ///   Unity 生命周期空实现（OnDestroy/OnEnable/Awake）是最高危的一类。
        /// </summary>
        private static readonly Type[] PatchTypes =
        {
            typeof(GameStartPatch),

            // ── 能力模块 · 生存 ──
            typeof(Ability.GodMode.ApplyDamageInfoPatch),
            typeof(Ability.GodMode.ApplyDamagePatch),
            typeof(Ability.GodMode.KillPatch),
            typeof(Ability.GodMode.DestroyBodyPartPatch),

            // ── 能力模块 · 摔落 ──
            typeof(Ability.NoFallenDamage.ApplyDamageInfoPatch),
            typeof(Ability.NoFallenDamage.ApplyDamagePatch),

            // ── 能力模块 · 负重 ──
            typeof(Ability.InfinityWeightPatch),

            // ── 能力模块 · 飞行 ──
            typeof(Ability.FlyModeNoGravityPatch),
            typeof(Ability.FlyModeMotionPatch),
            typeof(Ability.FlyModeNoFallDamagePatch),
            typeof(Ability.FlyModeBlockJumpPatch),

            // ── 能力模块 · 念力开锁 ──
            typeof(Ability.TelekinisisUnlock.GetActionsPatch),

            // ── 战斗模块 · 自瞄与后坐力 ──
            typeof(Combat.NoRecoilNewPatch),
            typeof(Combat.NoRecoilOldPatch),
            typeof(Combat.MagicBulletPatch),

            // ── 战斗模块 · 无限弹药（四条开火链路各自处理）──
            typeof(Combat.InfiniteAmmo.ShootPatch),
            typeof(Combat.InfiniteAmmo.LauncherFirePatch),
            typeof(Combat.InfiniteAmmo.RocketLauncherFirePatch),
            typeof(Combat.InfiniteAmmo.FlareGunFirePatch),

            // ── 战斗模块 · 武器状态 ──
            typeof(Combat.NoMalfunction.PlayerWeaponNeverJamPatch),
            typeof(Combat.NoWeaponDurabilityCost.DurabilityLossPatch),

            // ── 战斗模块 · 隐身 ──
            typeof(Combat.GhostMode.BotGroupAddEnemyPatch),

            // ── 奇迹之门 · 物品捕获（悬停追踪）──
            typeof(ItemSpawn.ItemViewPointerEnterPatch),
            typeof(ItemSpawn.ItemViewPointerExitPatch),
            typeof(ItemSpawn.GridItemViewPointerEnterPatch),
            typeof(ItemSpawn.GridItemViewPointerExitPatch),
            // 手册图标：必须挂悬停进/出这一对 lambda，不能挂 Show(Item) ——
            // Show 是渲染批次触发的，会导致捕获到同批次的任意一件物品。详见该类注释。
            typeof(ItemSpawn.EntityIconHoverStartPatch),
            typeof(ItemSpawn.EntityIconHoverEndPatch),
            typeof(ItemSpawn.HideoutItemViewPointerEnterPatch),

            // ── 奇迹之门 · 仓库造物（服务端登记路由）──
            typeof(ItemSpawn.ItemSpawnStashPatch.InventoryScreenShowPatch),

            // ── 创世引擎 · 战利品（远程搜索）──
            // 让未搜索过的物品也显示为可交互。这是本模块侵入性最强的补丁 ——
            // 出现异常行为时优先怀疑它，详见 LootManagerGUI.RemoteSearchPatch 注释。
            typeof(RaidManager.LootManagerGUI.RemoteSearchPatch),

            // ── 创世引擎 · AI 管理（远程搜身辅助）──
            // 让搜索控制器认为"没有发现新容器"，直接按已搜索处理。
            typeof(RaidManager.AIManagerGUI.TryFindChangedContainerPatch),
            // 注：此处原有一个 ConvertOperationPatch（为 AddResult 桥接）。
            //     5.0 下该桥接不可能成立，已随链路重构一并移除 ——
            //     原因见 ItemSpawnStashPatch 的类注释，勿加回。
        };

        public override void Load()
        {
            Log.LogInfo("Oracle 5.0 正在加载...");

            // 顺序不可颠倒：配置项的显示名要经过 .i18n()，语言必须先就绪
            LocaleManager.Initialize(Config);
            Log.LogInfo($"[Oracle] 语言已加载，当前: {LocaleManager.CurrentLanguage.Value}");

            // 反射自举：扫描本程序集所有 IOracleCfg 实现并按 OracleCfgOrder 排序初始化
            OracleEvent.InitializeConfigs(Config);

            // 初始化绘制样式（ESP 材质 / 文本样式）。
            // 必须在事件订阅之前 —— 订阅者可能在第一次绘制时就引用这些样式。
            OracleRendering.Initialize();
            Log.LogInfo($"[Oracle] 绘制层就绪: {OracleRendering.IsReady}");

            // 注册按键监听与事件订阅
            OracleEvent.InitializeKeyUpdate();
            OracleEvent.InitializeEventSubscribe();

            // 向 IL2CPP 域注入自定义类型 —— 必须早于任何实例被创建
            RegisterInjectedTypes();

            // 挂载每帧驱动组件
            MountBehaviour();

            // 启动后台扫描协程（依赖 Behaviour 已挂载）
            StartScanners();

            // 注册游戏侧补丁
            InitializePatches();

            Log.LogInfo("Oracle 5.0 加载完成");
        }

        /// <summary>
        /// 自定义类型注入入口 —— **目前不需要注入任何类型**。
        ///
        /// ══════════════ 曾经的失败与结论 ══════════════
        ///
        /// 奇迹之门的造物路由原先靠三个托管侧继承游戏类型的类：
        ///     OracleAddCommand        : CommandWithOwners
        ///     OracleAddDescriptor     : InventoryOperationDescriptor
        ///     OracleAddOperationClass : AbstractOperation
        /// 它们必须注入 IL2CPP 域。实机结果：**三个全部注入失败**
        ///     OracleAddCommand:        NRE at ClassInjector.FindAbstractMethods
        ///     OracleAddDescriptor:     NRE at ClassInjector.FindBaseInterfaceImplementation
        ///     OracleAddOperationClass: 同上
        /// 随后 controller.Execute 在原生侧抛 EntryPointNotFoundException。
        ///
        /// 同一次启动里 OracleBehaviour（MonoBehaviour）注入是成功的，
        /// 说明不是 ClassInjector 整体不可用，而是"继承游戏类型的注入"在本环境
        /// （SPTushonka 重写过的 v31 元数据）下走不通。
        ///
        /// 结论：**造物改走不依赖类型注入的路径**（手工拼请求体 → 直接交给后端会话，
        /// 详见 ItemSpawner.CloneAndSpawnItemIntoStash），那三个类已随 AddItemRouter.cs
        /// 一并删除。
        ///
        /// ⚠ 若日后要重新引入"托管继承 IL2CPP 类型"，务必先单独验证注入能否成功，
        ///   不要默认 RegisterTypeInIl2Cpp 一定可用。
        /// </summary>
        private void RegisterInjectedTypes()
        {
            // 目前无自定义类型需要注入。
        }

        /// <summary>
        /// 逐个注册补丁，每个独立 try/catch。
        /// 这样即便某个目标在游戏更新后改名/被剥离，其余功能依然可用。
        /// </summary>
        private void InitializePatches()
        {
            Harmony = new Harmony(PluginsInfo.GUID);

            int ok = 0, fail = 0;
            foreach (Type patchType in PatchTypes)
            {
                try
                {
                    Harmony.PatchAll(patchType);
                    ok++;
                    Log.LogInfo($"[Oracle] 补丁成功: {patchType.Name}");
                }
                catch (Exception ex)
                {
                    fail++;
                    // 失败不致命：其余补丁继续注册。升级游戏后优先看这些行。
                    Log.LogError($"[Oracle] 补丁失败 {patchType.Name}: {ex.Message}");
                }
            }

            Log.LogInfo($"[Oracle] 补丁注册完成: 成功 {ok}, 失败 {fail}");
        }

        /// <summary>
        /// 启动后台扫描协程。
        ///
        /// ⚠ 两个必须点：
        ///   1. 协程只能挂在 MonoBehaviour 上 —— BasePlugin 没有 StartCoroutine，
        ///      所以必须用 Behaviour 作为宿主。
        ///   2. 托管 IEnumerator 不能直接交给 IL2CPP 的 StartCoroutine（它要的是
        ///      Il2CppSystem.Collections.IEnumerator）。BepInEx 提供了扩展方法
        ///      MonoBehaviourExtensions.StartCoroutine 负责包装成 Il2CppManagedEnumerator。
        ///      这里刻意用【静态调用】形式而非扩展语法，避免与 MonoBehaviour 上
        ///      接受 Il2Cpp IEnumerator 的同名实例方法产生重载歧义。
        /// </summary>
        private void StartScanners()
        {
            if (Behaviour == null)
            {
                Log.LogError("[Oracle] 驱动组件未挂载，扫描协程无法启动");
                return;
            }

            StartScanner("战利品", OracleLootDataManager.LootScannerCoroutine());
            StartScanner("尸体", OracleCorpseDataManager.CorpseScannerCoroutine());
            StartScanner("绊雷", OracleTripwireManager.TripwireScannerCoroutine());
            StartScanner("愿望单", OracleWishlistDataManager.WishlistScannerCoroutine());
        }

        /// <summary>
        /// 启动单个扫描协程，失败只影响它自己。
        ///
        /// ⚠ 两个必须点：
        ///   1. 协程只能挂在 MonoBehaviour 上 —— BasePlugin 没有 StartCoroutine，
        ///      所以用 Behaviour 作为宿主。
        ///   2. 托管 IEnumerator 不能直接交给 IL2CPP 的 StartCoroutine（它要的是
        ///      Il2CppSystem.Collections.IEnumerator）。BepInEx 的扩展方法会把它
        ///      包装成 Il2CppManagedEnumerator。这里刻意用【静态调用】形式而非扩展
        ///      语法，避免与 MonoBehaviour 上接受 Il2Cpp IEnumerator 的同名实例方法
        ///      产生重载歧义；全限定名则是因为本工程引用了约 170 个 interop 程序集，
        ///      MonoBehaviourExtensions 这类通用名有重名风险。
        /// </summary>
        private void StartScanner(string name, System.Collections.IEnumerator routine)
        {
            try
            {
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(Behaviour, routine);
                Log.LogInfo($"[Oracle] {name}扫描协程已启动");
            }
            catch (Exception ex)
            {
                Log.LogError($"[Oracle] {name}扫描协程启动失败: {ex}");
            }
        }

        /// <summary>
        /// 把 OracleBehaviour 挂到一个常驻 GameObject 上，以取得 Unity 的每帧回调。
        ///
        /// ⚠ 这里【必须】用 BepInEx 提供的 AddComponent&lt;T&gt;()，不能自己 new GameObject + DontDestroyOnLoad。
        ///
        /// 踩坑记录：最初的实现是手工
        ///     var go = new GameObject("Oracle");
        ///     UnityEngine.Object.DontDestroyOnLoad(go);
        ///     go.AddComponent&lt;OracleBehaviour&gt;();
        /// 结果是 Awake/OnEnable 在 frame=0 正常触发，紧接着同一帧就 OnDisable/OnDestroy -
        /// 组件在游戏加载首个场景时被销毁，Update/OnGUI 一次都没跑到。
        ///
        /// 反汇编 BepInEx 的实现（BasePlugin.AddComponent → IL2CPPChainloader.AddUnityComponent
        /// → Il2CppUtils.AddComponent）后看到它真正做的事：
        ///     · 一个静态持有的单例 GameObject（managerGo），**完全不调用 DontDestroyOnLoad**
        ///     · 设 hideFlags = 61 (HideAndDontSave = HideInHierarchy|DontSaveInEditor|
        ///       NotEditable|DontSaveInBuild|DontUnloadUnusedAsset) —— 这才是钉住对象的关键
        ///     · 内部自行处理 ClassInjector 注册
        /// 这正是 ConfigurationManager 等成熟插件走的路径，直接复用即可。
        /// </summary>
        private void MountBehaviour()
        {
            try
            {
                // 静态字段持有是刻意的：Il2CppInterop 文档警告注入实例由 IL2CPP 侧 GC 管理，
                // 「即使托管域持有引用也可能被回收」。这里保持强引用，避免托管包装失效。
                Behaviour = AddComponent<OracleBehaviour>();
                Log.LogInfo($"[Oracle] 驱动组件已挂载: {Behaviour != null}");
            }
            catch (Exception ex)
            {
                // 这里失败意味着 Update/OnGUI 全部不可用，必须显式暴露，不能静默
                Log.LogError($"[Oracle] 驱动组件挂载失败，Update/OnGUI 将不可用: {ex}");
            }
        }
    }
}
