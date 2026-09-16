using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Oracle.Data
{
    /// <summary>
    /// 本地化文本结构定义（locales/*.json 的反序列化目标）
    ///
    /// ⚠ IL2CPP 移植要点：这里刻意使用 System.Text.Json 而非 Newtonsoft.Json。
    ///   BepInEx\interop\Newtonsoft.Json.dll 是 Il2CppInterop 生成的【代理程序集】，
    ///   其中的 JsonPropertyAttribute 并不是可用的 .NET 特性类（编译期报 CS0616），
    ///   且 Il2Cpp 版 JsonConvert 无法反序列化【托管】DTO。
    ///   System.Text.Json 属于 .NET 6 共享框架，运行时由 I:\build\dotnet 提供，零额外依赖。
    /// </summary>
    public class LocaleData
    {
        [JsonPropertyName("Language")]
        public string Language { get; set; }

        [JsonPropertyName("Translate")]
        public Dictionary<string, string> Translate { get; set; }
    }
}
