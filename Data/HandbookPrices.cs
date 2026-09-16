using BepInEx;
using Oracle.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oracle.Data
{
    /// <summary>
    /// 手册价格表（TemplateId → 卢布价格）
    ///
    /// IL2CPP 移植说明：
    ///
    /// 1. 【数据源改为直接读文件】
    ///    旧版走 HTTP：SPT.Common.Http.RequestHandler.PostJson("/client/handbook/templates", ...)。
    ///    在 SPT5 中 SPT.Common 已不存在（改由 SPTushonka 提供），但该路由仍在服务端。
    ///    这里改为直接读取 SPT_Data 下的 handbook.json —— **它与服务端提供的是同一份数据**
    ///    （服务端就是读它再序列化出去的），因此结果等价，同时免除了：
    ///      · 对 SPTushonka 内部程序集的编译期依赖（那会让插件在 SPT 重构后直接加载失败）
    ///      · 网络时序问题（本插件先于 SPTushonka 加载，此时 HTTP 客户端尚未初始化）
    ///
    /// 2. 【JSON 解析用 System.Text.Json】
    ///    interop 版的 Newtonsoft 无法反序列化托管 DTO（JsonPropertyAttribute 不是可用特性类，
    ///    编译期报 CS0616）。System.Text.Json 属 .NET 6 共享框架，零额外依赖。
    ///
    /// 3. 【失败不影响功能】
    ///    加载失败时价格表为空，LootESP 自动退化为「只显示名称与距离、不分级着色」，
    ///    而不是崩溃或整体失效。
    /// </summary>
    public static class HandbookPrices
    {
        private static readonly Dictionary<string, int> _prices = new Dictionary<string, int>(8192);

        private static bool _loadAttempted;
        private static bool _loaded;

        /// <summary>价格表是否可用</summary>
        public static bool IsLoaded => _loaded;

        /// <summary>已加载的条目数</summary>
        public static int Count => _prices.Count;

        /// <summary>候选路径：SPT_Runtime 目录名在不同打包方式下可能不同，逐个尝试</summary>
        private static string[] CandidatePaths()
        {
            // 全限定名：本工程引用了约 170 个 interop 程序集，Paths 这类通用名存在重名风险
            string root = BepInEx.Paths.GameRootPath;
            return new[]
            {
                Path.Combine(root, "SPT_Runtime", "SPT_Data", "database", "templates", "handbook.json"),
                Path.Combine(root, "SPT_Data", "database", "templates", "handbook.json"),
                Path.Combine(root, "user", "mods", "SPT_Data", "database", "templates", "handbook.json"),
            };
        }

        /// <summary>
        /// 确保价格表已尝试加载（幂等，失败也不会重试）。
        /// </summary>
        public static void EnsureLoaded()
        {
            if (_loadAttempted) return;
            _loadAttempted = true;

            foreach (string path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;

                    string json = File.ReadAllText(path);
                    var dto = JsonSerializer.Deserialize<HandbookFileDto>(json, JsonOptions);
                    if (dto?.Items == null || dto.Items.Count == 0) continue;

                    int added = 0;
                    foreach (var item in dto.Items)
                    {
                        if (string.IsNullOrEmpty(item.Id)) continue;
                        _prices[item.Id] = item.Price;
                        added++;
                    }

                    _loaded = added > 0;
                    OracleLog.Once("handbook_loaded",
                        $"[Oracle] 价格表已加载: {added} 条，来源 {path}");
                    return;
                }
                catch (Exception ex)
                {
                    OracleLog.Throttled("handbook_path_failed",
                        $"[Oracle] 读取价格表失败 ({path}): {ex.Message}");
                }
            }

            OracleLog.Once("handbook_unavailable",
                "[Oracle] 未能加载价格表 —— 物资透视将退化为「只显示名称与距离」，不做价格分级着色");
        }

        /// <summary>取价格；不存在返回 false</summary>
        public static bool TryGetPrice(string templateId, out int price)
        {
            price = 0;
            if (string.IsNullOrEmpty(templateId)) return false;
            return _prices.TryGetValue(templateId, out price);
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        private sealed class HandbookFileDto
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
