using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // `vainv_dumpui` - writes the LIVE inventory hierarchy with every rect to a file.
    //
    // The editor-side dump reads the vanilla prefab; this reads what is actually on screen, after our
    // layout has been applied and after GuiScaler has set the canvas scale. That is the only way to see
    // where a child really ended up, which is what tuning the layout needs.
    internal static class UiDump
    {
        const string CommandName = "vainv_dumpui";
        static bool registered;

        internal static void Register()
        {
            if (registered) return;
            registered = true;
            // The ConsoleCommand constructor registers itself, so no Terminal patch is needed.
            new Terminal.ConsoleCommand(CommandName,
                "dump the live inventory UI hierarchy + rects to BepInEx/config/VAInventory_uidump[_suffix].txt; add 'open' to open the inventory first",
                args => Run(args), isCheat: true);   // 1.0.74: admin/devcommands only
        }

        static void Run(Terminal.ConsoleEventArgs args)
        {
            string suffix = null;
            bool open = false;
            for (int i = 1; args != null && i < args.Length; i++)
            {
                if (string.Equals(args[i], "open", StringComparison.OrdinalIgnoreCase)) open = true;
                else suffix = args[i];
            }

            // An unattended run never opens the inventory, so a dump taken then records the CLOSED state:
            // every panel parent-off, positions left wherever they happened to be. "open" makes the dump
            // drive the screen itself, so a traverse can produce real UI evidence with no input at all.
            if (open && InventoryGui.instance != null && !InventoryGui.IsVisible() && VAInventory.Instance != null)
            {
                VAInventory.Instance.StartCoroutine(OpenDumpClose(suffix));
                return;
            }

            Dump(suffix);
        }

        static IEnumerator OpenDumpClose(string suffix)
        {
            InventoryGui.instance.Show(null);
            // Show builds the hierarchy synchronously, but layout groups and the reveal animation need
            // frames before the rects read true.
            yield return null;
            yield return null;
            yield return null;
            Dump(suffix);
            InventoryGui.instance.Hide();
        }

        static void Dump(string suffix)
        {
            var gui = InventoryGui.instance;
            if (gui == null || gui.m_inventoryRoot == null)
            {
                Debug.LogWarning("[VAInventory] dumpui: no InventoryGui - open the inventory first");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("# VAInventory live UI dump  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("# layout=" + (ConfigManager.Instance?.configUiLayout?.Value ?? "?")
                          + "  screen=" + Screen.width + "x" + Screen.height);

            var canvas = gui.GetComponentInParent<Canvas>();
            var scaler = canvas != null ? canvas.GetComponent<CanvasScaler>() : null;
            if (scaler != null) sb.AppendLine("# canvas scaleFactor=" + scaler.scaleFactor.ToString("0.###")
                                              + "  (canvas units = screen px / scaleFactor)");
            if (gui.m_inventoryRoot is RectTransform rootRect)
                sb.AppendLine("# root rect = " + rootRect.rect.width.ToString("0") + " x " + rootRect.rect.height.ToString("0") + " units");
            sb.AppendLine();
            sb.AppendLine("# columns: name [state]  aMin aMax pivot pos size scale | worldRect(x0,y0 -> x1,y1 in px)");
            sb.AppendLine();

            Walk(gui.m_inventoryRoot, 0, sb);

            var dir = BepInEx.Paths.ConfigPath;
            var clean = string.IsNullOrEmpty(suffix) ? null : new string(suffix.Where(char.IsLetterOrDigit).ToArray());
            var path = Path.Combine(dir, string.IsNullOrEmpty(clean)
                ? "VAInventory_uidump.txt"
                : "VAInventory_uidump_" + clean + ".txt");
            try
            {
                File.WriteAllText(path, sb.ToString());
                Debug.LogWarning("[VAInventory] dumpui -> " + path);
            }
            catch (Exception exception)
            {
                Debug.LogError("[VAInventory] dumpui failed: " + exception.Message);
            }
        }

        static void Walk(Transform t, int depth, StringBuilder sb)
        {
            var indent = new string(' ', depth * 2);
            var rect = t as RectTransform;

            var state = t.gameObject.activeSelf ? "" : " [inactive]";
            if (t.gameObject.activeSelf && !t.gameObject.activeInHierarchy) state = " [parent-off]";

            sb.Append(indent).Append(t.name).Append(state);

            if (rect != null)
            {
                sb.Append("  aMin").Append(V(rect.anchorMin))
                  .Append(" aMax").Append(V(rect.anchorMax))
                  .Append(" piv").Append(V(rect.pivot))
                  .Append(" pos").Append(V(rect.anchoredPosition))
                  .Append(" size").Append(V(rect.sizeDelta));

                if (rect.localScale != Vector3.one)
                    sb.Append(" scale").Append(V(rect.localScale));

                sb.Append(" | ").Append(WorldRect(rect));
            }

            // Things that explain a blank cell: no sprite, no font, or fully transparent.
            var image = t.GetComponent<Image>();
            if (image != null)
                sb.Append("  <Image sprite=").Append(image.sprite != null ? image.sprite.name : "NULL")
                  .Append(" a=").Append(image.color.a.ToString("0.##")).Append(">");
            var raw = t.GetComponent<RawImage>();
            if (raw != null)
                sb.Append("  <RawImage tex=").Append(raw.texture != null ? raw.texture.name : "NULL")
                  .Append(" a=").Append(raw.color.a.ToString("0.##")).Append(">");
            var text = t.GetComponent<TextMeshProUGUI>();
            if (text != null)
                sb.Append("  <TMP font=").Append(text.font != null ? text.font.name : "NULL")
                  .Append(" \"").Append(Trim(text.text)).Append("\">");

            sb.AppendLine();

            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb);
        }

        static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ");
            return s.Length > 24 ? s.Substring(0, 24) + "..." : s;
        }

        static string V(Vector2 v) => "(" + v.x.ToString("0.##") + "," + v.y.ToString("0.##") + ")";
        static string V(Vector3 v) => "(" + v.x.ToString("0.##") + "," + v.y.ToString("0.##") + ")";

        // Screen pixels, so a value here can be compared directly against what is on the monitor.
        static string WorldRect(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return "(" + corners[0].x.ToString("0") + "," + corners[0].y.ToString("0")
                   + " -> " + corners[2].x.ToString("0") + "," + corners[2].y.ToString("0") + ")";
        }
    }
}
