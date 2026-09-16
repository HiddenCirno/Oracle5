using System;
using System.IO;

namespace Oracle.Utils
{
    /// <summary>
    /// 插件路径解析。
    ///
    /// 旧版直接使用 Assembly.GetExecutingAssembly().Location —— 在 IL2CPP 下，
    /// BepInEx 6 的 BasePlugin 并不提供 Info.Location 属性（与 BepInEx 5 不同），
    /// 而插件程序集本身仍由 CoreCLR 从磁盘加载，所以 Assembly.Location 是可用的，
    /// 但仍保留回退路径，避免返回空串导致 locales/ 等目录静默失效。
    /// </summary>
    public static class OraclePaths
    {
        private static string _pluginDir;

        /// <summary>插件自身所在目录（locales/、crosshairs/、itemsaves/ 的父目录）</summary>
        public static string PluginDir
        {
            get
            {
                if (_pluginDir != null) return _pluginDir;
                _pluginDir = ResolvePluginDir();
                return _pluginDir;
            }
        }

        private static string ResolvePluginDir()
        {
            // 首选：插件程序集自身的位置
            try
            {
                string location = typeof(OraclePaths).Assembly.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    string dir = Path.GetDirectoryName(location);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
            }
            catch
            {
                // 某些加载上下文下 Location 会抛异常，落到回退分支
            }

            // 回退：BepInEx 插件根目录下的 Oracle 子目录
            try
            {
                return Path.Combine(BepInEx.Paths.PluginPath, "Oracle");
            }
            catch
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }
    }
}
