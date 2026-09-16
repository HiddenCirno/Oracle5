using EFT.SynchronizableObjects;
using Oracle.ESP;
using Oracle.Utils;
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

        /// <summary>扫描协程</summary>
        public static IEnumerator TripwireScannerCoroutine()
        {
            var frontBuffer = new List<TripwireData>(64);
            var backBuffer = new List<TripwireData>(64);
            CachedTripwires = frontBuffer;

            while (true)
            {
                yield return new WaitForSeconds(ScanInterval);

                if (!OracleGameState.InRaid || !TripwireESPCfg.EnableTripwireESP.Value)
                {
                    backBuffer.Clear();
                    Swap(ref frontBuffer, ref backBuffer);
                    CachedTripwires = frontBuffer;
                    continue;
                }

                backBuffer.Clear();

                // 绊雷数量少，且 FindObjectsOfType 本身较慢，无需分帧
                var found = Object.FindObjectsOfType<TripwireProceduralMesh>();
                var tripwires = OracleCollections.ToManagedList(found);

                for (int i = 0; i < tripwires.Count; i++)
                {
                    var tripwire = tripwires[i];
                    if (tripwire == null) continue;

                    var go = tripwire.gameObject;
                    if (go == null || !go.activeSelf) continue;

                    Vector3 start = tripwire._fromPositionPivot;
                    Vector3 end = tripwire._toPositionPivot;

                    backBuffer.Add(new TripwireData
                    {
                        StartPos = start,
                        EndPos = end,
                        CenterPos = (start + end) * 0.5f,
                        OverlayLabel = "text_esp_overlay_tripwire_label".i18n()
                    });
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
