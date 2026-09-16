using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oracle.Utils;
using System;
using System.Collections.Generic;

namespace Oracle.ItemSpawn
{
    /// <summary>
    /// 预设存档读写：物品树 ↔ 扁平物品 JSON（与 4.1 存档格式完全兼容）。
    ///
    /// ══════════════ 为什么不用游戏自带的 JSON 助手 ══════════════
    ///
    /// 4.1 用的是 SPT 客户端扩展：
    ///     var flat = json.ParseJsonTo&lt;JsonType.FlatItem[]&gt;();
    ///     string text = flatItems.ToPrettyJson();
    ///
    /// 5.0 中 `JsonExtensions`（全局命名空间，游戏自有）确实提供了同名方法，
    /// 但它们是**泛型方法**，Il2CppInterop 对泛型方法的处理是
    /// `MethodInfoStoreGeneric_*` 模式 —— 需要按泛型实参去原生侧查方法指针，
    /// 而该实参若未被 AOT 提前实例化，取到的指针就是空，
    /// `il2cpp_runtime_invoke` 的表现是**进程级崩溃，而不是可捕获的异常**。
    ///
    /// 因此这里刻意绕开所有泛型入口，只用**非泛型**的具体类型：
    ///   · 写：手工拼 JArray/JObject，再交给 JToken.ToString() 序列化
    ///   · 读：JArray.Parse() 解析后，手工构造 JsonType.FlatItem
    /// 唯一依赖的游戏入口是 ItemFactory 的两个非泛型方法：
    ///   TreeToFlatItems(IEnumerable&lt;Item&gt;) 与 FlatItemsToTree(FlatItem[], bool, dict)
    ///
    /// ── 关于 location / upd ──
    /// 这两个字段是 UnparsedData，内部只包了一个 JToken。它的读写由游戏的
    /// UnparsedDataConverter 负责（把 JToken 原样内联进 JSON）。
    /// 这里等价地手工处理：写出时直接内联 JToken，读入时把它塞回 UnparsedData。
    /// 因此产出的 JSON 与 4.1 的存档逐字段一致（可互相读取）。
    /// </summary>
    public static class OracleItemPresetIO
    {
        // ══════════════════ 写：物品表 → JSON ══════════════════

        /// <summary>
        /// 把物品表序列化成扁平物品 JSON。失败返回 "[]"（空存档，而不是 null）。
        /// </summary>
        public static string Serialize(List<Item> items)
        {
            if (items == null || items.Count == 0) return "[]";

            var factory = Singleton<ItemFactory>.Instance;
            if (factory == null) return "[]";

            // 用 Il2Cpp 的 List 承载根物品 —— 对应 TreeToFlatItems 的
            // IEnumerable<Item> 重载。刻意不走 Il2CppReferenceArray/Item[] 那两个重载：
            // 它们在元数据里是同一个 C# 签名，交给编译器重载解析容易出歧义。
            var roots = new Il2CppSystem.Collections.Generic.List<Item>();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null) roots.Add(items[i]);
            }
            if (roots.Count == 0) return "[]";

            var flatItems = factory.TreeToFlatItems(roots);
            if (flatItems == null) return "[]";

            var array = new JArray();
            for (int i = 0; i < flatItems.Length; i++)
            {
                JsonType.FlatItem flat = flatItems[i];
                if (flat == null) continue;
                array.Add(FlatItemToJson(flat));
            }

