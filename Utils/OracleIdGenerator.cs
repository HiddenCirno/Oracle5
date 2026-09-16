using System;
using System.Security.Cryptography;
using System.Text;

namespace Oracle.Utils
{
    /// <summary>
    /// 物品 ID 生成工具。
    ///
    /// 用途：复制物品/弹药时需要给新实例一个合法的 MongoId 格式 ID（24 位十六进制）。
    /// 做法是对「原 ID + 盐」取 SHA256 后截取前 12 字节转十六进制 ——
    /// 同一个盐能稳定复现同一批 ID，便于批量复制时保持一致。
    ///
    /// 说明：SHA256 实例按线程缓存。复制弹药可能在同一帧内被调用多次，
    /// 每次都新建实例会造成不必要的开销。
    /// </summary>
    public static class OracleIdGenerator
    {
        [ThreadStatic]
        private static SHA256 _sha256;

        private static readonly char[] HexLookup = "0123456789abcdef".ToCharArray();

        /// <summary>基于原 ID 与盐生成 24 位十六进制 ID</summary>
        public static string GenerateSafeHexId(string originalId, string salt)
        {
            if (_sha256 == null) _sha256 = SHA256.Create();

            string input = (originalId ?? string.Empty) + (salt ?? string.Empty);
            byte[] hashBytes = _sha256.ComputeHash(Encoding.UTF8.GetBytes(input));

            char[] hexBuffer = new char[24];
            for (int i = 0; i < 12; i++)
            {
                byte b = hashBytes[i];
                hexBuffer[i * 2] = HexLookup[b >> 4];
                hexBuffer[i * 2 + 1] = HexLookup[b & 0x0F];
            }
            return new string(hexBuffer);
        }

        /// <summary>生成一个随机盐（时间戳 + GUID）</summary>
        public static string NewSalt()
        {
            return $"{DateTime.Now.Ticks}_{Guid.NewGuid():N}";
        }
    }
}
