using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json.Linq;
using Oracle.Data;
using Oracle.Utils;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static Oracle.Data.OracleInterface;

// ⚠ BepInEx 6 (IL2CPP) 把 KeyboardShortcut 放在了这里，而不是 BepInEx.Configuration。
//   注意构造函数签名也不同：修饰键要传 KeyCode[] 而非单个 KeyCode。
using KeyboardShortcut = BepInEx.Unity.IL2CPP.Configuration.KeyboardShortcut;

namespace Oracle.ItemSpawn
{
    /// <summary>
    /// 虚空造物 —— 生成 / 复制物品。
    ///
    /// ══════════════ IL2CPP 移植要点 ══════════════
    ///
    /// 与 4.1 的差异集中在三处 API：
    ///
    ///   1. CloneItem 现在**必须**传 IDatabaseIdGenerator
    ///      4.1: item.CloneItem()
    ///      5.0: ItemExtensions.CloneItem&lt;T&gt;(T originalItem, IDatabaseIdGenerator idGenerator)
    ///      这里传 SameIdGenerator.Instance（保持原 ID），随后由 ReassignAllIds 统一改号 ——
    ///      与原版"克隆后就地重编号"的语义完全一致。
    ///
    ///   2. ThrowItem 的第 9 个参数不再有默认值
    ///      4.1: float makeVisibleAfterDelay = 0f（可省略）
    ///      5.0: 无默认值，必须显式传 0f
    ///
    ///   3. MongoID.Generate 需要显式参数
    ///      4.1: Generate(bool newProcessId = true)
    ///      5.0: Generate(bool newProcessId)（无默认值）
    ///
    /// 另外，ID 重分配已不再需要反射（见 ItemInstanceHelper）。
    /// </summary>
    public static class ItemSpawner
    {
        /// <summary>
        /// 按 Template ID 创建一个全新物品并入列工作区。
        /// </summary>
        /// <param name="templateId">物品模板 ID（24 位十六进制）</param>
        public static void AddItemToManager(string templateId)
        {
            // ⚠ 本方法原先在每一处失败点都是静默 return，出问题时完全无从排查
            //   （实机表现为"按了创建键但列表始终是空的"）。这里逐条给出可观测的失败原因。

            if (string.IsNullOrEmpty(templateId))
            {
                OracleLog.Warning("[Oracle] 造物失败：配置里的 Template ID 为空");
                return;
            }

            var itemFactory = Singleton<EFT.ItemFactory>.Instance;
            if (itemFactory == null)
            {
                OracleLog.Warning("[Oracle] 造物失败：ItemFactory 尚未就绪");
                return;
            }

            // 模板不存在直接返回，避免 CreateItem 抛异常
            var templates = itemFactory.ItemTemplates;
            if (templates == null || !templates.ContainsKey(templateId))
            {
                OracleLog.Warning($"[Oracle] 造物失败：模板不存在 {templateId}");
                return;
            }

            try
            {
                // 显式传 true：要求生成新的进程段，避免同进程内大量造物时的碰撞
                string newId = MongoID.Generate(true);

                Item newItem = itemFactory.CreateItem(newId, templateId, null);
                if (newItem == null)
                {
                    OracleLog.Warning($"[Oracle] 造物失败：CreateItem 返回空 {templateId}");
                    return;
                }

                OracleItemWorkspace.ActiveList.Add(newItem);

                OracleLog.Info(
                    $"[Oracle] 已创建实例 {newItem.Name.Localized()} ({templateId})，" +
                    $"当前列表 {OracleItemWorkspace.ActiveList.Count} 件");
            }
            catch (Exception ex)
            {
                OracleLog.Exception("ItemSpawner", "AddItemToManager", ex);
                OracleCommon.ShowError(ex);
            }
        }

