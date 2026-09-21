using EFT.SynchronizableObjects;
using Oracle.ESP;
using Oracle.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Oracle.Data
{
    /// <summary>
    /// 绊雷数据总线
    ///
    /// IL2CPP 移植要点：
    ///   旧版用反射读 TripwireProceduralMesh 的私有字段 _fromPositionPivot / _toPositionPivot。
    ///   在 SPT5 中这两个字段**已被 Il2CppInterop 生成为 public 属性**（实测确认），
    ///   因此反射整段删除，改为直接属性访问 —— 更快，且不会因字段改名而静默失效。
    /// </summary>
    public static class OracleTripwireManager
    {
        /// <summary>全局缓存表</summary>
        public static List<TripwireData> CachedTripwires = new List<TripwireData>();

        private const float ScanInterval = 2f;

        /// <summary>
        /// 相位偏移 —— 与其它三个扫描器错开，避免尖峰叠加。
        /// 详见 `OracleLootDataManager.PhaseOffset` 的完整说明。
        /// </summary>
        private const float PhaseOffset = 0.74f;

        /// <summary>扫描协程</summary>
        public static IEnumerator TripwireScannerCoroutine()
        {
            var frontBuffer = new List<TripwireData>(64);
            var backBuffer = new List<TripwireData>(64);
            CachedTripwires = frontBuffer;

            while (true)
            {
                yield return new WaitForSeconds(ScanInterval + PhaseOffset);

                if (!OracleGameState.InRaid || !TripwireESPCfg.EnableTripwireESP.Value)
                {
                    backBuffer.Clear();
                    Swap(ref frontBuffer, ref backBuffer);
                    CachedTripwires = frontBuffer;
                    continue;
                }

                backBuffer.Clear();

                // ══════════════════════════════════════════════════════════
                //  ★★ 读游戏自己的绊雷注册表 —— **不再做全场景扫描**
                //
                //  原先这里是：
                //      Object.FindObjectsOfType<TripwireProceduralMesh>()
                //  它的开销与【场景对象总数】成正比（战局里几万个），
                //  **与命中多少个无关** —— 所以一个绊雷都没有时，它照样把整个
                //  场景扫一遍。每 2 秒一次，这就是"隔几秒卡一下"的根因。
                //  （4.1 是同一份代码，所以那边也一样；这不是移植引入的。）
                //
                //  那句「绊雷数量少，无需分帧」的注释推理是错的：
                //  数量少只说明**结果**少，不说明**代价**小。
                //
                //  游戏自己维护着这张表，直接读即可：
                //      GameWorld.PlantTripwire()    → TripwireManager.AddTripwire()
                //      GameWorld.TriggerTripwire()  → TripwireManager.RemoveTripwire()
                //  玩家放的雷和 AI 放的雷（BotMinesData）都经
                //  InventoryController.PlantTripwire 这一条路，所以表是完整的。
                //  复杂度：O(场景对象数) → O(绊雷数)。
                //
                //  ⚠ 全程**不需要挂钩** —— 不碰本项目"方法体合并"那条雷区
                //    （挂空实现/Awake 类方法会被折叠成全游戏共享入口）。
                // ══════════════════════════════════════════════════════════
                try
                {
                    var world = OracleGameState.CurrentGameWorld;

                    // 逐层判空：processor / manager 在世界拆毁时会被置 null
                    var processor = world != null ? world.SynchronizableObjectLogicProcessor : null;
                    var manager = processor != null ? processor.TripwireManager : null;
                    var list = manager != null ? manager._tripwires : null;

                    if (list != null)
                    {
                        int count = list.Count;      // 只遍历绊雷，不碰场景
                        for (int i = 0; i < count; i++)
                        {
                            TripwireSynchronizableObject tw = list[i];
                            if (tw == null) continue;

                            // 只收「已布设且还在」的。触发过/已收起的会是
                            // Inert / None / Exploding / Exploded。
                            // 用 TripwireState 而不是试探子物体的 activeSelf ——
                            // 游戏自己判「雷还在」用的就是 Wait
                            //（InteractionContextHelper / PlantedMineAIInfo）。
                            ETripwireState state = tw.TripwireState;
                            if (state != ETripwireState.Wait && state != ETripwireState.Active) continue;

                            // ⚠ 起止点必须读【mesh 的 pivot】，**不能**用同步对象上的
                            //   FromPosition / ToPosition。后者是**桩子底部**的位置，
                            //   而线实际连的是**桩子轴心** —— SetupGrenade 里是
                            //       _line.SetPosition(_firstStakePivot.position, _secondStakePivot.position)
                            //   两者高度不同，用错了线会贴地画。
                            TripwireProceduralMesh line = tw._line;
                            if (line == null) continue;

                            Vector3 start = line._fromPositionPivot;
                            Vector3 end = line._toPositionPivot;

                            backBuffer.Add(new TripwireData
                            {
                                StartPos = start,
                                EndPos = end,
                                CenterPos = (start + end) * 0.5f,
                                OverlayLabel = "text_esp_overlay_tripwire_label".i18n()
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 读表失败只影响绊雷 ESP，绝不能让它打断整个扫描协程
                    OracleLog.Exception("TripwireScanner", "读取绊雷注册表", ex);
                }

                Swap(ref frontBuffer, ref backBuffer);
                CachedTripwires = frontBuffer;
            }
        }

        private static void Swap(ref List<TripwireData> a, ref List<TripwireData> b)
        {
            var t = a; a = b; b = t;
        }
    }
}
