using BepInEx.Configuration;
using Oracle.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Oracle.Utils
{
    /// <summary>
    /// 本地化管理器
    /// </summary>
    public static class LocaleManager
    {
        /// <summary>
        /// 当前语言
        /// </summary>
        public static ConfigEntry<string> CurrentLanguage;

        /// <summary>
        /// 内存中的语言列表
        /// </summary>
        private static readonly Dictionary<string, Dictionary<string, string>> _loadedTranslations = new Dictionary<string, Dictionary<string, string>>();

        /// <summary>
        /// Fallback语言
        /// </summary>
        private const string FallbackLangName = "English";

        public static void Initialize(ConfigFile config)
        {
            //扫描指定目录的所有语言文件
            string dirPath = Path.Combine(OraclePaths.PluginDir, "locales");

            _loadedTranslations.Clear();
            List<string> availableLanguages = new List<string>();

            if (Directory.Exists(dirPath))
            {
                string[] jsonFiles = Directory.GetFiles(dirPath, "*.json");
                foreach (string file in jsonFiles)
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        LocaleData data = JsonSerializer.Deserialize<LocaleData>(json);

                        if (data != null && !string.IsNullOrEmpty(data.Language) && data.Translate != null)
                        {
                            _loadedTranslations[data.Language] = data.Translate;
                            availableLanguages.Add(data.Language);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"[Oracle] 语言文件 ({file}) 加载错误! \n{e.Message}");
                    }
                }
            }
            else
            {
                Console.WriteLine($"[Oracle] 语言目录不存在: {dirPath}");
            }

            //Fallback
            if (availableLanguages.Count == 0)
            {
                availableLanguages.Add(FallbackLangName);
                _loadedTranslations[FallbackLangName] = new Dictionary<string, string>();
            }

            //Cfg初始化
            CurrentLanguage = config.Bind(
                "Language / 语言",
                "Language Opinion / 语言设置",
                availableLanguages.Contains(FallbackLangName) ? FallbackLangName : availableLanguages[0],
                new ConfigDescription(
                    "Change language (Configuration menu's requires game restart). / 更改语言（F12菜单需要重启游戏生效）。",
                    new AcceptableValueList<string>(availableLanguages.ToArray())
                ));
        }

        /// <summary>
        /// 获取本地化
        /// </summary>
        public static string Get(string key)
        {
            if (CurrentLanguage != null &&
                _loadedTranslations.TryGetValue(CurrentLanguage.Value, out var currentDict))
            {
                if (currentDict.TryGetValue(key, out var text)) return text;
            }

            if (_loadedTranslations.TryGetValue(FallbackLangName, out var fallbackDict))
            {
                if (fallbackDict.TryGetValue(key, out var fallbackText)) return fallbackText;
            }

            return key;
        }

        /// <summary>
        /// 拓展语法糖
        /// </summary>
        public static string i18n(this string key)
        {
            return Get(key);
        }
    }
}