        /// <summary>
        /// 克隆物品并放入玩家背包（背包满则掉落在地）。
        /// </summary>
        public static async Task CloneAndSpawnItemIntoInventoryAsync(Player player, Item item)
        {
            if (player == null || item == null)
            {
                OracleLog.Warning($"[Oracle] 战局内造物失败：{(player == null ? "本地玩家为空（未进入战局？）" : "物品为空")}");
                return;
            }

            var gameWorld = OracleGameState.CurrentGameWorld;
            if (gameWorld == null)
            {
                OracleLog.Warning("[Oracle] 战局内造物失败：GameWorld 为空");
                return;
            }

            // 先异步加载该物品树涉及的 assetbundle，否则模型/图标取不到
            await LoadItemBundlesAsync(item);

            Item clonedItem = CloneFresh(item);
            if (clonedItem == null)
            {
                OracleLog.Warning("[Oracle] 战局内造物失败：克隆结果为空");
                return;
            }

            ItemAddress targetLocation = FindEmptyLocation(player, clonedItem);

            if (targetLocation == null)
            {
                // 胸挂/口袋/背包全满 —— 掉落
                OracleLog.Info("[Oracle] 背包已满，改为掉落到地面");
                DropItemToGround(player, clonedItem, gameWorld);
                return;
            }

            var addOperationResult = ItemManipulator.Add(
                clonedItem,
                targetLocation,
                player.InventoryController,
                false          // simulate=false：真正执行
            );

            if (!addOperationResult.Succeeded)
            {
                // 寻址成功但服务端/本地校验拒绝 —— 转为掉落，避免物品凭空消失
                OracleLog.Warning($"[Oracle] 战局内造物：本地 Add 未通过，改为掉落（{addOperationResult.Error}）");
                DropItemToGround(player, clonedItem, gameWorld);
                return;
            }

            // ⚠ 刻意【不再】调用 TryRunNetworkTransaction。
            //
            //   物品已经通过上面那次本地 Add 进入背包，这就够了。而那个调用对 AddResult
            //   只会走 ConvertOperationResultToOperation —— 它不处理 AddResult（已核验），
            //   必然抛异常；麻烦的是 AddResult 自带 RollBack 语义，异常路径很可能
            //   把刚刚加进去的物品又回滚掉。这正好解释了"战局内也生成不了"。
            //
            //   4.1 之所以看起来相安无事：它把异常吞掉了，而且 Mono 环境下异常没有
            //   跨越 native/managed 边界，不会中断 native 侧的后续流程。
            //
            //   若日后确认了 5.0 正确的同步入口，再在这里补上即可。
            OracleLog.Info($"[Oracle] 战局内造物完成（本地）：{item.Name.Localized()}");
        }

        /// <summary>
        /// 克隆物品并掉落在玩家前方（异步执行）。
        /// </summary>
        public static async Task CloneAndDropItemAsync(Player player, Item originalItem, Camera cam)
        {
            // ⚠ 这一串原本全是静默 return —— 掉落没反应时完全无从判断死在哪一步。
            //   每个分支都给出可观测原因（4.1 同样如此，只是日志被吞掉了）。
            if (player == null) { OracleLog.Warning("[Oracle] 掉落失败：本地玩家为空（未进入战局？）"); return; }
            if (originalItem == null) { OracleLog.Warning("[Oracle] 掉落失败：物品为空"); return; }

            var gameWorld = OracleGameState.CurrentGameWorld;
            if (gameWorld == null) { OracleLog.Warning("[Oracle] 掉落失败：GameWorld 为空"); return; }

            try
            {
                Item clonedItem = CloneFresh(originalItem);
                if (clonedItem == null) { OracleLog.Warning("[Oracle] 掉落失败：克隆结果为空"); return; }

                await LoadItemBundlesAsync(clonedItem);
                DropItem(player, clonedItem, gameWorld, cam);
            }
            catch (Exception ex)
            {
                OracleCommon.ShowError(ex, "CloneAndDropItemAsync");
            }
        }

        /// <summary>
        /// 克隆 + 重编号 + 清洗状态（造物的统一入口）。
        ///
        /// 三步缺一不可：
        ///   CloneItem           —— 深拷贝整棵树（5.0 起需传 ID 生成器）
        ///   ReassignAllIds      —— 换新 ID，使其成为独立实例
        ///   CleanAndResetItem   —— 恢复耐久/清故障/设置带勾
        /// </summary>
        public static Item CloneFresh(Item source)
        {
            if (source == null) return null;

            // SameIdGenerator：克隆时保持原 ID，随后由 ReassignAllIds 统一重编号。
            // ⚠ 它是嵌套在 ItemExtensions 里的单例类型，必须写全限定名。
            Item cloned = ItemExtensions.CloneItem(
                source,
                ItemExtensions.SameIdGenerator.Instance);
            if (cloned == null) return null;

            return cloned
                .ReassignAllIds()
                .CleanAndResetItem(OracleItemWorkspace.SpawnedInSession);
        }

