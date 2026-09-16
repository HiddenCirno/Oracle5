using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Oracle.Utils
{
    /// <summary>
    /// Il2Cpp 集合访问辅助。
    ///
    /// ⚠ 为什么需要这一层：
    ///
    /// 1. 【两种 List 不能混用】
    ///    System.Collections.Generic.List&lt;T&gt; 与
    ///    Il2CppSystem.Collections.Generic.List&lt;T&gt; 是**完全不同的类型**，
    ///    不能互转、不能互相赋值，泛型推断也不会自动跨过去。
    ///    GameWorld.AllAlivePlayersList 等游戏集合都是 Il2Cpp 版，
    ///    因此下面所有签名都写成**全限定名**，避免 using 带来的歧义。
    ///
    /// 2. 【活集合】游戏集合随时可能在别处增删元素。用 foreach 走 Il2Cpp 枚举器
    ///    既要装箱、又容易在集合变动时抛异常，所以对这类集合统一采用
    ///    「索引 + Count 快照」，并显式做越界防御。
    ///
    /// 3. 【枚举器可达性】实测 interop 中：
    ///      Il2CppSystem.Collections.Generic.IEnumerator&lt;T&gt;  有公开属性 Current
    ///      Il2CppSystem.Collections.IEnumerator              有公开方法 MoveNext()
    ///    泛型接口从非泛型接口继承 MoveNext，故如下写法可用：
    ///      var e = src.GetEnumerator();
    ///      while (e.MoveNext()) { var cur = e.Current; }
    ///
    /// 4. 【Il2Cpp 数组】Il2CppArrayBase&lt;T&gt; 提供 Length / Count / 索引器，
    ///    可直接按下标搬运，不需要绕枚举器。
    /// </summary>
    public static class OracleCollections
    {
        // ─────────────── Il2Cpp List<T> ───────────────

        /// <summary>安全取元素：越界或集合为空时返回 null，不抛异常</summary>
        public static T SafeGet<T>(Il2CppSystem.Collections.Generic.List<T> list, int index)
            where T : Il2CppSystem.Object
        {
            if (list == null) return null;
            if (index < 0 || index >= list.Count) return null;
            return list[index];
        }

        /// <summary>取元素数量；集合为空时返回 0</summary>
        public static int SafeCount<T>(Il2CppSystem.Collections.Generic.List<T> list)
            where T : Il2CppSystem.Object
        {
            if (list == null) return 0;
            return list.Count;
        }

        // ─────────────── 物化到托管 List ───────────────

        /// <summary>
        /// 把 Il2Cpp 的 IEnumerable&lt;T&gt; 物化成托管 List&lt;T&gt;。
        /// 用于 GetAllItems() 这类返回 Il2Cpp 泛型序列的 API。
        /// </summary>
        public static System.Collections.Generic.List<T> ToManagedList<T>(
            Il2CppSystem.Collections.Generic.IEnumerable<T> src) where T : Il2CppSystem.Object
        {
            var result = new System.Collections.Generic.List<T>();
            if (src == null) return result;

            var e = src.GetEnumerator();
            if (e == null) return result;

            while (e.MoveNext())
            {
                T cur = e.Current;   // Current 是泛型接口上的公开属性
                if (cur != null) result.Add(cur);
            }
            return result;
        }

        /// <summary>
        /// 把 Il2Cpp 数组物化成托管 List&lt;T&gt;。
        /// 用于 Object.FindObjectsOfType&lt;T&gt;() 与模板的 Grids 等返回值。
        /// </summary>
        public static System.Collections.Generic.List<T> ToManagedList<T>(Il2CppArrayBase<T> src)
            where T : Il2CppSystem.Object
        {
            var result = new System.Collections.Generic.List<T>();
            if (src == null) return result;

            int n = src.Length;
            for (int i = 0; i < n; i++)
            {
                T v = src[i];
                if (v != null) result.Add(v);
            }
            return result;
        }

        // ─────────────── 托管 List 辅助 ───────────────

        /// <summary>在托管 List 中查找首个满足条件的元素；找不到返回 null</summary>
        public static T FirstOrDefault<T>(System.Collections.Generic.List<T> list,
            System.Predicate<T> match)
        {
            if (list == null || match == null) return default;
            for (int i = 0; i < list.Count; i++)
            {
                if (match(list[i])) return list[i];
            }
            return default;
        }
    }
}
