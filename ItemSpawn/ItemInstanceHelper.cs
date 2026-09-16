using EFT.InventoryLogic;
using Oracle.Utils;
using System;
using System.Security.Cryptography;
using System.Text;

namespace Oracle.ItemSpawn
{
    /// <summary>
    /// 物品实例辅助工具：ID 重分配与状态清洗。
    ///
    /// ══════════════ IL2CPP 移植的关键改进 ══════════════
    ///
    /// 4.1 (Mono) 版本里，把克隆出来的物品树改成"独立实例"靠的是**反射写私有 backing field**：
    ///     itemType.GetField("&lt;Id&gt;k__BackingField", BindingFlags.NonPublic | ...)
    ///     itemType.GetField("_id", ...)
    ///     PropertyInfo idProp = ...; idProp.SetValue(item, newId)
    ///
    /// 这套写法在 IL2CPP 下是**不可用**的：托管侧的类型是 Il2CppInterop 生成的代理，
    /// 反射拿到的是代理的字段，与原生对象的内存无关，写进去不会有任何效果（且不报错）。
    ///
    /// 但 5.0 的 interop 程序集里，Item 的 backing field **已经被生成为公开可写属性**：
    ///     prop get:Public set:Public string _Id_k__BackingField
    /// （Il2CppInterop 会为每个原生字段生成 NativeFieldInfoPtr_&lt;name&gt; 与同名属性。）
    ///
    /// 因此这里直接赋值即可，**完全不需要反射** —— 既精确又无越界风险。
    /// </summary>
    public static class ItemInstanceHelper
    {
        [ThreadStatic]
        private static SHA256 _sha256;

        private static readonly char[] HexLookup = "0123456789abcdef".ToCharArray();

        /// <summary>
        /// 对物品树重新分配 ID，使其成为独立实例。
        ///
        /// 同一棵树共用同一个盐，因此"从单一实例复制出无数个独立实例"时，
        /// 每个实例内部的相对结构稳定，而实例之间彼此不同。
        /// </summary>
        public static Item ReassignAllIds(this Item clonedItem)
        {
            if (clonedItem == null) return null;

            string operationSalt = $"{Guid.NewGuid():N}-{DateTime.Now.Ticks}";

            // 取整棵树的快照再遍历：GetAllItems 返回的是 Il2Cpp 的惰性序列，
            // 而我们在循环里会改写每个节点的 Id，先落到托管 List 上更稳妥。
            var items = OracleCollections.ToManagedList(clonedItem.GetAllItems());
            if (items == null) return clonedItem;

            foreach (var item in items)
            {
                if (item == null) continue;

                // 自带防御的 ID 读取（历史数据里偶见空 Id）
                string originalId = string.IsNullOrEmpty(item.Id) ? Guid.NewGuid().ToString("N") : item.Id;
                string newSafeId = GenerateSafeHexId(originalId, operationSalt);

                // ★ 5.0 的 backing field 是公开可写属性，直接赋值，无需反射
                item._Id_k__BackingField = newSafeId;
            }

            return clonedItem;
        }

        /// <summary>
        /// 生成符合 MongoId 规范（24 位十六进制）的 ID。
        /// 算法与 4.1 完全一致：对「原 ID + 盐」取 SHA256，截取前 12 字节转十六进制。
        /// </summary>
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

        /// <summary>
        /// 清洗物品状态：耐久、使用次数、故障、带勾标记……
        ///
        /// 遍历整棵物品树，逐节点处理。子弹（弹药）的带勾由游戏内部处理，不额外干预。
        /// </summary>
        /// <param name="clonedItem">物品树根节点</param>
        /// <param name="fir">是否标记为"战局中获得"（带勾）</param>
        public static Item CleanAndResetItem(this Item clonedItem, bool fir)
        {
            if (clonedItem == null) return null;

            var items = OracleCollections.ToManagedList(clonedItem.GetAllItems());
            if (items == null) return clonedItem;

            foreach (var item in items)
            {
                if (item == null) continue;

                // 每个节点都同步带勾状态（而不是只有 root 带勾）
                item.SpawnedInSession = fir;

                // 武器 / 护甲等可维修物品：恢复耐久上限与当前耐久
                if (item.TryGetItemComponent<RepairableComponent>(out var repairable) && repairable != null)
                {
                    repairable.MaxDurability = repairable.TemplateDurability;
                    if (repairable.Durability < repairable.TemplateDurability)
                    {
                        repairable.Durability = repairable.TemplateDurability;
                    }
                }

                // 钥匙 / 钥匙卡：清空使用次数记录
                if (item.TryGetItemComponent<KeyComponent>(out var key) && key != null)
                {
                    if (key.NumberOfUsages > 0)
                    {
                        key.NumberOfUsages = 0;
                    }
                }

                // 医疗物品：恢复 HP 资源
                if (item.TryGetItemComponent<MedKitComponent>(out var medkit) && medkit != null)
                {
                    if (medkit.HpResource < medkit.MaxHpResource)
                    {
                        medkit.HpResource = medkit.MaxHpResource;
                    }
                }

                // 食物 / 饮料：恢复剩余量
                if (item.TryGetItemComponent<FoodDrinkComponent>(out var food) && food != null)
                {
                    if (food.HpPercent < food.MaxResource)
                    {
                        food.HpPercent = food.MaxResource;
                    }
                }

                // 滤毒罐 / 燃料桶等资源类：恢复资源值
                if (item.TryGetItemComponent<ResourceComponent>(out var resource) && resource != null)
                {
                    if (resource.Value < resource.MaxResource)
                    {
                        resource.Value = resource.MaxResource;
                    }
                }

                // 面罩：清除弹孔与裂痕
                if (item.TryGetItemComponent<FaceShieldComponent>(out var faceShield) && faceShield != null)
                {
                    faceShield.Hits = 0;
                    faceShield.HitSeed = 0;
                }

                // 武器：清除故障状态
                //
                // ⚠ 5.0 的 Weapon.MalfState 是**只读属性**（返回 MalfunctionState 对象），
                //   但该对象自身暴露了公开可写的 `_state` 字段属性，
                //   因此这里改的是 _state 而不是 MalfState.State。
                if (item is Weapon weapon && weapon.MalfState != null)
                {
                    weapon.MalfState._state = Weapon.EMalfunctionState.None;
                }

                // 维修包：重新充能
                if (item.TryGetItemComponent<RepairKitComponent>(out var repairKit) && repairKit != null)
                {
                    var template = repairKit._template;
                    if (template != null && repairKit.Resource < template.MaxRepairResource)
                    {
                        repairKit.Resource = template.MaxRepairResource;
                    }
                }
            }

            return clonedItem;
        }
    }
}