        /// <summary>
        /// 在玩家脚底生成掉落物（背包满时的兜底）。
        /// </summary>
        private static void DropItemToGround(Player player, Item item, GameWorld gameWorld)
        {
            if (player == null || item == null || gameWorld == null) return;

            // 脚底高一米、身前 0.5 米
            Vector3 spawnPosition = player.Transform.position + new Vector3(0f, 1f, 0f);
            spawnPosition += player.Transform.forward * 0.5f;
            spawnPosition.y += UnityEngine.Random.Range(-0.05f, 0.1f);

            gameWorld.ThrowItem(
                item,
                null,                    // Owner=null 表示"刷新出来的"，而非某玩家丢弃
                spawnPosition,
                Quaternion.identity,
                Vector3.zero,
                Vector3.zero,
                true,                    // syncable
                true,                    // performPickUpValidation
                0f                       // makeVisibleAfterDelay（5.0 取消了默认值，必须显式传）
            );
        }

        /// <summary>
        /// 在摄像机前方生成掉落物。
        /// </summary>
        private static void DropItem(Player player, Item item, GameWorld gameWorld, Camera cam)
        {
            if (item == null || gameWorld == null)
            {
                OracleLog.Warning("[Oracle] 掉落失败：物品或 GameWorld 为空");
                return;
            }

            // 定位基准：优先摄像机，取不到就退回 Player.CameraPosition
            // （游戏自带的视线 Transform，位置/朝向与摄像机一致）。
            // 两者都拿不到才真正放弃 —— 但那时一定给出原因。
            Vector3 origin;
            Vector3 forward;

            if (cam != null)
            {
                origin = cam.transform.position;
                forward = cam.transform.forward;
            }
            else
            {
                Transform eye = player?.CameraPosition;
                if (eye == null)
                {
                    OracleLog.Warning("[Oracle] 掉落失败：Camera.main 与 Player.CameraPosition 都取不到");
                    return;
                }
                origin = eye.position;
                forward = eye.forward;
            }

            Vector3 spawnPosition = origin + forward * 0.8f;

            // 只保留水平朝向，避免物品以奇怪的角度插进地里
            Vector3 flatForward = new Vector3(forward.x, 0f, forward.z);
            Quaternion spawnRotation = flatForward.sqrMagnitude < 0.0001f
                ? Quaternion.identity
                : Quaternion.LookRotation(flatForward);

            try
            {
                gameWorld.ThrowItem(
                    item,
                    null,
                    spawnPosition,
                    spawnRotation,
                    Vector3.zero,
                    Vector3.zero,
                    true,
                    true,
                    0f
                );

                OracleLog.Info($"[Oracle] 已掉落至 {spawnPosition}：{item.Name?.Localized()}");
            }
            catch (Exception ex)
            {
                OracleCommon.ShowError(ex, "DropItem.ThrowItem");
            }
        }

        /// <summary>
        /// 异步加载物品树涉及的资源包。
        ///
        /// 不做这一步的话，自定义物品（及其配件）会因为 prefab 未加载而无法正确显示。
        /// </summary>
        private static async Task LoadItemBundlesAsync(Item rootItem)
        {
            // ⚠ 整段包 try/catch，且**失败只降级不中断**。
            //
            //   这一步只是"预加载资源包，让物品模型/图标能正确显示"，
            //   属于锦上添花；而它曾经把整条造物/掉落链路一起拖死 ——
            //   实机表现就是"生成弹失败、掉落毫无反应"，真正的报错却埋在
            //   一个与业务无关的资源加载调用里。
            //   资源没加载成功最多是模型显示异常，绝不该让物品凭空消失。
            try
            {
                await LoadItemBundlesCoreAsync(rootItem);
            }
            catch (Exception ex)
            {
                OracleLog.Warning($"[Oracle] 物品资源预加载失败（不影响入包/掉落）：{ex.Message}");
            }
        }

