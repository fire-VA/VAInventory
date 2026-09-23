using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // Right-click the coin pouch → a grid popup of every configured currency; click one to
    // display it in the pouch. Plain Unity UI (VAInventory doesn't reference FUC's helpers),
    // styled to the game's dark parchment tooltip. Closes on outside-click, Esc, or inventory close.
    internal static class CurrencyPouchPopup
    {
        private static GameObject _root;
        public static bool IsOpen => _root != null;

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
            var grid = InventoryGui.instance?.m_playerGrid;
            if (grid == null) return;
            var canvas = grid.GetComponentInParent<Canvas>();
            if (canvas == null) return;

            var contents = CoinPurseStackPatch.GetPouchContents();
            if (contents.Count == 0) return;
            string displayed = CoinPurseStackPatch.DisplayedKey();
            var font = Font();

            const int cols = 3;
            int rows = Mathf.Max(1, (contents.Count + cols - 1) / cols);
            const float cell = 62f, gap = 6f, pad = 12f, titleH = 26f;
            float w = pad * 2 + cols * cell + (cols - 1) * gap;
            float h = pad * 2 + titleH + rows * cell + (rows - 1) * gap;

            _root = new GameObject("CurrencyPouchPopup", typeof(RectTransform));
            _root.transform.SetParent(canvas.transform, false);
            Stretch(_root);
            _root.transform.SetAsLastSibling();
            _root.AddComponent<PopupWatcher>();

            var catcher = NewChild(_root.transform, "catcher");
            Stretch(catcher);
            var cImg = catcher.AddComponent<Image>(); cImg.color = new Color(0f, 0f, 0f, 0.35f); cImg.raycastTarget = true;
            var cBtn = catcher.AddComponent<Button>(); cBtn.transition = Selectable.Transition.None; cBtn.onClick.AddListener(Close);

            var panel = NewChild(_root.transform, "panel");
            var prt = (RectTransform)panel.transform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(w, h);

            // Anchor just below the pouch slot (top-left of the panel = bottom-left of the slot),
            // clamped on-screen. Falls back to screen-centre when no anchor is supplied.
            var canvasRect = (RectTransform)canvas.transform;
            if (anchor != null)
            {
                prt.pivot = new Vector2(0f, 1f);
                var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                var corners = new Vector3[4];
                anchor.GetWorldCorners(corners); // 0 = bottom-left
                Vector2 screenBL = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screenBL, cam, out Vector2 localBL))
                {
                    var ap = localBL + new Vector2(0f, -4f);
                    float cw = canvasRect.rect.width, ch = canvasRect.rect.height;
                    ap.x = Mathf.Clamp(ap.x, -cw / 2f + 4f, cw / 2f - w - 4f);
                    ap.y = Mathf.Clamp(ap.y, -ch / 2f + h + 4f, ch / 2f - 4f);
                    prt.anchoredPosition = ap;
                }
                else { prt.pivot = new Vector2(0.5f, 0.5f); prt.anchoredPosition = Vector2.zero; }
            }
            else
            {
                prt.pivot = new Vector2(0.5f, 0.5f);
                prt.anchoredPosition = Vector2.zero;
            }
            var pImg = panel.AddComponent<Image>(); pImg.color = new Color(0.13f, 0.10f, 0.07f, 0.98f); pImg.raycastTarget = true;
            var pol = panel.AddComponent<Outline>(); pol.effectColor = new Color(0.55f, 0.42f, 0.20f, 1f); pol.effectDistance = new Vector2(2f, -2f);

            var title = NewChild(panel.transform, "title");
            var trt = (RectTransform)title.transform;
            trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(1f, 1f); trt.pivot = new Vector2(0.5f, 1f);
            trt.offsetMin = new Vector2(0f, -(titleH + 4f)); trt.offsetMax = new Vector2(0f, -4f);
            var ttmp = title.AddComponent<TextMeshProUGUI>();
            if (font != null) ttmp.font = font;
            ttmp.text = "Coin Pouch"; ttmp.fontSize = 16f; ttmp.color = new Color(0.93f, 0.78f, 0.37f);
            ttmp.alignment = TextAlignmentOptions.Center; ttmp.raycastTarget = false;

            float x0 = pad, y0 = -(titleH + pad);
            for (int i = 0; i < contents.Count; i++)
            {
                var (key, item) = contents[i];
                int r = i / cols, c = i % cols;
                MakeCell(panel.transform, key, item,
                    string.Equals(key, displayed, System.StringComparison.OrdinalIgnoreCase),
                    font, x0 + c * (cell + gap), y0 - r * (cell + gap), cell);
            }
        }

        private static void MakeCell(Transform parent, string key, ItemDrop.ItemData item, bool displayed, TMP_FontAsset font, float x, float y, float size)
        {
            var go = NewChild(parent, "cell");
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y); rt.sizeDelta = new Vector2(size, size);
            var bg = go.AddComponent<Image>();
            bg.color = displayed ? new Color(0.30f, 0.24f, 0.11f, 0.95f) : new Color(0.19f, 0.16f, 0.12f, 0.9f);
            bg.raycastTarget = true;
            if (displayed)
            {
                var ol = go.AddComponent<Outline>();
                ol.effectColor = new Color(0.93f, 0.78f, 0.37f, 1f); ol.effectDistance = new Vector2(1.5f, -1.5f);
            }

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

        private static void Stretch(GameObject go)
        {
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        private class PopupWatcher : MonoBehaviour
        {
            private void Update()
            {
                if (!InventoryGui.IsVisible() || Input.GetKeyDown(KeyCode.Escape))
                    Close();
            }
        }
    }
}
