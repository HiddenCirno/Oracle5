using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using Oracle.Data;
using Oracle.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oracle.RaidManager
{
    /// <summary>
    /// 技能与专精面板（创世引擎的页签之一）。
    ///
    /// 两个子页签：
    ///   · 技能：列出全部技能（两列网格），可把选中项直接设到指定等级
    ///   · 专精：列出全部武器专精，可一键清零 / 拉到 2 级 / 3 级
    ///
    /// ══════════════ 与 4.1 的三处差异 ══════════════
    ///
    /// 1. 子页签不再用 GUILayout.Toolbar。
    ///    Toolbar 内部会调 GUIContent.Temp(Il2CppStringArray)，而该方法体在 IL2CPP
    ///    构建中被裁剪 —— 实机抛 System.NotSupportedException: Method unstripping failed，
    ///    且异常会中断整个绘制。改用 RaidManagerGUI.DrawTabBar（逐个绘制的按钮组）。
    ///
    /// 2. 修正一处 4.1 的文案键笔误：
    ///    4.1 写的是 "text_tab_skill_manager_no_result"，locales 里实际是
    ///    "text_skill_manager_no_result"（无 tab_ 段）。
    ///    —— 同样的笔误在 AI 面板也出现过一次，看来是当时的普遍手误。
    ///
    /// 3. 技能容器类型：5.0 的 player.Skills 返回 EFT.SkillManager，
    ///    其 Skills 是 Il2CppReferenceArray&lt;Skill&gt;（用 .Length），
    ///    Mastering 是 Dictionary&lt;string, Mastering&gt;（键是 string，不是 MongoID）。
    /// </summary>
    public class SkillManagerGUI
    {
        private readonly Dictionary<string, Texture2D> _masteringIconCache = new Dictionary<string, Texture2D>();

        private Vector2 _scrollPos;
        private int _selectedSubTab = 0;

        private readonly string[] _tabKeys =
        {
            "text_skill_manager_skill_title",
            "text_skill_manager_mastering_title"
        };
        private string[] _tabs;

        /// <summary>目标等级输入（技能子页签用）</summary>
        private string _targetLevelStr = "51";

        // 直接持有选中项的引用
        private Skill _selectedSkill;
        private Mastering _selectedMastering;

        public void RefreshLocalizedCache()
        {
            if (_tabs == null || _tabs.Length != _tabKeys.Length)
            {
                _tabs = new string[_tabKeys.Length];
            }

            for (int i = 0; i < _tabKeys.Length; i++)
            {
                _tabs[i] = _tabKeys[i].i18n();
            }
        }

        public void DrawPanel()
        {
            if (_tabs == null) RefreshLocalizedCache();

            // 拿不到玩家技能就只给提示 —— 4.1 的"空指针防御"，这里保留
            var skillManager = OracleGameState.LocalPlayer?.Skills;
            if (skillManager == null)
            {
                GUILayout.Label("text_skill_manager_no_result".i18n(), UIStyleManager.BoxStyle);
                return;
            }

            // ── 子页签 ──
            _selectedSubTab = RaidManagerGUI.DrawTabBar(_selectedSubTab, _tabs);
            GUILayout.Space(10);

            // ── 等级输入（仅技能页签）──
            if (_selectedSubTab == 0)
            {
                GUILayout.BeginVertical(UIStyleManager.BoxStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label("text_skill_manager_skill_level_input".i18n(), GUILayout.Width(110));
                _targetLevelStr = GUILayout.TextField(_targetLevelStr, UIStyleManager.TextFieldStyle, GUILayout.Width(60));
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUILayout.Space(10);
            }

            // ── 列表 ──
            GUIStyle origScroll = GUI.skin.verticalScrollbar;
            GUIStyle origThumb = GUI.skin.verticalScrollbarThumb;
            GUI.skin.verticalScrollbar = UIStyleManager.ScrollbarStyle;
            GUI.skin.verticalScrollbarThumb = UIStyleManager.ScrollbarThumbStyle;

            _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            if (_selectedSubTab == 0)
            {
                DrawSkillGrid(skillManager.Skills);
            }
            else
            {
                DrawMasteringGrid(skillManager.Mastering);
            }

            GUILayout.EndScrollView();

            GUI.skin.verticalScrollbar = origScroll;
            GUI.skin.verticalScrollbarThumb = origThumb;

            // ── 操作按钮 ──
            GUILayout.Space(10);

            if (_selectedSubTab == 0)
            {
                DrawSkillAction();
            }
            else
            {
                DrawMasteringAction();
            }
        }

        // ══════════════════════ 技能页签 ══════════════════════

        private void DrawSkillAction()
        {
            GUI.enabled = _selectedSkill != null;

            // ⚠ 文案在进分组前算好：4.1 这里直接把 string.Format 写在 GUILayout.Button 的参数里，
            //   一旦格式化抛异常就会发生在布局分组内部（虽然此处不在 Begin/End 之间，
            //   但仍以统一提前计算为好）。
            string btnText = "";
            try
            {
                btnText = _selectedSkill != null
                    ? string.Format("text_skill_manager_skill_level_set".i18n(), _selectedSkill.Id.ToString().Localized())
                    : "text_skill_manager_skill_level_select".i18n();
            }
            catch (Exception ex)
            {
                OracleLog.Throttled("skill_text", $"[Oracle] 技能面板文案格式化失败: {ex.Message}");
            }

            if (GUILayout.Button(btnText, UIStyleManager.BlueButtonStyle, GUILayout.Height(40)))
            {
                if (int.TryParse(_targetLevelStr, out int targetLevel))
                {
                    try
                    {
                        // BaseSkill.SetLevel(int) —— 5.0 确认为 public
                        _selectedSkill.SetLevel(targetLevel);
                    }
                    catch (Exception ex)
                    {
                        OracleCommon.ShowError(ex, "SkillManagerGUI.SetLevel");
                    }
                }
                else
                {
                    OracleLog.Warning($"[Oracle] 技能等级输入无效：{_targetLevelStr}");
                }
            }

            GUI.enabled = true;
        }

        private void DrawSkillGrid(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Skill> skills)
        {
            if (skills == null || skills.Length == 0) return;

            const float itemWidth = 245f;

            for (int i = 0; i < skills.Length; i += 2)
            {
                GUILayout.BeginHorizontal();

                DrawSkillItem(skills[i], itemWidth);
                GUILayout.Space(10);

                // 奇数个技能时右列留空，保持两列对齐
                if (i + 1 < skills.Length)
                {
                    DrawSkillItem(skills[i + 1], itemWidth);
                }
                else
                {
                    GUILayout.Space(itemWidth);
                }

                GUILayout.EndHorizontal();
                GUILayout.Space(6);
            }
        }

        private void DrawSkillItem(Skill skill, float width)
        {
            if (skill == null) return;

            bool isSelected = (_selectedSkill != null && _selectedSkill.Pointer == skill.Pointer);

            // ⚠ 文案与图标先算好再进分组（格式化异常会破坏 IMGUI 布局栈）
            string nameLine = "";
            string levelLine = "";
            string noIconText = "";
            try
            {
                nameLine = $"<b>{skill.Id.ToString().Localized()}</b>";
                levelLine = string.Format("text_skill_manager_show_level".i18n(), skill.Level);
                noIconText = "text_item_instance_manager_no_icon".i18n();
            }
            catch (Exception ex)
            {
                OracleLog.Throttled("skill_item_text", $"[Oracle] 技能条目文案失败: {ex.Message}");
            }

            Sprite skillIconSprite = GetSkillSprite(skill);

            GUILayout.BeginHorizontal(
                isSelected ? UIStyleManager.SelectedBoxStyle : UIStyleManager.BoxStyle,
                GUILayout.Width(width),
                GUILayout.Height(80));

            if (skillIconSprite != null)
            {
                // ⚠ 走 sprite 裁剪绘制，不能退化成 Texture2D ——
                //   5.0 的技能图标是图集，画整张贴图会把所有图标平铺出来
                DrawSpriteInLayout(skillIconSprite, 64f, 64f);
            }
            else
            {
                GUILayout.Label(noIconText, GUILayout.Width(64), GUILayout.Height(64));
            }

            GUILayout.BeginVertical();
            GUILayout.Space(8);
            GUILayout.Label(nameLine);
            GUILayout.Label(levelLine);
            GUILayout.EndVertical();

            GUILayout.FlexibleSpace();

            string btnText = isSelected
                ? "text_button_item_instance_manager_selected".i18n()
                : "text_button_item_instance_manager_select".i18n();

            GUIStyle btnStyle = isSelected
                ? UIStyleManager.RedButtonStyle
                : UIStyleManager.BlueButtonStyle;

            if (GUILayout.Button(btnText, btnStyle, GUILayout.Width(90), GUILayout.Height(30)))
            {
                _selectedSkill = isSelected ? null : skill;
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 取技能图标 sprite。
        ///
        /// 4.1 用的是 Dictionary.GetValueOrDefault(skill.Id) —— 那是泛型方法，
        /// 在 IL2CPP 下不可直接调用（泛型实参未被 AOT 实例化会取到空指针，
        /// 属于进程级崩溃而非可捕获异常）。改用非泛型的 TryGetValue。
        ///
        /// ⚠ 返回的是 **Sprite 而不是 Texture2D**，这是刻意的 ——
        ///   5.0 把技能图标打进了图集，必须保留子矩形信息才能正确绘制，详见 DrawSpriteInLayout。
        /// </summary>
        private static Sprite GetSkillSprite(Skill skill)
        {
            try
            {
                var hardSettings = EFTHardSettings.Instance;
                var staticIcons = hardSettings?.StaticIcons;
                var sprites = staticIcons?.SkillIdSprites;
                if (sprites == null) return null;

                Sprite sprite;
                if (sprites.TryGetValue(skill.Id, out sprite))
                {
                    return sprite;
                }
            }
            catch (Exception ex)
            {
                OracleLog.Once("skill_icon_fail", $"[Oracle] 技能图标取用失败: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 在 GUILayout 布局里按 sprite 的子矩形绘制图标。
        ///
        /// ══════════════ 为什么不能直接画 sprite.texture ══════════════
        ///
        /// 实机现象："技能图标在 5.0 变成了精灵图，图标的渲染全炸掉了"。
        ///
        /// 4.1 时代每个技能图标是一张**独立贴图**，画 sprite.texture 就等于画那个图标。
        /// 5.0 把技能图标打进了**图集（atlas）**，此时 sprite.texture 返回的是
        /// **整张图集**（所有图标平铺在一起），直接画它自然是一团乱。
        ///
        /// 正解：用 sprite.textureRect（该 sprite 在图集里的像素矩形）
        /// 归一化成 UV，交给 GUI.DrawTextureWithTexCoords 裁剪绘制。
        ///
        /// 关于 UV 的 Y 轴朝向（这里很容易搞反，已核对 Unity 官方文档示例）：
        ///   DrawTextureWithTexCoords 的 texCoords 用的是**标准贴图 UV 空间，原点在左下**。
        ///   官方文档示例中 texCoords = (0, 0, 0.5, 0.5) 被明确注释为
        ///   "the bottom left half of the texture" —— 与 Sprite.textureRect 同一套约定，
        ///   因此**直接映射即可，不需要翻转 Y**。
        ///
        /// 这个写法对**未打图集**的 sprite 同样正确：它的 textureRect 就等于整张贴图，
        /// 归一化后 UV = (0,0,1,1)，与直接绘制完全等价。
        /// （专精图标走的是物品图标源，目前是独立贴图，所以那边没出问题；
        ///   但这条路径对两种来源都成立，将来物品图标若也打图集，这里可以直接复用。）
        /// </summary>
        private static void DrawSpriteInLayout(Sprite sprite, float width, float height)
        {
            if (sprite == null) return;

            Texture2D tex = sprite.texture;
            if (tex == null) return;

            int texW = tex.width;
            int texH = tex.height;
            if (texW <= 0 || texH <= 0) return;

            // 预留布局空间（与原来 GUILayout.Label(tex, Width, Height) 的占位行为一致）
            Rect drawRect = GUILayoutUtility.GetRect(width, height);

            try
            {
                // ⚠ textureRect 对**紧密打包（tight packing）**的 sprite 会抛异常
                Rect tr = sprite.textureRect;
                if (tr.width <= 0f || tr.height <= 0f) return;

                Rect uv = new Rect(
                    tr.x / texW,
                    tr.y / texH,
                    tr.width / texW,
                    tr.height / texH);

                GUI.DrawTextureWithTexCoords(drawRect, tex, uv);
            }
            catch (Exception ex)
            {
                // 退化方案：画整张贴图。虽然可能是图集的一部分，但至少不是空白，
                // 而且会留下可诊断的日志。
                OracleLog.Throttled("skill_sprite_rect",
                    $"[Oracle] 技能图标 textureRect 取用失败，退化为整图绘制: {ex.Message}");
                GUI.DrawTexture(drawRect, tex, ScaleMode.ScaleToFit);
            }
        }

        // ══════════════════════ 专精页签 ══════════════════════

        private void DrawMasteringAction()
        {
            GUI.enabled = _selectedMastering != null;

            GUILayout.BeginHorizontal();

            string btnText1 = _selectedMastering != null
                ? "text_skill_manager_mastering_level_set_1".i18n()
                : "text_skill_manager_mastering_level_select".i18n();

            if (GUILayout.Button(btnText1, UIStyleManager.RedButtonStyle, GUILayout.Height(40)))
            {
                try { _selectedMastering.SetCurrent(0f, false); }
                catch (Exception ex) { OracleCommon.ShowError(ex, "SkillManagerGUI.MasteringSetZero"); }
            }

            string btnText2 = _selectedMastering != null
                ? "text_skill_manager_mastering_level_set_2".i18n()
                : "text_skill_manager_mastering_level_select".i18n();

            if (GUILayout.Button(btnText2, UIStyleManager.BlueButtonStyle, GUILayout.Height(40)))
            {
                try { _selectedMastering.SetCurrent(_selectedMastering.Lvl1, false); }
                catch (Exception ex) { OracleCommon.ShowError(ex, "SkillManagerGUI.MasteringSetLv2"); }
            }

            string btnText3 = _selectedMastering != null
                ? "text_skill_manager_mastering_level_set_3".i18n()
                : "text_skill_manager_mastering_level_select".i18n();

            if (GUILayout.Button(btnText3, UIStyleManager.BlueButtonStyle, GUILayout.Height(40)))
            {
                try { _selectedMastering.SetCurrent(_selectedMastering.Lvl1 + _selectedMastering.Lvl2, false); }
                catch (Exception ex) { OracleCommon.ShowError(ex, "SkillManagerGUI.MasteringSetLv3"); }
            }

            GUILayout.EndHorizontal();
            GUI.enabled = true;
        }

        private void DrawMasteringGrid(Il2CppSystem.Collections.Generic.Dictionary<string, Mastering> masterings)
        {
            if (masterings == null) return;

            // ⚠ 先把 interop 字典摊平成托管列表再渲染：
            //   直接在 foreach 里穿透 interop 枚举器，一旦中途抛异常会连着布局一起坏掉。
            List<Mastering> list;
            try
            {
                list = new List<Mastering>();
                foreach (var kv in masterings)
                {
                    if (kv.Value != null) list.Add(kv.Value);
                }
            }
            catch (Exception ex)
            {
                OracleLog.Throttled("mastering_enum", $"[Oracle] 专精列表枚举失败: {ex.Message}");
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                DrawMasteringItem(list[i]);
                GUILayout.Space(6);
            }
        }

        private void DrawMasteringItem(Mastering mastering)
        {
            bool isSelected = (_selectedMastering != null && _selectedMastering.Pointer == mastering.Pointer);

            // 文案先算好
            string nameLine = "";
            string levelLine = "";
            string noIconText = "";
            try
            {
                nameLine = $"<b>{mastering.MasteringGroup?.Id}</b>";
                levelLine = string.Format("text_skill_manager_show_level".i18n(), mastering.Level);
                noIconText = "text_item_instance_manager_no_icon".i18n();
            }
            catch (Exception ex)
            {
                OracleLog.Throttled("mastering_text", $"[Oracle] 专精条目文案失败: {ex.Message}");
            }

            Texture2D icon = GetMasteringCachedIcon(mastering);

            GUILayout.BeginHorizontal(
                isSelected ? UIStyleManager.SelectedBoxStyle : UIStyleManager.BoxStyle,
                GUILayout.Height(80));

            if (icon != null)
            {
                DrawRotatedIcon(icon);
            }
            else
            {
                GUILayout.Label(noIconText, GUILayout.Width(64), GUILayout.Height(64));
            }

            GUILayout.BeginVertical();
            GUILayout.Space(8);
            GUILayout.Label(nameLine);
            GUILayout.Label(levelLine);
            GUILayout.EndVertical();

            GUILayout.FlexibleSpace();

            GUIStyle btnStyle = isSelected
                ? UIStyleManager.RedButtonStyle
                : UIStyleManager.BlueButtonStyle;

            string btnText = isSelected
                ? "text_button_item_instance_manager_selected".i18n()
                : "text_button_item_instance_manager_select".i18n();

            if (GUILayout.Button(btnText, btnStyle, GUILayout.Width(100), GUILayout.Height(35)))
            {
                _selectedMastering = isSelected ? null : mastering;
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>专精图标画成 45° 斜置，与游戏内的专精徽章观感一致</summary>
        private static void DrawRotatedIcon(Texture2D tex)
        {
            const float size = 72f;

            Rect rect = GUILayoutUtility.GetRect(size, size);

            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(45f, rect.center);
            GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit);
            GUI.matrix = old;
        }

        /// <summary>
        /// 专精图标：取该专精组第一个模板的物品图标并缓存。
        ///
        /// 内容与 4.1 一致，仅把异常从"静默吞掉"改为记一条日志 ——
        /// 4.1 这里是空的 catch，出问题时完全无从排查。
        /// </summary>
        private Texture2D GetMasteringCachedIcon(Mastering mastering)
        {
            if (mastering == null) return null;

            var group = mastering.MasteringGroup;
            var templates = group?.Templates;
            if (templates == null || templates.Length == 0) return null;

            string templateId = templates[0].ToString();
            if (string.IsNullOrEmpty(templateId)) return null;

            if (_masteringIconCache.TryGetValue(templateId, out Texture2D cached) && cached != null)
            {
                return cached;
            }

            try
            {
                var factory = Singleton<ItemFactory>.Instance;
                if (factory == null) return null;

                Item item = factory.GetPresetItem(templateId);
                if (item == null) return null;

                var iconData = ItemViewFactory.LoadItemIcon(item, 1, false);
                if (iconData?.Sprite?.texture != null)
                {
                    Texture2D tex = iconData.Sprite.texture;
                    _masteringIconCache[templateId] = tex;
                    return tex;
                }
            }
            catch (Exception ex)
            {
                OracleLog.Once("mastering_icon_fail", $"[Oracle] 专精图标生成失败: {ex.Message}");
            }

            return null;
        }
    }
}