        private static async Task LoadItemBundlesCoreAsync(Item rootItem)
        {
            if (rootItem == null) return;

            var poolManager = Singleton<ObjectsFactory>.Instance;
            if (poolManager == null) return;

            // 用 Il2Cpp 的 List 作为去重容器 —— LoadBundlesAndCreatePools 要的就是它
            var keys = new Il2CppSystem.Collections.Generic.List<ResourceKey>();

            var allItems = OracleCollections.ToManagedList(rootItem.GetAllItems());
            if (allItems == null) return;

            foreach (var item in allItems)
            {
                if (item == null) continue;

                var template = item.Template;
                if (template == null) continue;

                var prefab = template.Prefab;
                if (prefab != null && !keys.Contains(prefab)) keys.Add(prefab);

                var usePrefab = template.UsePrefab;
                if (usePrefab != null && !keys.Contains(usePrefab)) keys.Add(usePrefab);
            }

            if (keys.Count == 0) return;

            // ⚠ 三个 IL2CPP 特有的注意事项（均已对着 interop 元数据核对）：
            //   1. PoolsCategory / AssemblyType 是嵌套在 ObjectsFactory 里的类型，需全限定名
            //   2. 第 4 参是 Diz.Jobs.YieldDelegate（委托），而非 4.1 时代的 JobYieldPriority 枚举 ——
            //      JobYieldPriority.Immediate 正好是返回 YieldDelegate 的静态属性，故可直接用
            //   3. 第 6 参必须传 【Il2CppSystem】.Threading.CancellationToken，
            //      传 System.Threading.CancellationToken 会因类型隔离而解析到另一个重载
            //
            // ★ 第 6 参【绝对不能用 default(...)】—— 这是实机崩溃的真凶。
            //
            //   反汇编 interop 的 marshal 存根可以看到，各参数的封送方式并不相同：
            //       ldarg.s 03 (AssemblyType  枚举) → ldarga.s + stind.i（直接取地址）
            //       ldarg.s 04 (YieldDelegate)      → Il2CppObjectBaseToPtr      ← 允许 null
            //       ldarg.s 05 (IProgress)          → Il2CppObjectBaseToPtr      ← 允许 null
            //       ldarg.s 06 (CancellationToken)  → Il2CppObjectBaseToPtrNotNull
            //                                         + il2cpp_object_unbox      ← 不允许 null/零指针
            //
            //   也就是说 progress 传 null 是合法的，而 CancellationToken 这个**值类型**
            //   参数走的是 NotNull 分支。default(CancellationToken) 的内部指针是 IntPtr.Zero，
            //   于是 Il2CppObjectBaseToPtrNotNull 直接抛 NullReferenceException ——
            //   栈顶正好就是 IL2CPP.Il2CppObjectBaseToPtrNotNull，与实机日志完全吻合。
            //
            //   4.1（Mono，不过 interop 封送）传 null + CancellationToken.None 相安无事，
            //   移植时为了避开 System / Il2CppSystem 的二义性把 None 改写成了 default(...)，
            //   这一改就踩中了封送路径 —— 必须还原成 None。
            await poolManager.LoadBundlesAndCreatePools(
                ObjectsFactory.PoolsCategory.Raid,
                ObjectsFactory.AssemblyType.Local,
                keys,
                Diz.Jobs.JobYieldPriority.Immediate,
                null,                                              // IProgress：走 ToPtr，允许 null
                Il2CppSystem.Threading.CancellationToken.None      // ← 必须是实例，不能是 default
            );
        }

        // ─────────────── 桥接方法（按键回调不能直接 await，统一在这里兜异常）───────────────

        public static async void CloneAndSpawnItemIntoInventory(Player player, Item item)
        {
            // 入口就留痕：实机反馈"战局内生成会弹失败但是没有日志"，
            // 有了这一行才能区分"根本没进这个方法"和"进去后中途失败"。
            OracleLog.Info($"[Oracle] 战局内造物发起：{item?.Name?.Localized() ?? "(空物品)"}");

            try
            {
                await CloneAndSpawnItemIntoInventoryAsync(player, item);
            }
            catch (Exception ex)
            {
                // ⚠ 顺序很重要：先写日志，再弹通知。
                //   弹通知走的是 Unity 通知系统，在异步续体里（可能非主线程）
                //   有失败风险；先记日志能保证"至少留下证据"。
                OracleCommon.ShowError(ex, "CloneAndSpawnItemIntoInventory");
                OracleNotify.Warning("text_spawn_item_error".i18n());
            }
        }

