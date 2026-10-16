using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // The coin purse's popup: every configured currency; click one to display it in the purse. Plain Unity UI (VAInventory
    // doesn't reference FUC's helpers). Fire 10-07 (1.0.69): it opens on a LEFT click in Unified (right click in Legacy) and
    // looks like the Tools / Ammo / Food pouch popups: a title bar over slot-face cells, just above the purse, no screen
    // dimming. Closes on an outside click, Esc, or the inventory closing.
    internal static class CurrencyPouchPopup
    {
        private static GameObject _root;
        public static bool IsOpen => _root != null;

        // The pouch popups' look (UnifiedScreen.BuildPouches): panel colour, gold edge and title, cells at the row pitch.
        private static readonly Color PanelColor = new Color(0.13f, 0.10f, 0.07f, 0.98f);
        private static readonly Color Gold = new Color(0.93f, 0.78f, 0.37f, 1f);
        private static readonly Color GoldEdge = new Color(0.55f, 0.42f, 0.20f, 1f);
        private const int MaxCols = 4;
        private const float Cell = 64f, Gap = 6f, PadX = 10f, PadTop = 6f, PadBottom = 10f, TitleH = 20f, TitleGap = 4f, AboveAnchor = 6f;

        public static void Toggle(RectTransform anchor = null) { if (IsOpen) Close(); else Open(anchor); }

        public static void Close()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
        }

        private static TMP_FontAsset Font()
        {
            var amt = InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.transform.Find("amount")?.GetComponent<TMP_Text>();
            return amt != null ? amt.font : null;
        }

        public static void Open(RectTransform anchor = null)
        {
            Close();
            PouchPopup.Close(); // one popup at a time (pouches + coin)
            var grid = InventoryGui.instance?.m_playerGrid;
            if (grid == null) return;
            var canvas = grid.GetComponentInParent<Canvas>();
            if (canvas == null) return;

            var contents = CoinPurseStackPatch.GetPouchContents();
            if (contents.Count == 0) return;
            string displayed = CoinPurseStackPatch.DisplayedKey();
            var font = Font();
            var face = grid.m_elementPrefab != null ? grid.m_elementPrefab.GetComponent<Image>() : null;
            var faceTint = grid.m_elementPrefab != null ? grid.m_elementPrefab.GetComponent<Selectable>() : null;

            int cols = Mathf.Min(MaxCols, contents.Count);
            int rows = (contents.Count + cols - 1) / cols;
            float w = PadX * 2f + cols * Cell + (cols - 1) * Gap;
            float h = PadTop + TitleH + TitleGap + rows * Cell + (rows - 1) * Gap + PadBottom;

            _root = NewChild(canvas.transform, "CurrencyPouchPopup");
            _root.transform.SetAsLastSibling();
            _root.AddComponent<PopupWatcher>().Init(anchor);
            var prt = (RectTransform)_root.transform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(w, h);
            Place(prt, (RectTransform)canvas.transform, canvas, anchor, w, h);
            var pImg = _root.AddComponent<Image>(); pImg.color = PanelColor; pImg.raycastTarget = true;
            var pol = _root.AddComponent<Outline>(); pol.effectColor = GoldEdge; pol.effectDistance = new Vector2(2f, -2f);

            var title = NewChild(_root.transform, "title");
            var trt = (RectTransform)title.transform;
            trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(1f, 1f); trt.pivot = new Vector2(0.5f, 1f);
            trt.offsetMin = new Vector2(0f, -(PadTop + TitleH)); trt.offsetMax = new Vector2(0f, -PadTop);
            var ttmp = title.AddComponent<TextMeshProUGUI>();
            if (font != null) ttmp.font = font;
            ttmp.text = "Coin purse"; ttmp.fontSize = 15f; ttmp.color = Gold;
            ttmp.alignment = TextAlignmentOptions.Center; ttmp.raycastTarget = false;

            float y0 = -(PadTop + TitleH + TitleGap);
            for (int i = 0; i < contents.Count; i++)
            {
                var (key, item) = contents[i];
                int r = i / cols, c = i % cols;
                MakeCell(_root.transform, key, item,
                    string.Equals(key, displayed, System.StringComparison.OrdinalIgnoreCase),
                    font, face, faceTint, PadX + c * (Cell + Gap), y0 - r * (Cell + Gap), Cell);
            }
        }

        // Bottom-centre just above the purse (as the pouch popups sit above their pouch), kept on screen; below it when there
        // is no room above. Screen centre without an anchor.
        private static void Place(RectTransform popup, RectTransform canvasRect, Canvas canvas, RectTransform anchor, float w, float h)
        {
            popup.pivot = new Vector2(0.5f, 0f);
            popup.anchoredPosition = new Vector2(0f, -h / 2f);
            if (anchor == null) return;
            var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var corners = new Vector3[4];
            anchor.GetWorldCorners(corners); // 0 = bottom-left, 1 = top-left, 2 = top-right
            Vector2 top = RectTransformUtility.WorldToScreenPoint(cam, (corners[1] + corners[2]) * 0.5f);
            Vector2 bottom = RectTransformUtility.WorldToScreenPoint(cam, (corners[0] + corners[3]) * 0.5f);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, top, cam, out Vector2 localTop)) return;
            float cw = canvasRect.rect.width, ch = canvasRect.rect.height;
            var ap = localTop + new Vector2(0f, AboveAnchor);
            if (ap.y + h > ch / 2f - 4f && RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, bottom, cam, out Vector2 localBottom))
            {
                popup.pivot = new Vector2(0.5f, 1f);
                ap = localBottom - new Vector2(0f, AboveAnchor);
            }
            ap.x = Mathf.Clamp(ap.x, -cw / 2f + w / 2f + 4f, cw / 2f - w / 2f - 4f);
            popup.anchoredPosition = ap;
        }

        private static void MakeCell(Transform parent, string key, ItemDrop.ItemData item, bool displayed, TMP_FontAsset font,
            Image face, Selectable faceTint, float x, float y, float size)
        {
            var go = NewChild(parent, "cell");
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y); rt.sizeDelta = new Vector2(size, size);
            var bg = go.AddComponent<Image>();
            if (face != null && face.sprite != null) { bg.sprite = face.sprite; bg.type = Image.Type.Sliced; }
            bg.color = faceTint != null ? faceTint.colors.normalColor : new Color(0.19f, 0.16f, 0.12f, 0.9f);
            bg.raycastTarget = true;
            var ol = go.AddComponent<Outline>();
            ol.effectColor = displayed ? Gold : GoldEdge;
            ol.effectDistance = displayed ? new Vector2(2f, -2f) : new Vector2(1f, -1f);

            var icon = CoinPurseStackPatch.GetCurrencyIcon(key, item);
            if (icon != null)
            {
                var ig = NewChild(go.transform, "icon");
                var irt = (RectTransform)ig.transform; irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
                irt.offsetMin = new Vector2(6f, 12f); irt.offsetMax = new Vector2(-6f, -6f);
                var im = ig.AddComponent<Image>(); im.sprite = icon; im.preserveAspect = true; im.raycastTarget = false;
                if (item == null) im.color = new Color(1f, 1f, 1f, 0.3f);
            }

            var amt = NewChild(go.transform, "amt");
            var art = (RectTransform)amt.transform;
            art.anchorMin = new Vector2(0f, 0f); art.anchorMax = new Vector2(1f, 0f); art.pivot = new Vector2(0.5f, 0f);
            art.offsetMin = new Vector2(2f, 1f); art.offsetMax = new Vector2(-3f, 15f);
            var atmp = amt.AddComponent<TextMeshProUGUI>();
            if (font != null) atmp.font = font;
            atmp.text = (item?.m_stack ?? 0).ToString("N0"); atmp.fontSize = 12f;
            atmp.color = item != null ? Color.white : new Color(1f, 1f, 1f, 0.4f);
            atmp.alignment = TextAlignmentOptions.BottomRight; atmp.raycastTarget = false;

            string capturedKey = key;
            var btn = go.AddComponent<Button>(); btn.targetGraphic = bg;
            btn.onClick.AddListener(() =>
            {
                CoinPurseStackPatch.SetDisplayed(capturedKey);
                EquipmentPanel.MarkDirty();
                EquipmentPanel.RefreshPanelOnly();
                Close();
            });
        }

        private static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        // Closes on Esc, the inventory closing, or a click outside both the popup and the purse (a click on the purse is its
        // own toggle).
        private class PopupWatcher : MonoBehaviour
        {
            private RectTransform _anchor;
            internal void Init(RectTransform anchor) => _anchor = anchor;

            private void Update()
            {
                if (!InventoryGui.IsVisible() || Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
                if ((Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1))
                    && !Over(transform as RectTransform) && !Over(_anchor))
                    Close();
            }

            private static bool Over(RectTransform rect) =>
                rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, null);
        }
    }
}
