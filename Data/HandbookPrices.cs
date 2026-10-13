using Oracle.Utils;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oracle.Data
{
    /// <summary>
    /// 手册价格表（TemplateId → 卢布价格）
    ///
    /// ══════════════ 数据源：必须走路由，不能读文件 ══════════════
    ///
    /// 4.1 走的是 `SPT.Common.Http.RequestHandler.PostJson("/client/handbook/templates", ...)`，
    /// 5.0 里对应物是 `SPTushonka.Common.Http.RequestHandler`（同名同形），此处沿用它。
    ///
    /// ⚠⚠ 移植期曾经把它改成"直接读 SPT_Data/database/templates/handbook.json"，
    ///     理由是"服务端就是读它再序列化出去的，两者等价" —— **这个前提对模组物品不成立**。
    ///
    ///     磁盘上那份是**原版快照**（实测 5215 条，与火神重工 500 件物品的交集为 0）；
    ///     模组物品是**服务端运行时**才注册进 handbook 的，只存在于这条路由返回的那份里。
    ///
    ///     代价是一条完全静默的失效链：
    ///         模组物品 TryGetPrice 失败 → Price = 0
    ///         → GetItemLevel 回退到价格分级 → Level = 1
    ///         → TryAddLootData 的双重过滤 (Price < 门槛 && Level < 分级) 命中
    ///         → **物品被直接丢弃，没有任何日志**
    ///
    ///     症状是"透视里看不到模组物品"，与"模组物品没刷出来"完全无法区分 ——
    ///     曾因此误判成服务端战利品生成的问题，白查了一整轮。
    ///     ★ 教训：判据失败时要能区分"我没有它的数据"和"它的数据是 0"。
    ///
    /// 5.0 的 /client/handbook/templates 直接把 templateTable.Handbook 原样流式返回
    /// （不再套 {err, errmsg, data} 外壳），所以响应形状与那份文件一致，解析逻辑无需改动。
    ///
    /// 依赖代价（已知且接受）：这是 Oracle 唯一的 SPTushonka 引用，会让本插件被
    /// SPTushonka.PrePatch/Tushonka/StartupChecks 的版本门禁覆盖 ——
    /// 引用的 major.minor 与当前不符即拒绝启动，**SPTushonka 升 minor 时必须重编 Oracle**。
    ///
    /// 加载失败时价格表为空，LootESP 退化为「只显示名称与距离、不分级着色」，
    /// 不崩溃、不阻塞进图。
    /// </summary>
    public static class HandbookPrices
    {
        private static readonly Dictionary<string, int> _prices = new Dictionary<string, int>(16384);

        private static bool _loadAttempted;
        private static bool _loaded;

        /// <summary>价格表是否可用</summary>
        public static bool IsLoaded => _loaded;

        /// <summary>已加载的条目数</summary>
        public static int Count => _prices.Count;

        /// <summary>原版手册路由（与 4.1 完全一致）</summary>
        private const string HandbookRoute = "/client/handbook/templates";

        /// <summary>
        /// 确保价格表已尝试加载（幂等，失败也不会重试）。
        /// ⚠ 这是一次同步 HTTP 调用，只在扫描器启动时执行一次；
        ///   绝不要放到每帧或每轮扫描里。
        /// </summary>
        public static void EnsureLoaded()
        {
            if (_loadAttempted) return;
            _loadAttempted = true;

            try
            {
                // 请求体走 EmptyRequestData，服务端不读它；与 4.1 一样只需要路由本身
                string json = SPTushonka.Common.Http.RequestHandler.PostJson(HandbookRoute, "{}");

                if (string.IsNullOrWhiteSpace(json))
                {
                    OracleLog.Once("handbook_empty",
                        "[Oracle] 手册路由返回空 —— 物资透视将退化为「只显示名称与距离」，不做价格分级着色");
                    return;
                }

                var dto = JsonSerializer.Deserialize<HandbookResponseDto>(json, JsonOptions);
                if (dto?.Data?.Items == null || dto.Data.Items.Count == 0)
                {
                    // 形状再变时把响应片段打出来，免得又一次靠猜
                    string head = json.Length > 300 ? json.Substring(0, 300) : json;
                    OracleLog.Once("handbook_no_items",
                        $"[Oracle] 手册路由未返回物品条目 —— 价格分级不可用。响应前 300 字符: {head}");
                    return;
                }

                int added = 0;
                foreach (var item in dto.Data.Items)
                {
                    if (string.IsNullOrEmpty(item.Id)) continue;
                    _prices[item.Id] = item.Price;
                    added++;
                }

                _loaded = added > 0;
                OracleLog.Once("handbook_loaded",
                    $"[Oracle] 价格表已加载: {added} 条，来源 路由 {HandbookRoute}");
            }
            catch (Exception ex)
            {
                OracleLog.Once("handbook_unavailable",
                    $"[Oracle] 手册路由调用失败 ({HandbookRoute}): {ex.GetType().Name} {ex.Message} " +
                    "—— 物资透视将退化为「只显示名称与距离」，不做价格分级着色");
            }
        }

        /// <summary>取价格；不存在返回 false</summary>
        public static bool TryGetPrice(string templateId, out int price)
        {
            price = 0;
            if (string.IsNullOrEmpty(templateId)) return false;
            return _prices.TryGetValue(templateId, out price);
        }

        /// <summary>价格表里有没有这条 —— 供调用方区分「没数据」与「数据为 0」</summary>
        public static bool HasPrice(string templateId)
        {
            return !string.IsNullOrEmpty(templateId) && _prices.ContainsKey(templateId);
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        // ⚠ 服务端 HttpResponseUtil.GetStreamedBody 会包一层 GetBodyResponseData<T>：
        //     { err, errmsg, data: { Categories, Items } } —— 与 4.1 的 HandbookResponse 同形。
        //     （别只看 Callback 里 return 的 templateTable.Handbook，那还没经过 GetStreamedBody）
        private sealed class HandbookResponseDto
        {
            [JsonPropertyName("data")]
            public HandbookDataDto Data { get; set; }
        }

        private sealed class HandbookDataDto
        {
            [JsonPropertyName("Items")]
            public List<HandbookItemDto> Items { get; set; }
        }

        private sealed class HandbookItemDto
        {
            [JsonPropertyName("Id")]
            public string Id { get; set; }

            [JsonPropertyName("Price")]
            public int Price { get; set; }
        }
    }
}