        /// <summary>
        /// 克隆物品并掉落在视线前方（按键/按钮的统一入口）。
        /// </summary>
        public static async void CloneAndDropItem(Player player, Item item)
        {
            // ⚠ 原先这里是 `Camera cam = Camera.main; if (cam == null) return;` ——
            //   静默返回，玩家按了掉落键却"什么都没有发生"，日志里还一片空白。
            //   这正是实机反馈的"掉落则什么都没有发生"。
            //
            //   Tarkov 的主摄像机并不保证带 MainCamera 标签，Camera.main 很可能为 null。
            //   因此不再把它当作硬前提：拿不到就退回 Player.CameraPosition
            //   （游戏自带的视线 Transform，位置与朝向都与摄像机一致）。
            Camera cam = Camera.main;

            OracleLog.Info(
                $"[Oracle] 掉落发起：{item?.Name?.Localized() ?? "(空物品)"}，" +
                $"cam={(cam != null ? cam.name : "null→退回 CameraPosition")}");

            try
            {
                await CloneAndDropItemAsync(player, item, cam);
            }
            catch (Exception ex)
            {
                OracleNotify.Warning("text_spawn_item_error".i18n());
                OracleCommon.ShowError(ex, "CloneAndDropItem");
            }
        }

        /// <summary>
        /// 在仓库中生成物品。
        ///
        /// ══════════════ 为什么不再走"自定义操作类" ══════════════
        ///
        /// 4.1 的链路靠三个**托管侧继承自 IL2CPP 类型**的类来承载请求：
        ///     OracleAddCommand : CommandWithOwners
        ///     OracleAddDescriptor : InventoryOperationDescriptor
        ///     OracleAddOperationClass : AbstractOperation
        /// 它们必须经 ClassInjector.RegisterTypeInIl2Cpp 注入 IL2CPP 域才能被原生代码使用。
        ///
        /// ══ 实机结果：三个类型全部注入失败 ══
        ///    注入类型失败 OracleAddCommand:
        ///        NRE at ClassInjector.FindAbstractMethods → RegisterTypeInIl2Cpp
        ///    注入类型失败 OracleAddDescriptor:
        ///        NRE at ClassInjector.FindBaseInterfaceImplementation → RegisterTypeInIl2Cpp
        ///    注入类型失败 OracleAddOperationClass: 同上
        /// 紧接着：
        ///    EntryPointNotFoundException at AbstractOperation.ToBaseInventoryCommands
        ///      at BackEndInventoryController.Execute
        ///
        /// 因果很清楚：类型没进 IL2CPP 域 → 原生侧没有对应实现 → 虚表槽位为空 →
        /// 走到 ToBaseInventoryCommands 时找不到入口点。
        /// 注意 OracleBehaviour（MonoBehaviour）同一次启动注入**成功**，
        /// 所以不是 ClassInjector 整体不可用，而是这几个继承游戏类型的注入在
        /// 本环境（SPTushonka 重写过的 v31 元数据）下走不通。
        ///
        /// ══ 所以改成绕开类型注入 ══
        /// 关键事实：会话发包接口的形参是 `Il2CppSystem.Object`，不是 BaseInventoryCommand：
        ///     void IClientSession.SendOperationRightNow(Il2CppSystem.Object operation, Callback callback)
        /// 也就是说它只要求"一个可被序列化的对象"。而 Newtonsoft 的 JObject
        /// 本身就是 IL2CPP 侧的真实类型（无需注入），序列化时原样内联。
        /// 于是我们直接手工拼出服务端期望的请求体丢给它 —— 与 4.1 走完
        /// ToBaseInventoryCommand 后得到的 JSON 完全一致。
        ///
        /// ══ 服务端契约（已在 EternalCycleServer/Core.cs 核对）══════
        ///     public record ProfileStashSyncRequestData : BaseInteractionRequestData
        ///     {
        ///         [JsonPropertyName("stashData")] public Item[] StashData { get; set; }
        ///     }
        ///     new ItemRouteAction&lt;ProfileStashSyncRequestData&gt;("SyncStashExtend", ...)
        /// 即：{ "Action": "SyncStashExtend", "stashData": [ ...扁平物品... ] }
        /// 而 OracleItemPresetIO.Serialize 产出的正是这套扁平物品格式
        /// （与 4.1 存档逐字段一致，已比对真实存档）。
        /// </summary>
        public static void CloneAndSpawnItemIntoStash(Item item)
        {
            var controller = OracleGameState.StashController;
            if (controller == null || item == null)
            {
                // 原先这里是静默 return：仓库界面没打开过时 StashController 为空，
                // 玩家点了"生成"却什么都没发生，也没有任何提示。
                OracleLog.Warning(
                    $"[Oracle] 仓库造物失败：{(controller == null ? "StashController 为空（请先打开一次仓库界面）" : "物品为空")}");
                return;
            }

            try
            {
                // ── 只做空位预检，绝不做本地落位 ──
                //
                // ⚠ 这里曾经真的执行了 ItemManipulator.Add(item, targetLocation, controller,
                //   simulate:false)，本意是"让物品立刻显示"。实测直接 softlock：
                //
                //     [Oracle] 已上报服务端：LEDX皮肤透照仪（201 字节）
                //     [Error] (x: 7, y: 1, r: Horizontal) in grid hideout in item
                //             Edge of darkness stash 10x68 (id: 5fe49444ae6628187a2e78b8)
                //             is taken by another item when trying to add item
                //             Item_barter_medical_transilluminator
                //
                //   因果链：本地先把它放到 (7,1) → 序列化时该位置随 location 一并写进
                //   stashData → 服务端照着 (7,1) 登记 → **客户端会自动应用服务端回包**
                //   （这点与传不传 callback 无关，我先前判断错了）→ 回包又往 (7,1) 放一次
                //   → 撞上自己刚放的那件 → 抛异常 → 库存状态错乱 → softlock。
                //
                //   正解与 4.1 完全一致：本地**什么都不放**（4.1 用的是 simulate:true，
                //   同样没有真正落位），只把请求发给服务端，由回包统一落位。
                //   这样 item 保持无父节点，扁平化产出的根条目不带 parentId 与 location，
                //   服务端自行挑空格放置 —— 物品在回包到达后自然出现在仓库里。
                ItemAddress targetLocation = FindEmptyLocationInStash(controller, item);
                if (targetLocation == null)
                {
                    OracleNotify.Warning("仓库已满！");
                    OracleLog.Warning("[Oracle] 仓库造物失败：仓库内找不到空位");
                    return;
                }

                // ── 上报服务端（唯一的落位来源）──
                SyncStashExtend(item, controller);

                OracleNotify.Message($"{item.Name.Localized()} 已上报服务端");
            }
            catch (Exception ex)
            {
                OracleLog.Exception("ItemSpawner", "CloneAndSpawnItemIntoStash", ex);
                OracleCommon.ShowError(ex, "CloneAndSpawnItemIntoStash");
            }
        }

