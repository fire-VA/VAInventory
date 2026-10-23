using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using FiresCore.UI.Shell;
using FiresCore.UI.Shell.Kit;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// The Player tab's default "Stats" pill.
    /// LEFT: a live preview of the player with their gear (reused: FCC's ModelPreview camera, copied below as PlayerPreview, since
    /// VAInventory does not depend on FCC) and the equipped items as rows; hover one = its vanilla tooltip, nothing can be dragged.
    /// RIGHT: the player's name, then every stat with HOW it is made up. Sources (no new formulas where code exists):
    ///   vanilla Player.m_baseHP / m_baseStamina, GetFoods (each food's health / stamina / eitr), GetMaxHealth / GetMaxStamina /
    ///   GetMaxEitr (the totals), GetBodyArmor + each worn item's GetArmor, GetCurrentWeapon().GetDamage() + GetSkillFactor,
    ///   GetTotalWeight / GetMaxCarryWeight, GetEquipmentMovementModifier, SEMan.GetStatusEffects (buffs, debuffs, set bonuses: each
    ///   with its own vanilla tooltip); RPGClasses AttributeEffects.Current(Stat) (class attribute bonuses, by reflection) and
    ///   ClassProgressApi.TryGet (level). What the totals hold beyond base + food is shown as one "other" line, never guessed apart.
    /// Refreshes once a second while shown; the preview camera runs only between OnShow and OnHide.
    /// </summary>
    internal sealed class PlayerStatsPage : FiresShellModuleBase
    {
        const string ModuleId = "vainv.player.stats";
        static PlayerStatsPage s_instance;
        static IShellHandle s_handle;

        public override string Id => ModuleId;
        public override string Title => "Stats";
        public override int Order => 9;
        public override string ParentTab => "player";
        public override string PillLabel => "Stats";
        public override int PillOrder => 0;
        public override bool Dense => true;
        public override Vector2 MinContentSize => new Vector2(1100f, 720f);

        RectTransform _content, _left, _right, _rightBody, _equipList, _tip;
        RawImage _sink;
        PlayerPreview _preview;
        PageTicker _ticker;
        bool _shown;
        int _refreshN;   // 1.0.261

        internal static void Register()
        {
            if (s_instance != null) return;
            try
            {
                s_instance = new PlayerStatsPage();
                s_handle = FiresShell.Register(s_instance);
                EquipmentPanel.PanelLog?.LogInfo($"[VAInventory] hub page '{ModuleId}' registered (Player > Stats, pill order 0)");
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"[VAInventory] Player > Stats page not registered (needs FiresUnifiedCore 0.2.606+): {e.GetType().Name}: {e.Message}"); }
        }

        public override void BuildTab(RectTransform content, IShellContext ctx)
        {
            _content = content;
            float w = Mathf.Max(content.rect.width, MinContentSize.x), leftW = Mathf.Round(w * 0.38f);
            _left = Widgets.Node(content, "StatsLeft"); Widgets.Stretch(_left, 0f, 0f, 0f, 0f);
            _left.anchorMax = new Vector2(0.38f, 1f);
            _right = Widgets.Node(content, "StatsRight"); Widgets.Stretch(_right, 0f, 0f, 0f, 0f);
            _right.anchorMin = new Vector2(0.38f, 0f); _right.offsetMin = new Vector2(24f, 0f);

            // left: preview (top 62 %) + equipped rows
            var well = Widgets.Well(_left, "PreviewWell", 0f, 0f, leftW, 10f);
            well.anchorMin = new Vector2(0f, 0.38f); well.anchorMax = Vector2.one; well.offsetMin = Vector2.zero; well.offsetMax = Vector2.zero;
            var img = new GameObject("Preview", typeof(RectTransform), typeof(RawImage));
            var irt = (RectTransform)img.transform; irt.SetParent(well, false); Widgets.Stretch(irt, 6f, 6f, 6f, 6f);
            _sink = img.GetComponent<RawImage>(); _sink.raycastTarget = false;
            var aspect = img.AddComponent<AspectRatioFitter>(); aspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent; aspect.aspectRatio = 1f;
            _equipList = Widgets.Node(_left, "Equipped");
            _equipList.anchorMin = Vector2.zero; _equipList.anchorMax = new Vector2(1f, 0.38f); _equipList.offsetMin = Vector2.zero; _equipList.offsetMax = new Vector2(0f, -10f);

            // right: a scroll view (the breakdown can be long)
            var view = Widgets.Node(_right, "Viewport"); Widgets.Stretch(view, 0f, 0f, 0f, 0f);
            view.gameObject.AddComponent<RectMask2D>();
            var hit = view.gameObject.AddComponent<Image>(); hit.color = new Color(0f, 0f, 0f, 0f);
            _rightBody = Widgets.Node(view, "Body");
            _rightBody.anchorMin = new Vector2(0f, 1f); _rightBody.anchorMax = new Vector2(1f, 1f); _rightBody.pivot = new Vector2(0.5f, 1f);
            _rightBody.anchoredPosition = Vector2.zero; _rightBody.sizeDelta = new Vector2(0f, 10f);
            var sr = view.gameObject.AddComponent<ScrollRect>(); sr.viewport = view; sr.content = _rightBody; sr.horizontal = false; sr.scrollSensitivity = 30f; sr.movementType = ScrollRect.MovementType.Clamped;

            _ticker = content.gameObject.AddComponent<PageTicker>(); _ticker.Page = this; _ticker.enabled = false;
            ctx.SetFooterHints(("Hover", "inspect gear"), ("Esc", "back"));
            EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] Player > Stats built: content {content.rect.width:0}x{content.rect.height:0}");
        }

        // 
        // the SAME page, sub-view "companion" = the open companion (FiresCompanions CompanionPanelApi.Current.Target): its preview, gear,
        // pools / armour / damage / skills from CompanionPanelApi.Stats (Core CompanionStats + CompanionSkills), its active effects, and an
        // "Attributes" button to FiresCompanions' CompanionAttributesUi (0.1.76+). The companion panel's Stats pill opens it:
        // PlayerStatsPage.OpenForCompanion() -> FiresShell.Open("vainv.player.stats", "companion").
        Humanoid _companion;
        internal static bool Registered => s_handle != null;
        // VAInventoryApi.OpenCompanionStats(Humanoid) hands the companion here and opens THIS page on it - no inventory.
        static Humanoid s_pendingCompanion;
        internal static bool OpenFor(Humanoid companion)
        {
            if (companion == null || s_handle == null) return false;
            s_pendingCompanion = companion;
            // The inventory closes first
            if (InventoryGui.IsVisible() && InventoryGui.instance != null) InventoryGui.instance.Hide();
            try { FiresShell.Open(ModuleId, "companion"); EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] OpenCompanionStats: hub Player > Stats for '{companion.m_name}'"); return true; }
            catch (Exception e) { s_pendingCompanion = null; EquipmentPanel.PanelLog?.LogDebug("[VAInventory] OpenCompanionStats failed: " + e.Message); return false; }
        }

        internal static void OpenForCompanion()
        {
            // The panel's Stats pill takes the SAME path as
            // VAInventoryApi.OpenCompanionStats - the companion is captured first, the inventory closes, then the hub page opens on it
            var c = CompanionStats.Target();
            if (c != null) { OpenFor(c); return; }
            try { FiresShell.Open(ModuleId, "companion"); EquipmentPanel.PanelLog?.LogDebug("[VAInventory] companion Stats -> hub Player > Stats (companion view)"); }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug("[VAInventory] companion Stats: hub open failed: " + e.Message); }
        }

        public override void OnShow(string subView)
        {
            _shown = true; _refreshN = 0;   // 1.0.261
            _companion = string.Equals(subView, "companion", StringComparison.OrdinalIgnoreCase) ? (s_pendingCompanion != null ? s_pendingCompanion : CompanionStats.Target()) : null;   // a companion handed in by OpenCompanionStats first
            s_pendingCompanion = null;
            var p = Player.m_localPlayer;
            if (_preview == null && _sink != null) { _preview = new PlayerPreview(); _preview.Initialize(_left, _sink); }
            Transform who = _companion != null ? _companion.transform : p != null ? p.transform : null;
            if (who != null && _preview != null) { _preview.SetTarget(who); _preview.Show(); }
            _equipSig = null;
            if (_ticker != null) { _ticker.enabled = true; _ticker.ProbeAt = Time.unscaledTime + 1.5f; }   // One raycast probe 1.5 s after show
            Refresh();
        }

        public override void OnHide()
        {
            _shown = false;
            _preview?.Hide();
            if (_ticker != null) _ticker.enabled = false;
            HideTip();
        }

        public override void OnResize(Vector2 size) { if (_shown) Refresh(); }

        sealed class PageTicker : MonoBehaviour
        {
            internal PlayerStatsPage Page; float _next; internal float ProbeAt = -1f;
            void LateUpdate()
            {
                Page?._preview?.UpdateFraming();
                if (ProbeAt > 0f && Time.unscaledTime >= ProbeAt) { ProbeAt = -1f; Page?.RaycastProbe(); }   // 1.0.267
                if (Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + 1f;
                Page?.Refresh();
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string F(float v) => Mathf.Abs(v - Mathf.Round(v)) < 0.05f ? Mathf.Round(v).ToString("0", Inv) : v.ToString("0.#", Inv);
        static string Pct(float v) => (v >= 0f ? "+" : "") + v.ToString("0.#", Inv) + "%";

        // Core's own kit colours (FiresTokens.Ok /
        // Danger) as TMP rich text (kit labels have richText on). sign > 0 = good, < 0 = bad, 0 = left as it is.
        static readonly string GoodHex = "#" + ColorUtility.ToHtmlStringRGB(FiresTokens.Ok), BadHex = "#" + ColorUtility.ToHtmlStringRGB(FiresTokens.Danger);
        static string Tint(string s, float sign) => sign > 0f ? $"<color={GoodHex}>{s}</color>" : sign < 0f ? $"<color={BadHex}>{s}</color>" : s;
        static string Signed(float v) => Tint((v > 0 ? "+" : "") + F(v), v);
        static string SignedPct(float v) => Tint(Pct(v), v);
        static string Mult(float m) => Tint("x" + m.ToString("0.##", Inv), m - 1f);

        int _lastSig;
        void Refresh()
        {
            if (_companion != null) { RefreshCompanion(); return; }
            var p = Player.m_localPlayer;
            if (p == null || _rightBody == null) return;
            BuildEquipped(p);
            // Core's kit finds Section/Row groups BY NAME and Destroy is deferred, so the
            // rebuilt rows landed in the old (dying) groups. Detach first, then destroy (memory kit-redraw-detach-before-destroy).
            for (int i = _rightBody.childCount - 1; i >= 0; i--) { var c = _rightBody.GetChild(i); c.SetParent(null, false); UnityEngine.Object.Destroy(c.gameObject); }
            float width = Mathf.Max(300f, ((RectTransform)_rightBody.parent).rect.width - 16f);
            float y = 0f;

            // name header
            var name = KitText.Create(_rightBody, "Name", p.GetPlayerName(), FontRole.SerifBold, 30f, FiresTokens.Gold, KitAlign.Left, 0f, true, false);
            Widgets.Place(name.Rect, 0f, y, width, 40f); y += 42f;
            string cls = ClassLine(p);
            if (cls != null) { var c = KitText.Create(_rightBody, "Class", cls, FontRole.SansRegular, 15f, FiresTokens.TextMuted, KitAlign.Left, 0f, true, false); Widgets.Place(c.Rect, 0f, y, width, 22f); y += 26f; }
            y += 8f;

            var foods = p.GetFoods();
            float fH = 0f, fS = 0f, fE = 0f;
            if (foods != null) foreach (var f in foods) { fH += f.m_health; fS += f.m_stamina; fE += f.m_eitr; }

            // health / stamina / eitr: base + food + other (class attribute %, skills, effects) = vanilla's own total
            y = Pool(width, y, "health", "Health", p.GetMaxHealth(), p.m_baseHP, fH, "MaxHealth");
            y = Pool(width, y, "stamina", "Stamina", p.GetMaxStamina(), p.m_baseStamina, fS, "MaxStamina");
            y = Pool(width, y, "eitr", "Eitr", p.GetMaxEitr(), 0f, fE, "MaxEitr");

            // armour: each worn piece + whatever else GetBodyArmor adds
            var s = Widgets.Section(_rightBody, "armor", "Armor", 0f, y, width, "total " + F(p.GetBodyArmor()));
            float pieces = 0f;
            foreach (var it in Equipped(p))
            {
                float a = 0f; try { a = it.GetArmor(); } catch { }
                if (a <= 0f) continue;
                pieces += a; Widgets.Row(s, "a_" + it.m_shared.m_name, Loc(it.m_shared.m_name), F(a));
            }
            float namedA = ClassLines(s, "armor", "Armour", out bool anyPctA);
            float otherA = p.GetBodyArmor() - pieces - namedA;
            if (Mathf.Abs(otherA) >= 0.05f) Widgets.Row(s, "a_other", anyPctA ? "Other (the % bonuses above, unnamed sources)" : "Other (unnamed sources)", (otherA > 0 ? "+" : "") + F(otherA));
            y = s.Root.anchoredPosition.y * -1f + s.Y + 14f;

            // damage
            var weapon = p.GetCurrentWeapon();
            s = Widgets.Section(_rightBody, "damage", "Damage", 0f, y, width, weapon != null ? Loc(weapon.m_shared.m_name) : "unarmed");
            if (weapon != null)
            {
                float total = 0f; try { total = weapon.GetDamage().GetTotalDamage(); } catch { }
                Widgets.Row(s, "d_base", "Weapon damage (item, quality)", F(total));
                float skill = 0f; try { skill = p.GetSkillFactor(weapon.m_shared.m_skillType); } catch { }
                Widgets.Row(s, "d_skill", $"{weapon.m_shared.m_skillType} skill level", F(skill * 100f));
            }
            ClassLines(s, "dmgp", "PhysicalDamage", out _);
            ClassLines(s, "dmgm", "MagicDamage", out _);
            ClassLines(s, "aspd", "AttackSpeed", out _);
            y = -s.Root.anchoredPosition.y + s.Y + 14f;

            // movement + carry
            s = Widgets.Section(_rightBody, "move", "Movement and carry", 0f, y, width);
            float mov = EquipMoveMod(p);
            Widgets.Row(s, "m_equip", "Equipment movement modifier", Pct(mov * 100f), mov < 0f ? RowState.Warn : RowState.Normal);
            float wt = 0f, max = 0f; try { wt = p.GetInventory().GetTotalWeight(); max = p.GetMaxCarryWeight(); } catch { }
            Widgets.Row(s, "m_weight", "Carry weight", F(wt) + " / " + F(max), wt > max ? RowState.Danger : RowState.Normal);
            ClassLines(s, "carry", "CarryWeight", out _);
            SeLines(s, "carry", "CarryWeight");
            SeLines(s, "move", "Move");
            y = -s.Root.anchoredPosition.y + s.Y + 14f;

            // foods
            s = Widgets.Section(_rightBody, "food", "Food", 0f, y, width, foods != null ? foods.Count + " eaten" : null);
            if (foods != null) foreach (var f in foods)
                {
                    string nm = f.m_item != null ? Loc(f.m_item.m_shared.m_name) : "?";
                    Widgets.Row(s, "f_" + nm, $"{nm} ({Mathf.CeilToInt(f.m_time)} s)", $"HP {F(f.m_health)}  St {F(f.m_stamina)}  Ei {F(f.m_eitr)}");
                }
            y = -s.Root.anchoredPosition.y + s.Y + 14f;

            // active effects: buffs, debuffs, set bonuses - each with vanilla's own tooltip text (its values)
            var ses = p.GetSEMan() != null ? p.GetSEMan().GetStatusEffects() : null;
            s = Widgets.Section(_rightBody, "effects", "Active effects", 0f, y, width, ses != null ? ses.Count + " active" : null);
            if (ses != null) foreach (var se in ses)
                {
                    if (se == null) continue;
                    float left = 0f; try { left = se.GetRemaningTime(); } catch { }
                    string tip = ""; try { tip = se.GetTooltipString(); } catch { }
                    // "Sunburnt: -25% health regen, ..." from its own SE_Stats values,
                    // coloured by the net sign; no stat values = vanilla's own one-line tooltip text
                    string does = EffectDoes(se, out float net);
                    var row = Widgets.Row(s, "se_" + se.name, Tint(Loc(se.m_name) + (does.Length > 0 ? ": " + does : ""), net), left > 0f ? Mathf.CeilToInt(left) + " s" : "");
                    if (!string.IsNullOrEmpty(tip)) Hover(row.Root, Loc(se.m_name), Loc(tip));
                }
            y = -s.Root.anchoredPosition.y + s.Y + 14f;

            _rightBody.sizeDelta = new Vector2(0f, y);
            // The first 10 refreshes of every show log their live row count - an empty flash = 0 rows
            if (_refreshN < 10) { _refreshN++; int rows = 0; foreach (Transform sec in _rightBody) if (sec.gameObject.activeInHierarchy) rows += sec.GetComponentsInChildren<TMPro.TMP_Text>(false).Length; EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] Player > Stats rows {rows} (refresh #{_refreshN}, live children {_rightBody.childCount})"); }
            int sig = (int)y;
            if (sig != _lastSig) { _lastSig = sig; EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] Player > Stats refreshed: {p.GetPlayerName()}, HP {F(p.GetMaxHealth())} (base {F(p.m_baseHP)} + food {F(fH)}), armor {F(p.GetBodyArmor())}, {(ses != null ? ses.Count : 0)} effect(s)"); }
        }

        float Pool(float width, float y, string id, string title, float total, float baseV, float food, string classStat)
        {
            var s = Widgets.Section(_rightBody, id, title, 0f, y, width, "total " + F(total));
            Widgets.Row(s, id + "_base", "Base", F(baseV));
            if (food > 0f) Widgets.Row(s, id + "_food", "Food", "+" + F(food));
            // Every class-side source by name
            // (RPGClasses ClassStatsApi.Breakdown - their own calc), then the active effects' own SE_Stats values; Other = the remainder
            float named = ClassLines(s, id, classStat, out bool anyPct);
            SeLines(s, id, classStat);
            float other = total - baseV - food - named;
            if (Mathf.Abs(other) >= 0.05f) Widgets.Row(s, id + "_other", anyPct ? "Other (the % bonuses above, unnamed sources)" : "Other (unnamed sources)", (other > 0 ? "+" : "") + F(other));
            return -s.Root.anchoredPosition.y + s.Y + 14f;
        }

        // ---- companion view ------------------------------------------------------------------------------------------------------
        void RefreshCompanion()
        {
            var c = _companion;
            if (c == null || _rightBody == null) return;
            BuildEquipped(c);
            for (int i = _rightBody.childCount - 1; i >= 0; i--) { var ch = _rightBody.GetChild(i); ch.SetParent(null, false); UnityEngine.Object.Destroy(ch.gameObject); }
            float width = Mathf.Max(300f, ((RectTransform)_rightBody.parent).rect.width - 16f);
            float y = 0f;
            object st = CompanionStats.Of(c);
            string name = CompanionStats.Str(st, "DisplayName"); if (string.IsNullOrEmpty(name)) name = Loc(c.m_name);
            var nm = KitText.Create(_rightBody, "Name", name, FontRole.SerifBold, 30f, FiresTokens.Gold, KitAlign.Left, 0f, true, false);
            Widgets.Place(nm.Rect, 0f, y, width, 40f); y += 42f;
            string role = CompanionStats.Str(st, "Role"); int lvl = (int)CompanionStats.Num(st, "Level");
            var sub = KitText.Create(_rightBody, "Class", $"Companion{(lvl > 0 ? " · level " + lvl : "")}{(string.IsNullOrEmpty(role) ? "" : " · " + role)}", FontRole.SansRegular, 15f, FiresTokens.TextMuted, KitAlign.Left, 0f, true, false);
            Widgets.Place(sub.Rect, 0f, y, width, 22f); y += 26f;
            // Attributes (FiresCompanions 0.1.76+ CompanionAttributesUi; hidden when it is absent or says no)
            if (CompanionStats.CanOpenAttributes(c))
            {
                // the kit button has no click of its own: a Button on its Outline, as CompanionKitPage.Pills does
                var b = Widgets.Button(_rightBody, "AttributesButton", "Attributes", ButtonKind.Primary, 0f, y, 160f, 30f);
                var target = b.Find("Outline")?.GetComponent<Graphic>();
                if (target != null)
                {
                    target.raycastTarget = true;
                    var btn = b.gameObject.GetComponent<Button>() ?? b.gameObject.AddComponent<Button>();
                    btn.targetGraphic = target; btn.transition = Selectable.Transition.None;
                    var cc = c; btn.onClick.AddListener(() => CompanionStats.OpenAttributes(cc));
                }
                y += 38f;
            }
            y += 8f;

            y = CompPool(width, y, "health", "Health", "MaxHealth", "BaseMaxHealth", "AttributeHealthBonus", "FoodHealthBonus", st, c.GetMaxHealth());
            y = CompPool(width, y, "stamina", "Stamina", "MaxStamina", "BaseMaxStamina", "AttributeStaminaBonus", "FoodStaminaBonus", st, 0f);
            y = CompPool(width, y, "eitr", "Eitr", "MaxEitr", "BaseMaxEitr", "AttributeEitrBonus", "FoodEitrBonus", st, 0f);

            var s = Widgets.Section(_rightBody, "armor", "Armor", 0f, y, width, "total " + F(CompanionStats.Num(st, "Armor")));
            foreach (var it in Equipped(c)) { float a = 0f; try { a = it.GetArmor(); } catch { } if (a > 0f) Widgets.Row(s, "a_" + it.m_shared.m_name, Loc(it.m_shared.m_name), F(a)); }
            y = -s.Root.anchoredPosition.y + s.Y + 14f;

            string weapon = CompanionStats.Str(st, "Weapon");
            s = Widgets.Section(_rightBody, "damage", "Damage", 0f, y, width, string.IsNullOrEmpty(weapon) ? "unarmed" : Loc(weapon));
            Widgets.Row(s, "d_total", "Weapon damage", F(CompanionStats.Num(st, "Damage")));
            y = -s.Root.anchoredPosition.y + s.Y + 14f;

            // skills: SkillsFull (name, level, progress) when FiresCompanions has it, else Skills (name, level)
            var skills = CompanionStats.Skills(st);
            s = Widgets.Section(_rightBody, "skills", "Skills", 0f, y, width, skills.Count + " skill(s)");
            foreach (var sk in skills) Widgets.Row(s, "k_" + sk.name, sk.name, F(sk.level) + (sk.progress >= 0f ? $"  ({sk.progress * 100f:0}%)" : ""));
            y = -s.Root.anchoredPosition.y + s.Y + 14f;

            var ses = c.GetSEMan() != null ? c.GetSEMan().GetStatusEffects() : null;
            s = Widgets.Section(_rightBody, "effects", "Active effects", 0f, y, width, ses != null ? ses.Count + " active" : null);
            if (ses != null) foreach (var se in ses)
                {
                    if (se == null) continue;
                    float left = 0f; try { left = se.GetRemaningTime(); } catch { }
                    string does = EffectDoes(se, out float net);
                    Widgets.Row(s, "se_" + se.name, Tint(Loc(se.m_name) + (does.Length > 0 ? ": " + does : ""), net), left > 0f ? Mathf.CeilToInt(left) + " s" : "");
                }
            y = -s.Root.anchoredPosition.y + s.Y + 14f;
            _rightBody.sizeDelta = new Vector2(0f, y);
            int sig = (int)y * 31 + skills.Count;
            if (sig != _lastSig) { _lastSig = sig; EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] Player > Stats (companion view) refreshed: {name}, HP max {F(CompanionStats.Num(st, "MaxHealth"))}, {skills.Count} skill(s), {(ses != null ? ses.Count : 0)} effect(s), stats source {(st != null ? "CompanionPanelApi.Stats" : "NONE")}"); }
        }

        // Base + attribute + food when FiresCompanions 0.1.76 gives the breakdown; the remainder (if any) as one muted line.
        float CompPool(float width, float y, string id, string title, string maxField, string baseF, string attrF, string foodF, object st, float fallbackMax)
        {
            float total = CompanionStats.Num(st, maxField); if (total <= 0f) total = fallbackMax;
            var s = Widgets.Section(_rightBody, id, title, 0f, y, width, "total " + F(total));
            float b = CompanionStats.Num(st, baseF), a = CompanionStats.Num(st, attrF), f = CompanionStats.Num(st, foodF);
            if (b > 0f) Widgets.Row(s, id + "_base", "Base", F(b));
            if (Mathf.Abs(a) >= 0.05f) Widgets.Row(s, id + "_attr", Tint("Attributes", a), Signed(a));
            if (Mathf.Abs(f) >= 0.05f) Widgets.Row(s, id + "_food", Tint("Food", f), Signed(f));
            float other = total - b - a - f;
            if (b > 0f && Mathf.Abs(other) >= 0.05f) Widgets.Row(s, id + "_other", "Other (level, effects)", Signed(other));
            return -s.Root.anchoredPosition.y + s.Y + 14f;
        }

        /// <summary>FiresCompanions, by reflection only (VAInventory has no reference): CompanionPanelApi.Current/Stats (0.1.58+ contract) and
        /// CompanionAttributesUi.CanOpen/Open (0.1.76+). Every miss = a skipped line, never a throw.</summary>
        static class CompanionStats
        {
            static Type s_api, s_ui; static bool s_looked;
            static void Look()
            {
                if (s_looked) return; s_looked = true;
                s_api = Type.GetType("VerdantsAscent.Modules.Companions.CompanionPanelApi, FiresCompanions", false);
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "FiresCompanions") continue;
                    try { foreach (var t in asm.GetTypes()) if (t.Name == "CompanionAttributesUi") { s_ui = t; break; } } catch { }
                }
            }
            static object Get(object o, string n)
            {
                if (o == null) return null;
                var t = o.GetType();
                var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance); if (p != null) return p.GetValue(o);
                var f = t.GetField(n, BindingFlags.Public | BindingFlags.Instance); return f?.GetValue(o);
            }
            internal static Humanoid Target()
            {
                Look();
                try { return Get(s_api?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null), "Target") as Humanoid; } catch { return null; }
            }
            internal static object Of(Humanoid c)
            {
                Look();
                try { return s_api?.GetMethod("Stats", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Humanoid) }, null)?.Invoke(null, new object[] { c }); } catch { return null; }
            }
            internal static string Str(object st, string n) => Get(st, n) as string;
            internal static float Num(object st, string n) { try { var v = Get(st, n); return v != null ? Convert.ToSingle(v) : 0f; } catch { return 0f; } }
            internal static List<(string name, float level, float progress)> Skills(object st)
            {
                var l = new List<(string, float, float)>();
                try
                {
                    if (Get(st, "SkillsFull") is System.Collections.IEnumerable full)
                    {
                        foreach (var e in full)
                        {
                            if (e == null) continue; var t = e.GetType();
                            object A(string n1, string n2) => t.GetField(n1)?.GetValue(e) ?? t.GetProperty(n1)?.GetValue(e) ?? t.GetField(n2)?.GetValue(e) ?? t.GetProperty(n2)?.GetValue(e);
                            string n = A("Item1", "Name") as string; if (string.IsNullOrEmpty(n)) continue;
                            l.Add((n, Convert.ToSingle(A("Item2", "Level") ?? 0f), Convert.ToSingle(A("Item3", "Progress") ?? -1f)));
                        }
                        if (l.Count > 0) return l;
                    }
                    if (Get(st, "Skills") is IEnumerable<KeyValuePair<string, float>> sk) foreach (var kv in sk) l.Add((kv.Key, kv.Value, -1f));
                }
                catch { }
                return l;
            }
            static Component Controller(Humanoid c)
            {
                if (c == null) return null;
                foreach (var m in c.GetComponents<MonoBehaviour>()) if (m != null && m.GetType().Name == "CompanionController") return m;
                return null;
            }
            internal static bool CanOpenAttributes(Humanoid c)
            {
                Look();
                var ctl = Controller(c); var m = s_ui?.GetMethod("CanOpen", BindingFlags.Public | BindingFlags.Static);
                if (ctl == null || m == null) return false;
                try { return (bool)m.Invoke(null, new object[] { ctl }); } catch { return false; }
            }
            internal static void OpenAttributes(Humanoid c)
            {
                var ctl = Controller(c); var m = s_ui?.GetMethod("Open", BindingFlags.Public | BindingFlags.Static);
                if (ctl == null || m == null) return;
                try { m.Invoke(null, new object[] { ctl, null }); EquipmentPanel.PanelLog?.LogDebug("[VAInventory] companion Stats: Attributes opened (FiresCompanions CompanionAttributesUi.Open)"); }
                catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug("[VAInventory] companion Stats: Attributes open failed: " + (e.InnerException ?? e).Message); }
            }
        }

        // ---- RPGClasses, by reflection (soft) --------------------------------------------------------------------------------
        static Type s_attr, s_statEnum, s_progress; static MethodInfo s_current, s_tryGet; static bool s_rpgLooked;
        static void LookRpg()
        {
            if (s_rpgLooked) return; s_rpgLooked = true;
            s_attr = Type.GetType("FiresRPGClasses.Combat.AttributeEffects, FiresRPGClasses");
            s_statEnum = s_attr?.GetNestedType("Stat", BindingFlags.Public);
            s_current = s_statEnum != null ? s_attr.GetMethod("Current", BindingFlags.Public | BindingFlags.Static, null, new[] { s_statEnum }, null) : null;
            s_progress = Type.GetType("FiresRPGClasses.ClassProgressApi, FiresRPGClasses");
            s_tryGet = s_progress?.GetMethod("TryGet", BindingFlags.Public | BindingFlags.Static);
        }
        // RPGClasses ClassStatsApi.Breakdown(Player, string stat) -> per-source (label, flat, percent). Accepts a list of
        // ValueTuple<string,float,float>, KeyValuePair<string,Vector2> (x flat, y %) or any object with Label/Flat/Percent. Missing API =
        // the 1.0.248 single "class attribute bonus %" line. Returns the FLAT total it listed (so Other is only the remainder).
        static MethodInfo s_breakdown; static bool s_bdLooked, s_bdWarned;
        float ClassLines(KitSection s, string id, string stat, out bool anyPct)
        {
            anyPct = false;
            if (!s_bdLooked) { s_bdLooked = true; s_breakdown = Type.GetType("FiresRPGClasses.ClassStatsApi, FiresRPGClasses")?.GetMethod("Breakdown", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Player), typeof(string) }, null); }
            var p = Player.m_localPlayer;
            if (s_breakdown == null || p == null) { ClassRow(s, stat, "Class attribute bonus"); return 0f; }
            float flatSum = 0f; int n = 0;
            try
            {
                if (s_breakdown.Invoke(null, new object[] { p, stat }) is System.Collections.IEnumerable list)
                    foreach (var e in list)
                    {
                        if (e == null || !ReadEntry(e, out string label, out float flat, out float pct)) continue;
                        bool hasF = Mathf.Abs(flat) >= 0.05f, hasP = Mathf.Abs(pct) >= 0.05f;
                        if (!hasF && !hasP) continue;
                        string v = (hasF ? Signed(flat) : "") + (hasP ? (hasF ? "  " : "") + SignedPct(pct) : "");
                        // The source's own name as RPGClasses gives it (per skill / passive /
                        // attribute once ClassStatsApi.Breakdown lists them one by one), coloured by its sign
                        Widgets.Row(s, id + "_c" + (n++), Tint(label, hasF ? flat : pct), v);
                        flatSum += flat; if (hasP) anyPct = true;
                    }
            }
            catch (Exception ex) { if (!s_bdWarned) { s_bdWarned = true; EquipmentPanel.PanelLog?.LogWarning($"[VAInventory] Player > Stats: RPGClasses ClassStatsApi.Breakdown failed: {(ex.InnerException ?? ex).Message}"); } }
            return flatSum;
        }
        static bool ReadEntry(object e, out string label, out float flat, out float pct)
        {
            label = null; flat = 0f; pct = 0f;
            var t = e.GetType();
            object Get(string n) { var f = t.GetField(n); if (f != null) return f.GetValue(e); var pr = t.GetProperty(n); return pr != null ? pr.GetValue(e) : null; }
            if (t.Name.StartsWith("ValueTuple", StringComparison.Ordinal)) { label = Get("Item1") as string; flat = Convert.ToSingle(Get("Item2")); pct = Convert.ToSingle(Get("Item3")); }
            else if (t.Name.StartsWith("KeyValuePair", StringComparison.Ordinal)) { label = Get("Key") as string; if (Get("Value") is Vector2 v2) { flat = v2.x; pct = v2.y; } }
            else { label = (Get("Label") ?? Get("label")) as string; flat = Convert.ToSingle(Get("Flat") ?? Get("flat") ?? 0f); pct = Convert.ToSingle(Get("Percent") ?? Get("percent") ?? 0f); }
            return !string.IsNullOrEmpty(label);
        }

        // Each active effect's own SE_Stats value for this stat, named ("Effect: Rested (health regen)  x1.5"); vanilla field
        // names read by reflection (a renamed field = that line skipped). Vanilla SE_Stats has no max HP / stamina / eitr field, so the
        // pools show the effects' regen multipliers.
        static readonly Dictionary<string, (string field, bool mult, string what)> SeFields = new Dictionary<string, (string, bool, string)>
        {
            { "MaxHealth", ("m_healthRegenMultiplier", true, "health regen") }, { "MaxStamina", ("m_staminaRegenMultiplier", true, "stamina regen") },
            { "MaxEitr", ("m_eitrRegenMultiplier", true, "eitr regen") }, { "CarryWeight", ("m_addMaxCarryWeight", false, "carry weight") },
            { "Move", ("m_speedModifier", false, "move speed") },
        };
        static void SeLines(KitSection s, string id, string stat)
        {
            var p = Player.m_localPlayer;
            if (p == null || p.GetSEMan() == null || !SeFields.TryGetValue(stat, out var spec)) return;
            var f = typeof(SE_Stats).GetField(spec.field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null || f.FieldType != typeof(float)) return;
            int n = 0;
            foreach (var se in p.GetSEMan().GetStatusEffects())
            {
                if (!(se is SE_Stats st)) continue;
                float v = (float)f.GetValue(st);
                if (spec.mult ? Mathf.Abs(v - 1f) < 0.005f : Mathf.Abs(v) < 0.005f) continue;
                float sign = spec.mult ? v - 1f : v;
                string val = spec.mult ? Mult(v) : (spec.field == "m_speedModifier" ? SignedPct(v * 100f) : Signed(v));
                // ("'Overheated (stamina regen)' in red"): the whole line in the effect's colour
                Widgets.Row(s, id + "_se" + (n++), Tint($"{Loc(se.m_name)} ({spec.what})", sign), val);
            }
        }

        // What an effect does, from vanilla SE_Stats fields (read by name; a missing field is skipped). net = +1 / -1 / 0 by the sum of
        // the signs (a mix reads neutral). Nothing found = the effect's own vanilla tooltip line (m_tooltip, localized).
        static readonly (string field, bool mult, string what)[] DoesFields =
        {
            ("m_healthRegenMultiplier", true, "health regen"), ("m_staminaRegenMultiplier", true, "stamina regen"), ("m_eitrRegenMultiplier", true, "eitr regen"),
            ("m_speedModifier", false, "move speed"), ("m_addMaxCarryWeight", false, "carry weight"), ("m_raiseSkillModifier", false, "skill gain"),
            ("m_runStaminaDrainModifier", false, "run stamina"), ("m_jumpStaminaUseModifier", false, "jump stamina"), ("m_damageModifier", false, "damage"),
        };
        static readonly Dictionary<string, FieldInfo> s_doesF = new Dictionary<string, FieldInfo>();
        static string EffectDoes(StatusEffect se, out float net)
        {
            net = 0f;
            var parts = new List<string>();
            if (se is SE_Stats st)
                foreach (var d in DoesFields)
                {
                    if (!s_doesF.TryGetValue(d.field, out var f)) s_doesF[d.field] = f = typeof(SE_Stats).GetField(d.field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f == null || f.FieldType != typeof(float)) continue;
                    float v = (float)f.GetValue(st);
                    float pct = d.mult ? (v - 1f) * 100f : (d.field == "m_addMaxCarryWeight" ? v : v * 100f);
                    if (Mathf.Abs(pct) < 0.05f) continue;
                    // costs (stamina drains) are bad when they go UP
                    float good = d.what.EndsWith("stamina") ? -pct : pct;
                    net += Mathf.Sign(good);
                    string val = d.field == "m_addMaxCarryWeight" ? (pct > 0 ? "+" : "") + F(pct) : Pct(pct);
                    parts.Add(Tint(val + " " + d.what, good));
                }
            if (parts.Count == 0)
            {
                string t = se != null && !string.IsNullOrEmpty(se.m_tooltip) ? Loc(se.m_tooltip).Split('\n')[0].Trim() : "";
                return t;
            }
            net = Mathf.Sign(net);
            return string.Join(", ", parts.ToArray());
        }

        static void ClassRow(KitSection s, string stat, string label)
        {
            LookRpg();
            if (s_current == null) return;
            try
            {
                float v = (float)s_current.Invoke(null, new[] { Enum.Parse(s_statEnum, stat) });
                if (Mathf.Abs(v) >= 0.05f) Widgets.Row(s, "c_" + stat, label, Pct(v));
            }
            catch { }
        }
        static string ClassLine(Player p)
        {
            LookRpg();
            if (s_tryGet == null) return null;
            try
            {
                var args = new object[] { p, 0, 0, 0 };
                if (!(bool)s_tryGet.Invoke(null, args)) return null;
                return $"Class level {args[1]} / {args[3]}  ·  {args[2]} point(s) spent";
            }
            catch { return null; }
        }

        // ---- vanilla helpers ----------------------------------------------------------------------------------------------------
        static string Loc(string s) => Localization.instance != null ? Localization.instance.Localize(s) : s;
        static List<ItemDrop.ItemData> Equipped(Humanoid p)
        {
            var l = new List<ItemDrop.ItemData>();
            try { foreach (var it in p.GetInventory().GetEquippedItems()) if (it != null) l.Add(it); } catch { }
            return l;
        }
        static MethodInfo s_moveMod; static bool s_moveLooked;
        static float EquipMoveMod(Player p)
        {
            if (!s_moveLooked) { s_moveLooked = true; s_moveMod = typeof(Player).GetMethod("GetEquipmentMovementModifier", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null); }
            if (s_moveMod != null) { try { return (float)s_moveMod.Invoke(p, null); } catch { } }
            float sum = 0f; foreach (var it in Equipped(p)) sum += it.m_shared.m_movementModifier;   // fallback: the same sum vanilla makes
            return sum;
        }
        static MethodInfo s_itemTip; static bool s_tipLooked;
        static string ItemTooltip(ItemDrop.ItemData it)
        {
            if (!s_tipLooked)
            {
                s_tipLooked = true;
                foreach (var m in typeof(ItemDrop.ItemData).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "GetTooltip" && m.ReturnType == typeof(string)) { s_itemTip = m; break; }
            }
            if (s_itemTip == null) return "";
            try
            {
                var ps = s_itemTip.GetParameters(); var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                return Loc((string)s_itemTip.Invoke(it, args));
            }
            catch { return ""; }
        }

        // FLO by reflection (soft): FloApi.GetRarity (0.1.8+) / GetRarityColor (its slot tint, used at full alpha) / GetDisplayName +
        // GetRarityName. No FLO = the vanilla name, no rarity. Common shows "(Common)" in the normal text colour.
        static readonly string[] RarityNames = { "Common", "Magic", "Rare", "Epic", "Legendary" };
        static MethodInfo s_floRarity, s_floColor, s_floName, s_floRarityName; static bool s_floLooked;
        static void ItemLook(ItemDrop.ItemData it, out string shown, out string rarity, out string hex)
        {
            shown = Loc(it.m_shared.m_name); rarity = null; hex = null;
            if (!s_floLooked)
            {
                s_floLooked = true;
                var api = Type.GetType("FiresLootOverhaul.Api.FloApi, FiresLootOverhaul", false) ?? Type.GetType("FiresLootOverhaul.FloApi, FiresLootOverhaul", false);   // the current FLO name first, the older one as fallback
                s_floRarity = api?.GetMethod("GetRarity", new[] { typeof(ItemDrop.ItemData) });
                s_floColor = api?.GetMethod("GetRarityColor", new[] { typeof(ItemDrop.ItemData) });
                s_floName = api?.GetMethod("GetDisplayName", new[] { typeof(ItemDrop.ItemData) });
                s_floRarityName = api?.GetMethod("GetRarityName", new[] { typeof(int) });
            }
            try
            {
                if (s_floName?.Invoke(null, new object[] { it }) is string n && n.Length > 0) shown = Loc(n);
                if (s_floRarity == null) return;
                int r = (int)s_floRarity.Invoke(null, new object[] { it });
                rarity = s_floRarityName?.Invoke(null, new object[] { r }) as string ?? RarityNames[Mathf.Clamp(r, 0, RarityNames.Length - 1)];
                if (r > 0 && s_floColor?.Invoke(null, new object[] { it }) is Color c) hex = "#" + ColorUtility.ToHtmlStringRGB(c);
            }
            catch { }
        }

        // ---- left: equipped list (hover = tooltip, no drag) --------------------------------------------------------------------
        string _equipSig;
        void BuildEquipped(Humanoid p)
        {
            var items = Equipped(p);
            string sig = string.Join("|", items.ConvertAll(i => i.m_shared.m_name + i.m_quality + (int)i.m_durability).ToArray());
            if (sig == _equipSig) return;
            _equipSig = sig;
            for (int i = _equipList.childCount - 1; i >= 0; i--) { var c = _equipList.GetChild(i); c.SetParent(null, false); UnityEngine.Object.Destroy(c.gameObject); }   // Detach first
            float w = Mathf.Max(200f, _equipList.rect.width);
            var s = Widgets.Section(_equipList, "equipped", "Equipped", 0f, 0f, w, items.Count + " item(s)");
            foreach (var it in items)
            {
                // FLO's rolled name in its rarity colour, then "Quality 4 (Legendary)"
                ItemLook(it, out string shown, out string rarity, out string hex);
                var row = Widgets.Row(s, "e_" + it.m_shared.m_name, hex != null ? $"<color={hex}>{shown}</color>" : shown,
                    $"Quality {it.m_quality}" + (rarity != null ? $" ({(hex != null ? $"<color={hex}>{rarity}</color>" : rarity)})" : ""));
                var ic = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                var irt = (RectTransform)ic.transform; irt.SetParent(row.Root, false);
                irt.anchorMin = irt.anchorMax = new Vector2(0f, 0.5f); irt.pivot = new Vector2(1f, 0.5f); irt.sizeDelta = new Vector2(22f, 22f); irt.anchoredPosition = new Vector2(-4f, 0f);
                var img = ic.GetComponent<Image>(); img.sprite = it.GetIcon(); img.preserveAspect = true; img.raycastTarget = false;
                var item = it;
                Hover(row.Root, Loc(it.m_shared.m_name), null, () => ItemTooltip(item));
            }
        }

        // ---- hover tooltips (Core's kit Tooltip) ----------------------------------------------------------------------------------
        void Hover(RectTransform target, string title, string body, Func<string> lazyBody = null)
        {
            var g = target.GetComponent<Image>() ?? target.gameObject.AddComponent<Image>();
            g.color = new Color(0f, 0f, 0f, 0f); g.raycastTarget = true;
            var et = target.gameObject.AddComponent<EventTrigger>();
            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ => ShowTip(target, title, lazyBody != null ? lazyBody() : body));
            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback.AddListener(_ => HideTip());
            et.triggers.Add(enter); et.triggers.Add(exit);
        }
        // Diagnostic for a hover that raises no PointerEnter: once per show, the EventSystem's full raycast
        // at the FIRST Equipped row's centre is logged, top hit first, with each hit's path, graphic type and raycastTarget, and whether
        // the row (or a child) is in the list. A blocker shows as an unrelated graphic above the row; an empty list = no raycaster sees it.
        internal void RaycastProbe()
        {
            try
            {
                var rows = _equipList != null ? _equipList.Find("Section/equipped/Row") : null;
                if (rows == null || rows.childCount == 0) { EquipmentPanel.PanelLog?.LogDebug("[VAInventory] Player > Stats raycast probe: no Equipped row"); return; }
                var row = (RectTransform)rows.GetChild(0);
                var c = new Vector3[4]; row.GetWorldCorners(c);
                var canvas = row.GetComponentInParent<Canvas>()?.rootCanvas;
                Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                Vector2 pt = RectTransformUtility.WorldToScreenPoint(cam, (c[0] + c[2]) * 0.5f);
                var es = EventSystem.current;
                if (es == null) { EquipmentPanel.PanelLog?.LogDebug("[VAInventory] Player > Stats raycast probe: NO EventSystem"); return; }
                var data = new PointerEventData(es) { position = pt };
                var hits = new List<RaycastResult>(); es.RaycastAll(data, hits);
                var sb = new System.Text.StringBuilder($"[VAInventory] Player > Stats raycast probe at row '{row.name}' screen {pt.x:0},{pt.y:0} (canvas '{(canvas != null ? canvas.name : "-")}' {canvas?.renderMode}, raycaster {(canvas != null && canvas.GetComponent<GraphicRaycaster>() != null)}): {hits.Count} hit(s); row in list {hits.Exists(h => h.gameObject != null && h.gameObject.transform.IsChildOf(row))} | ");
                for (int i = 0; i < hits.Count && i < 8; i++)
                {
                    var go = hits[i].gameObject; if (go == null) continue;
                    string path = go.name; for (var p = go.transform.parent; p != null && path.Length < 160; p = p.parent) path = p.name + "/" + path;
                    var g = go.GetComponent<Graphic>();
                    sb.Append($"#{i} {path} ({(g != null ? g.GetType().Name : "?")}, raycastTarget {(g != null && g.raycastTarget)}, alpha {(g != null ? g.color.a : 0f):0.00}); ");
                }
                EquipmentPanel.PanelLog?.LogDebug(sb.ToString());
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug("[VAInventory] Player > Stats raycast probe failed: " + e.Message); }
        }

        void ShowTip(RectTransform target, string title, string body)
        {
            HideTip();
            if (_content == null) return;
            var c = new Vector3[4]; target.GetWorldCorners(c);
            var local = _content.InverseTransformPoint(c[2]);
            float x = local.x - _content.rect.xMin + 12f, y = _content.rect.yMax - local.y;
            _tip = Widgets.Tooltip(_content, title, body ?? "", null, Mathf.Min(x, _content.rect.width - 320f), Mathf.Max(0f, y), 300f);
            _tip.SetAsLastSibling();
            foreach (var gr in _tip.GetComponentsInChildren<Graphic>(true))
            {
                gr.raycastTarget = false;
                // The kit tooltip's fill + shadow are fully opaque
                if (gr is Image im && (gr.name == "Fill" || gr.name == "Shadow")) { var col = im.color; col.a = 1f; im.color = col; }
            }
            // drawn above EVERYTHING (the hub's footer and pills included): its own canvas, sorted on top
            var cv = _tip.gameObject.AddComponent<Canvas>(); cv.overrideSorting = true; cv.sortingOrder = 32000;
            // fully on screen: measure the built tooltip and pull it back inside the content rect (and below its top)
            Canvas.ForceUpdateCanvases();
            float tw = 0f, th = 0f;
            foreach (RectTransform ch in _tip) { if (ch == null) continue; tw = Mathf.Max(tw, ch.rect.width); th = Mathf.Max(th, ch.rect.height); }
            if (tw < 1f) tw = 300f;
            var ap = _tip.anchoredPosition;   // kit Place: x right, y DOWN as -y
            float px = Mathf.Clamp(ap.x, 0f, Mathf.Max(0f, _content.rect.width - tw - 4f));
            float py = Mathf.Clamp(-ap.y, 0f, Mathf.Max(0f, _content.rect.height - th - 4f));
            _tip.anchoredPosition = new Vector2(px, -py);
            TipProof(target, title);
        }

        // Diagnostic for a missing tooltip: the first 3 tooltips per session log where they
        // went - screen rect, active, the canvas that draws them, and every clipping ancestor (RectMask2D / Mask / ScrollRect viewport)
        // with whether the tooltip lies inside it. No line on hover = PointerEnter never reached the row's EventTrigger.
        static int s_tipLogs;
        void TipProof(RectTransform target, string title)
        {
            if (_tip == null || s_tipLogs >= 3) return;
            s_tipLogs++;
            try
            {
                var c = new Vector3[4]; _tip.GetWorldCorners(c);
                Rect TipScreen(RectTransform r) { var w = new Vector3[4]; r.GetWorldCorners(w); return new Rect(w[0].x, w[0].y, w[2].x - w[0].x, w[2].y - w[0].y); }
                Rect tr = TipScreen(_tip);
                foreach (RectTransform ch in _tip) { if (ch == null) continue; var r = TipScreen(ch); tr = Rect.MinMaxRect(Mathf.Min(tr.xMin, r.xMin), Mathf.Min(tr.yMin, r.yMin), Mathf.Max(tr.xMax, r.xMax), Mathf.Max(tr.yMax, r.yMax)); }
                var sb = new System.Text.StringBuilder($"[VAInventory] Player > Stats tooltip #{s_tipLogs} '{title}' on '{target.name}': screen {tr.xMin:0},{tr.yMin:0} {tr.width:0}x{tr.height:0} (screen {Screen.width}x{Screen.height}), active {_tip.gameObject.activeInHierarchy}, canvas '{_tip.GetComponentInParent<Canvas>()?.name}' sort {_tip.GetComponent<Canvas>()?.sortingOrder}; clippers: ");
                int n = 0;
                for (var p = _tip.parent; p != null; p = p.parent)
                {
                    bool m2 = p.GetComponent<RectMask2D>() != null, mk = p.GetComponent<Mask>() != null;
                    if (!m2 && !mk) continue;
                    var pr = TipScreen((RectTransform)p);
                    sb.Append($"{p.name} ({(m2 ? "RectMask2D" : "Mask")}, tooltip inside {pr.Overlaps(tr)}/{(pr.Contains(tr.min) && pr.Contains(tr.max))}); ");
                    n++;
                }
                if (n == 0) sb.Append("none");
                EquipmentPanel.PanelLog?.LogDebug(sb.ToString());
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug("[VAInventory] Player > Stats tooltip proof failed: " + e.Message); }
        }
        void HideTip() { if (_tip != null) { UnityEngine.Object.Destroy(_tip.gameObject); _tip = null; } }
    }

    /// <summary>
    /// The player preview for Player > Stats: a COPY of FiresCharacterCreator's UI/ModelPreview (1.0.18 isolated-layer render with a
    /// gradient backdrop, bounds framing). The live player (with gear) renders to a RenderTexture; only while the page is shown.
    /// </summary>
    internal sealed class PlayerPreview
    {
        Camera _camera; RenderTexture _rt; RawImage _sink; Transform _target;
        float _yaw, _distance = 3f, _height = 1f;
        Light _light; int _isolatedLayer = -1; GameObject _backdrop;
        readonly List<(GameObject go, int layer)> _swapped = new List<(GameObject, int)>();
        bool _fogWas, _inRender;

        public void Initialize(Transform host, RawImage sink)
        {
            _sink = sink;
            _rt = new RenderTexture(512, 512, 16) { antiAliasing = 2 }; _rt.Create();
            var camGo = new GameObject("VAInvStatsPreviewCamera");
            camGo.transform.SetParent(host, false);
            _camera = camGo.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor; _camera.backgroundColor = new Color(0.07f, 0.08f, 0.10f, 1f);
            _camera.fieldOfView = 28f; _camera.nearClipPlane = 0.05f; _camera.farClipPlane = 60f; _camera.targetTexture = _rt; _camera.enabled = false;
            var lightGo = new GameObject("VAInvStatsPreviewLight"); lightGo.transform.SetParent(camGo.transform, false);
            _light = lightGo.AddComponent<Light>(); _light.type = LightType.Directional; _light.intensity = 1f; _light.transform.localRotation = Quaternion.Euler(30f, 15f, 0f);
            if (_sink != null) { _sink.texture = _rt; _sink.enabled = false; }
            for (int i = 31; i >= 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) { _isolatedLayer = i; break; }
            if (_isolatedLayer >= 0)
            {
                _light.cullingMask = 1 << _isolatedLayer; _light.enabled = false;
                _backdrop = MakeBackdrop(camGo.transform, _isolatedLayer);
                _camera.cullingMask = 1 << _isolatedLayer;
                Camera.onPreCull += OnPreCull; Camera.onPostRender += OnPostRender;
            }
            else { int ui = LayerMask.NameToLayer("UI"); _camera.cullingMask = ui >= 0 ? ~(1 << ui) : ~0; }
        }

        static GameObject MakeBackdrop(Transform cam, int layer)
        {
            Color bottom = new Color32(0x16, 0x11, 0x0c, 255), top = new Color32(0x24, 0x1a, 0x11, 255), glow = new Color32(0x3a, 0x2a, 0x12, 255);
            var tex = new Texture2D(64, 128, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < 128; y++) for (int x = 0; x < 64; x++)
                {
                    float v = y / 127f, dx = (x - 31.5f) / 32f, dy = (y - 70f) / 64f;
                    float g = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy)) * 0.6f;
                    tex.SetPixel(x, y, Color.Lerp(Color.Lerp(bottom, top, v), glow, g));
                }
            tex.Apply();
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad); go.name = "VAInvStatsPreviewBackdrop";
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.layer = layer; go.transform.SetParent(cam, false); go.transform.localPosition = new Vector3(0f, 0f, 50f); go.transform.localScale = new Vector3(40f, 40f, 1f);
            var mr = go.GetComponent<MeshRenderer>(); mr.sharedMaterial = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Texture")) { mainTexture = tex };
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            go.SetActive(false);
            return go;
        }

        void OnPreCull(Camera cam)
        {
            if (cam != _camera || _target == null || _inRender) return;
            _inRender = true; _swapped.Clear();
            if (_backdrop != null) _backdrop.SetActive(true);
            foreach (var r in _target.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.gameObject.layer == _isolatedLayer) continue;
                _swapped.Add((r.gameObject, r.gameObject.layer)); r.gameObject.layer = _isolatedLayer;
            }
            _fogWas = RenderSettings.fog; RenderSettings.fog = false;
            if (_light != null) _light.enabled = true;
        }
        void OnPostRender(Camera cam) { if (cam == _camera) Restore(); }
        void Restore()
        {
            if (!_inRender) return;
            foreach (var s in _swapped) if (s.go != null) s.go.layer = s.layer;
            _swapped.Clear(); RenderSettings.fog = _fogWas;
            if (_backdrop != null) _backdrop.SetActive(false);
            if (_light != null) _light.enabled = false;
            _inRender = false;
        }

        public void SetTarget(Transform t) { _target = t; _yaw = 0f; }
        public void Show() { if (_camera != null) _camera.enabled = true; if (_sink != null) _sink.enabled = true; Fit(); UpdateFraming(); }
        public void Hide() { if (_camera != null) _camera.enabled = false; if (_sink != null) _sink.enabled = false; Restore(); }

        void Fit()
        {
            if (_camera == null || _target == null) return;
            Bounds b = default; bool any = false;
            foreach (var r in _target.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                if (any) b.Encapsulate(r.bounds); else { b = r.bounds; any = true; }
            }
            if (!any) return;
            float halfFov = _camera.fieldOfView * 0.5f * Mathf.Deg2Rad, half = Mathf.Max(b.extents.y, Mathf.Max(b.extents.x, b.extents.z));
            _distance = half * 1.15f / Mathf.Tan(halfFov) + Mathf.Max(b.extents.x, b.extents.z);
            _height = b.center.y - _target.position.y - 0.15f;
        }

        public void UpdateFraming()
        {
            if (_camera == null || _target == null || !_camera.enabled) return;
            Vector3 focus = _target.position + Vector3.up * _height;
            Vector3 dir = Quaternion.Euler(0f, _yaw, 0f) * _target.forward;
            _camera.transform.position = focus + dir * _distance + Vector3.up * 0.15f;
            _camera.transform.LookAt(focus);
        }
    }
}