            // JToken.ToString() 默认就是 Indented —— 便于玩家手工编辑存档
            return array.ToString();
        }

        /// <summary>单个扁平物品 → JObject（字段名与 4.1 存档一致）</summary>
        private static JObject FlatItemToJson(JsonType.FlatItem flat)
        {
            var obj = new JObject();

            // MongoID 是值类型包装，ToString() 得到 24 位十六进制串
            obj.Add("_id", new JValue(flat._id.ToString()));
            obj.Add("_tpl", new JValue(flat._tpl.ToString()));

            // 根物品没有 parentId —— 正是靠"缺这个字段"来识别根节点，故此处必须省略而非写 null。
            //
            // ⚠ 必须读 StringParentId（string），绝不能读 parentId（Nullable<MongoID>）。
            //
            //   实机崩溃栈：
            //     NullReferenceException
            //       at Il2CppObjectBase.CreateGCHandle(IntPtr)
            //       at Il2CppSystem.Nullable`1..ctor(IntPtr pointer)
            //       at JsonType.FlatItem.get_parentId()      ← 就是这一句
            //
            //   原因：Il2CppInterop 为「值类型属性」生成的 getter 会从原生指针重建包装对象，
            //   而 Nullable<MongoID> 在没有值（根节点）时底层指针为空 →
            //   构造包装时 CreateGCHandle 抛空引用。
            //   即：只要遇到根节点就必然抛异常 —— 存档里一定全是根节点，所以存档功能 100% 失效。
            //   （同一行的 _id / _tpl 是普通值类型 MongoID，读取正常，问题只在 Nullable 上。）
            //
            //   StringParentId 是游戏自带的 string 访问器，原生侧自行处理"有无值"，
            //   无值时返回 null/空串，不会构造任何包装对象 —— 这才是安全的读法。
            string parentId = flat.StringParentId;
            if (!string.IsNullOrEmpty(parentId))
            {
                obj.Add("parentId", new JValue(parentId));
            }

            string slotId = flat.slotId;
            if (!string.IsNullOrEmpty(slotId)) obj.Add("slotId", new JValue(slotId));

            // location / upd：UnparsedData 内联其 JToken（等价于 UnparsedDataConverter 的写出行为）
            UnparsedData location = flat.location;
            if (location != null && location.JToken != null)
            {
                obj.Add("location", location.JToken);
            }

            UnparsedData upd = flat.upd;
            if (upd != null && upd.JToken != null)
            {
                obj.Add("upd", upd.JToken);
            }

            return obj;
        }

        // ══════════════════ 读：JSON → 物品表 ══════════════════

        /// <summary>
        /// 解析扁平物品 JSON 并还原成物品树（只返回根物品，子节点已挂在树上）。
        ///
        /// 解析失败抛异常，由调用方（GUI）负责提示 —— 这里不吞异常，
        /// 否则玩家会看到"载入成功但列表是空的"这种无法排查的现象。
        /// </summary>
        public static List<Item> Deserialize(string json)
        {
            var loaded = new List<Item>();
            if (string.IsNullOrEmpty(json)) return loaded;

            var factory = Singleton<ItemFactory>.Instance;
            if (factory == null) return loaded;

            // JArray.Parse 是**非泛型**静态方法，可安全直接调用
            JArray array = JArray.Parse(json);
            if (array == null || array.Count == 0) return loaded;

            // ⚠ 用 List 收集再 ToArray，**不能**按 array.Count 预分配定长数组：
            //   跳过损坏条目会留下 null 槽位，原生 FlatItemsToTree 会直接解引用它。
            var flatItems = new List<JsonType.FlatItem>(array.Count);
            var rootIds = new List<string>(array.Count);
            int skipped = 0;

            for (int i = 0; i < array.Count; i++)
            {
                JToken token = array[i];
                if (token == null || token.Type != JTokenType.Object) { skipped++; continue; }

                JsonType.FlatItem flat = JsonToFlatItem(token);
                if (flat == null) { skipped++; continue; }

                flatItems.Add(flat);

                // 没有 parentId 的就是根节点（与 4.1 的筛选规则一致）
                JToken parentToken = token["parentId"];
                if (parentToken == null || parentToken.Type == JTokenType.Null)
                {
                    rootIds.Add(flat._id.ToString());
                }
            }

            if (skipped > 0)
            {
                OracleLog.Warning($"[Oracle] 预设中有 {skipped} 条物品记录无法解析，已跳过");
            }

            var referenceArray =
                new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<JsonType.FlatItem>(flatItems.ToArray());

            // 第三参 preexistingItems 传 null：全新解析，不与既有实例复用
            var result = factory.FlatItemsToTree(referenceArray, false, null);

            if (result.DeserializationErrors != null && result.DeserializationErrors.Count > 0)
            {
                OracleLog.Warning($"[Oracle] 预设载入有 {result.DeserializationErrors.Count} 条反序列化错误（部分物品可能缺失）");
            }

            var items = result.Items;
            if (items == null) return loaded;

            foreach (string id in rootIds)
            {
                if (!items.ContainsKey(id)) continue;

                Item item = items[id];
                // 仓库本身不是"可生成物品"，4.1 同样排除
                if (item == null || item is Stash) continue;

                loaded.Add(item);
            }

            return loaded;
        }

        /// <summary>
        /// 单个 JSON 对象 → 扁平物品。
        ///
        /// ⚠ 参数刻意用 JToken 而非 JObject，并通过索引器取字段 ——
        ///   不要改写成 `token.Cast&lt;JObject&gt;()`：
        ///   JToken 实现了 IEnumerable&lt;JToken&gt;，那个写法有被解析成
        ///   LINQ `Enumerable.Cast` 的风险（它会去枚举子元素再逐个转型，
        ///   对 `{"_id": "..."}` 这种对象会直接抛转型失败）。
        ///   索引器没有这层歧义。
        /// </summary>
        private static JsonType.FlatItem JsonToFlatItem(JToken obj)
        {
            string id = TokenToString(obj["_id"]);
            string tpl = TokenToString(obj["_tpl"]);

            // 这两个是必填项，缺了说明存档损坏 —— 直接跳过该项，不构造半成品
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(tpl)) return null;

            var flat = new JsonType.FlatItem();
            flat._id = new MongoID(id);
            flat._tpl = new MongoID(tpl);

            string parentId = TokenToString(obj["parentId"]);
            if (!string.IsNullOrEmpty(parentId))
            {
                // ⚠ Il2CppSystem.Nullable<T> 在 interop 侧是引用类型：
                //   传 null 能编译但运行时崩溃，必须显式构造。
                flat.parentId = new Il2CppSystem.Nullable<MongoID>(new MongoID(parentId));
            }

            string slotId = TokenToString(obj["slotId"]);
            if (!string.IsNullOrEmpty(slotId)) flat.slotId = slotId;

            JToken locationToken = obj["location"];
            if (locationToken != null && locationToken.Type != JTokenType.Null)
            {
                flat.location = new UnparsedData { JToken = locationToken };
            }

            JToken updToken = obj["upd"];
            if (updToken != null && updToken.Type != JTokenType.Null)
            {
                flat.upd = new UnparsedData { JToken = updToken };
            }

            return flat;
        }

        /// <summary>
        /// JToken → 字符串。用 ToString() 而非显式转换运算符：
        /// 对 JValue 而言它返回不带引号的原始值，且不依赖 interop 是否正确生成了转换运算符。
        /// </summary>
        private static string TokenToString(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            return token.ToString();
        }
    }
}