        /// <summary>
        /// 把一件物品作为 SyncStashExtend 请求发给服务端，让 Profile 登记它。
        ///
        /// 走的是游戏自己的后端会话（BackEndInventoryController._backendSession），
        /// 不构造任何自定义 IL2CPP 类型 —— 见 CloneAndSpawnItemIntoStash 的说明。
        /// </summary>
        private static void SyncStashExtend(Item item, InventoryController controller)
        {
            // 扁平化：把整棵物品树（含配件/子弹）摊平成数组，对应服务端的 Item[]
            string flatJson = OracleItemPresetIO.Serialize(new List<Item> { item });
            if (string.IsNullOrEmpty(flatJson) || flatJson == "[]")
            {
                OracleLog.Warning("[Oracle] 上报失败：物品扁平化结果为空");
                return;
            }

            var payload = new JObject();
            payload["Action"] = "SyncStashExtend";
            payload["stashData"] = JArray.Parse(flatJson);

            // 会话在 BackEndInventoryController 上（仓库界面的 controller 实际就是这个类型）
            var backend = controller.TryCast<BackEndInventoryController>();
            if (backend == null)
            {
                OracleLog.Warning(
                    $"[Oracle] 上报失败：controller 不是 BackEndInventoryController（实际 {controller.GetType().Name}）");
                return;
            }

            var session = backend._backendSession;
            if (session == null)
            {
                OracleLog.Warning("[Oracle] 上报失败：后端会话为空（未连接服务端？）");
                return;
            }

            // ★ 回调绝不能传 null —— 这是客户端软锁的根因。
            //
            //   依据 4.1 反编译的 ClientBackendSession.SendCallback
            //   （I:\build\codespace\4.1\Assembly-CSharp\EFT\ClientBackendSession.cs）：
            //
            //     line 332:  this.QueueStatus = EOperationQueueStatus.AwaitingResponse;   // 发包前置位
            //     line 393:  immediateCommand.Callback.Invoke(result);                    // ← 没有空判断
            //     line 461:  this.QueueStatus = EOperationQueueStatus.Idle;               // 复位 + ReadWaitingQueue
            //
            //   传 null 会在 393 行抛 NullReferenceException，SendCallback 就此中断，
            //   461 行的复位与后续的 ReadWaitingQueue 永远执行不到 →
            //   QueueStatus 永久停在 AwaitingResponse → 此后 ReadWaitingQueue 一律提前返回
            //   → 整个操作队列死掉 → 客户端软锁。
            //
            //   实机表现完全吻合：日志里没有任何异常、物品已正确登记且重启后仍在，
            //   但客户端软锁。
            //
            //   回调本身只是"操作已送达"的收尾信号：
            //   服务端回包中的 Profile 变更由 SendCallback 里的 ApplyProfileChanges 统一应用
            //   （位置在调用回调之前），所以这里什么都不用做，记一条日志即可。
            Action<Comfort.Common.IResult> onResult = result =>
            {
                // ⚠ 这个回调运行在客户端的回包处理路径上，【绝对不能抛出】——
                //   抛出会重新制造上面那条软锁链路。整体包 try/catch。
                try
                {
                    OracleLog.Info($"[Oracle] 服务端回包：Succeed={result?.Succeed}, Error={result?.Error}");
                }
                catch
                {
                    // 忽略：回调内任何异常都不允许影响客户端的队列状态机
                }
            };

            // op_Implicit(System.Action<Comfort.Common.IResult>) → Comfort.Common.Callback
            Comfort.Common.Callback callback = onResult;

            session.SendOperationRightNow(payload, callback);

            OracleLog.Info($"[Oracle] 已上报服务端：{item.Name.Localized()}（{flatJson.Length} 字节）");
        }

        // ─────────────── 寻址 ───────────────

        /// <summary>
        /// 战局内寻址：胸挂 → 口袋 → 背包，返回第一个放得下的位置。
        /// </summary>
        public static ItemAddress FindEmptyLocation(Player player, Item newItem)
        {
            if (player?.Inventory == null || newItem == null) return null;

            var equipment = player.Inventory.Equipment;
            if (equipment == null)
            {
                OracleLog.Warning("[Oracle] 寻址失败：player.Inventory.Equipment 为空");
                return null;
            }

            EquipmentSlot[] slotsToCheck =
            {
                EquipmentSlot.Pockets,
                EquipmentSlot.TacticalVest,
                EquipmentSlot.Backpack
            };

            // 只在最终找不到位置时才输出诊断，避免刷屏（每个槽位一条，最多 3 条）
            string trace = "";

            foreach (var slotType in slotsToCheck)
            {
                var slot = equipment.GetSlot(slotType);
                if (slot == null)
                {
                    trace += $"\n  · {slotType}: GetSlot 返回空";
                    continue;
                }

                // ★ 只有容器类装备（胸挂/背包）才有 Grids。
                //
                //   ⚠ 这里【必须】用 TryCast，绝不能用 `is` / `as` 向下转型。
                //
                //   Slot.ContainedItem 的**声明类型**是 Item，而 Il2CppInterop 是按声明
                //   类型生成代理对象的 —— 拿到的托管包装运行时类型就是 Item，
                //   哪怕原生对象实际是 CompoundItem。于是 `is CompoundItem` 恒为 false，
                //   三个槽位全部被跳过 → 找不到空位 → 本该进背包的物品被丢到地上。
                //   实机表现正是："检查不到身上的有效格子，生成会直接掉落地面"。
                //
                //   TryCast<T>() 走原生类型判定（il2cpp 侧的继承链），是跨层转型的唯一可靠方式。
                //   本工程其它地方（InfiniteAmmo / OracleCorpseDataManager / TelekinisisUnlock）
                //   用的都是这个写法，只有这里漏了。
                //
                //   对照：仓库路径写的是 controller.Inventory.Stash，它的声明类型本身就是
                //   Stash（派生自 CompoundItem），所以那边的 `is` 恰好成立 —— 这也正是
                //   "仓库好使、战局内不好使"的原因。
                var containerItem = slot.ContainedItem?.TryCast<CompoundItem>();
                if (containerItem == null)
                {
                    trace += $"\n  · {slotType}: 未装备 / 不是 CompoundItem" +
                             $"（ContainedItem={(slot.ContainedItem == null ? "null" : "非空")}）";
                    continue;
                }

                var grids = containerItem.Grids;
                if (grids == null || grids.Length == 0)
                {
                    trace += $"\n  · {slotType}: 没有 Grid";
                    continue;
                }

                // 记录每个格子的尝试结果，便于判断"格子太小"还是"确实满了"
                string gridTrace = "";

                for (int i = 0; i < grids.Length; i++)
                {
                    var grid = grids[i];
                    if (grid == null) { gridTrace += $" [格子{i}=null]"; continue; }

                    var addressInGrid = grid.FindLocationForItem(newItem);
                    if (addressInGrid != null) return addressInGrid;

                    gridTrace += $" [格子{i}无空位]";
                }

                trace += $"\n  · {slotType}: {grids.Length} 个格子均无合适位置{gridTrace}";
            }

            OracleLog.Warning(
                $"[Oracle] 寻址失败，物品将掉落地面：{newItem.Name?.Localized()}{trace}");

            return null;
        }

        /// <summary>
        /// 战局外寻址：只在仓库(Stash)里找，不碰身上装备。
        /// </summary>
        public static ItemAddress FindEmptyLocationInStash(InventoryController controller, Item newItem)
        {
            if (controller?.Inventory == null || newItem == null) return null;

            if (!(controller.Inventory.Stash is CompoundItem stash)) return null;

            var grids = stash.Grids;
            if (grids == null) return null;

            int n = grids.Length;
            for (int i = 0; i < n; i++)
            {
                var grid = grids[i];
                if (grid == null) continue;

                var location = grid.FindLocationForItem(newItem);
                if (location != null) return location;
            }
            return null;
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(4)]
    public class ItemSpawnerCfg : IOracleCfg, IOracleKeyUpdate
    {
        internal static ConfigEntry<KeyCode> AddItemKey { get; set; }
        internal static ConfigEntry<KeyboardShortcut> CopyItemKey { get; set; }
        internal static ConfigEntry<string> TargetItemId { get; set; }
        internal static ConfigEntry<KeyCode> DropItemKey { get; set; }

        private const string Section = "4. 奇迹之门 / Creation Module";

        public void Initialize(ConfigFile config)
        {
            TargetItemId = config.Bind(
                Section,
                "物品 Template ID",
                "59faff1d86f7746c51718c9c",
                new ConfigDescription(
                    "cfg_creation_module_item_id_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_creation_module_item_id_name".i18n(),
                        IsAdvanced = false,
                        Order = 129
                    }
                )
            );

            AddItemKey = config.Bind(
                Section,
                "创建实例",
                KeyCode.KeypadDivide,
                new ConfigDescription(
                    "cfg_creation_module_item_create_key_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_creation_module_item_create_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 128
                    }
                )
            );

            CopyItemKey = config.Bind(
                Section,
                "保存物品",
                // 修饰键在 BepInEx 6 中要传数组（4.1 是可变参数）
                new KeyboardShortcut(KeyCode.C, new KeyCode[] { KeyCode.LeftShift }),
                new ConfigDescription(
                    "cfg_creation_module_item_copy_key_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        // ⚠ 键名里的 "itemm" 是 4.1 就有的拼写错误（多一个 m），
                        //   语言文件里也是这个键，改掉会导致配置显示为空。
                        DispName = "cfg_creation_module_itemm_copy_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 127
                    }
                )
            );

            DropItemKey = config.Bind(
                Section,
                "复制物品",
                KeyCode.Keypad5,
                new ConfigDescription(
                    "cfg_creation_module_item_drop_key_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_creation_module_itemm_drop_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 126
                    }
                )
            );
        }

        public void RegisterKeyUpdate()
        {
            OracleEvent.OnUpdate += KeyUpdate;
        }

        public static void KeyUpdate()
        {
            if (Input.GetKeyDown(AddItemKey.Value))
            {
                ItemSpawner.AddItemToManager(TargetItemId.Value);
            }

            if (Input.GetKeyDown(DropItemKey.Value))
            {
                // savedItem 由 ItemCatcher 在鼠标悬停物品 + 按保存键时捕获
                ItemSpawner.CloneAndDropItem(OracleGameState.LocalPlayer, ItemCatcher.SavedItem);
            }
        }
    }
}
