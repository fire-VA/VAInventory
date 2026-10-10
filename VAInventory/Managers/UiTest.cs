using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // `ui_test` - the UI / inventory / Core UI area's F5 test (Tools\FIRES_F5_TESTS.md, Fire 2026-09-28): the character
    // preview draws only the player, the panel's squares open without a one-frame flash, the config window's cold and
    // warm open time, and the keybind conflicts list. World-neutral: it spawns nothing, and it puts back the inventory's
    // open state, the preview's view and the config window's open state. The config and keybind steps SKIP without
    // FiresUnifiedCore (a soft dependency; UiTestCore is only ever called when it is loaded).
    internal static class UiTest
    {
        const string CommandName = "ui_test";
        const string Tag = "[UiTest]";
        const string CoreGuid = "com.Fire.FiresUnifiedCore";
        const string ResultsFolder = "FiresTests";
        const string ResultsFile = "ui_test.txt";
        const int EstimateSeconds = 15;
        const int SettleFrames = 3;
        const int FirstFrameWaitLimit = 10;
        const int BaselineFrames = 10;
        const float StallMs = 200f;
        const float ClearAlpha = 0.02f;
        const int CornerInset = 4;
        const int ShaHexLength = 12;
        const int ListedLimit = 3;
        const float MsPerSecond = 1000f;

        static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(VAInventory.PluginName);
        static bool registered;
        static bool running;
        static volatile string sha;
        const float ShaWaitSeconds = 5f;

        sealed class Step
        {
            internal readonly string Name;
            internal readonly Func<Step, IEnumerator> Run;
            internal string Outcome;
            internal string Detail;

            internal Step(string name, Func<Step, IEnumerator> run)
            {
                Name = name;
                Run = run;
            }

            internal void Pass(string detail) { Outcome = "PASS"; Detail = detail; }
            internal void Fail(string detail) { Outcome = "FAIL"; Detail = detail; }
            internal void Skip(string detail) { Outcome = "SKIP"; Detail = detail; }
        }

        static List<Step> Steps() => new List<Step>
        {
            new Step("preview_camera", PreviewCamera),
            new Step("preview_only_player", PreviewOnlyPlayer),
            new Step("preview_background", PreviewBackground),
            new Step("no_flash", NoFlash),
            new Step("config_open_cold", ConfigOpenCold),
            new Step("config_open_warm", ConfigOpenWarm),
            new Step("keybind_conflicts", KeybindConflicts),
            new Step("chest_panel", ChestPanel),
        };

        internal static void Register()
        {
            if (registered) return;
            registered = true;
            StartSha();
            new Terminal.ConsoleCommand(CommandName,
                "UI / inventory / config window checks (list | <step...>); PASS/FAIL per step, results in BepInEx/FiresTests/ui_test.txt",
                args => Run(args), isCheat: true);   // 1.0.74: admin/devcommands only
        }

        static void Run(Terminal.ConsoleEventArgs args)
        {
            var steps = Steps();
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; args != null && i < args.Length; i++)
            {
                if (string.Equals(args[i], "list", StringComparison.OrdinalIgnoreCase))
                {
                    Say($"{Tag} steps: " + string.Join(", ", steps.ConvertAll(s => s.Name)));
                    return;
                }
                if (!string.Equals(args[i], "keep", StringComparison.OrdinalIgnoreCase)) wanted.Add(args[i]);
            }
            if (wanted.Count > 0) steps.RemoveAll(s => !wanted.Contains(s.Name));
            if (running)
            {
                Say($"{Tag} already running.");
                return;
            }
            if (VAInventory.Instance == null) return;
            VAInventory.Instance.StartCoroutine(RunAll(steps));
        }

        static IEnumerator RunAll(List<Step> steps)
        {
            running = true;
            float started = Time.realtimeSinceStartup;
            while (sha == null && Time.realtimeSinceStartup - started < ShaWaitSeconds) yield return null;
            string player = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "none";
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "none";
            Say($"{Tag} BEGIN {VAInventory.PluginName} {VAInventory.PluginVersion} ({sha ?? "hashing"}) on client '{player}' world '{world}': "
                + $"{steps.Count} steps, ~{EstimateSeconds} s");
            bool inventoryWasOpen = InventoryGui.IsVisible();
            var view = CharacterPreview.SaveView();
            int pass = 0, fail = 0, skip = 0;
            try
            {
                for (int i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    // Nested routines are driven here rather than handed to Unity, so a throw anywhere in a step is
                    // that step's FAIL and the run goes on.
                    var stack = new Stack<IEnumerator>();
                    stack.Push(step.Run(step));
                    while (stack.Count > 0)
                    {
                        var top = stack.Peek();
                        bool more;
                        try { more = top.MoveNext(); }
                        catch (Exception ex)
                        {
                            step.Fail($"threw {ex.GetType().Name}: {ex.Message}");
                            Record($"{step.Name}: {ex}");
                            break;
                        }
                        if (!more) { stack.Pop(); continue; }
                        if (top.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        yield return top.Current;
                    }
                    if (step.Outcome == null) step.Fail("finished without a result");
                    if (step.Outcome == "PASS") pass++;
                    else if (step.Outcome == "FAIL") fail++;
                    else skip++;
                    Say($"{Tag} {i + 1}/{steps.Count} {step.Name} {step.Outcome} - {step.Detail}");
                }
            }
            finally
            {
                CharacterPreview.RestoreView(view);
                if (InventoryGui.instance != null)
                {
                    if (inventoryWasOpen && !InventoryGui.IsVisible()) InventoryGui.instance.Show(null);
                    else if (!inventoryWasOpen && InventoryGui.IsVisible()) InventoryGui.instance.Hide();
                }
                running = false;
            }
            float seconds = Time.realtimeSinceStartup - started;
            Say($"{Tag} END {(fail > 0 ? "FAIL" : "PASS")}: {pass} pass, {fail} fail, {skip} skip in {seconds:0.0} s");
        }

        // ── Steps ───────────────────────────────────────────────────────────
        static bool InGame(Step step)
        {
            if (Player.m_localPlayer != null && InventoryGui.instance != null) return true;
            step.Skip("no local player or inventory screen (run it in a world)");
            return false;
        }

        // Fire, 2026-09-30: "containers just dont show when opened". Opens the nearest chest the way E does (Container.Interact,
        // so the owner hand-over runs too) several times in a row: each open must show the chest panel fully on screen, at the
        // same spot. 1.0.48 and older sank the panel by [UI] ContainerOffset on every open.
        const float ChestSearchMetres = 30f;
        const int ChestOpens = 4;
        const float ChestOpenWaitSeconds = 3f;
        const int ChestOpenAttempts = 3;
        const float ChestSettleSeconds = 20f;
        const float ChestReachMargin = 1.5f;
        const float ChestStandMetres = 1.5f;
        const float ChestDriftLimit = 1f;

        static bool ChestPanelUp(InventoryGui gui) => gui.m_container != null && gui.m_container.gameObject.activeInHierarchy;

        static string ChestOwner(ZNetView nview)
        {
            if (nview == null || !nview.IsValid()) return "no ZDO";
            if (nview.IsOwner()) return "this client";
            return nview.GetZDO().HasOwner() ? "peer " + nview.GetZDO().GetOwner() : "nobody";
        }

        static IEnumerator ChestPanel(Step step)
        {
            if (!InGame(step)) yield break;
            var player = Player.m_localPlayer;
            Container chest = null;
            float best = ChestSearchMetres;
            foreach (var candidate in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
            {
                if (candidate == null || candidate.GetInventory() == null || candidate.IsInUse()) continue;
                if (candidate.GetComponentInParent<Player>() != null) continue;
                float metres = Vector3.Distance(player.transform.position, candidate.transform.position);
                if (metres < best) { best = metres; chest = candidate; }
            }
            if (chest == null) { step.Skip($"no free chest within {ChestSearchMetres:0} m to open"); yield break; }

            // 1.0.50: vanilla closes an open chest the next frame once the player is farther than InventoryGui's
            // m_autoCloseDistance (4 m), so the 1.0.49 step's open from 6 m (proof round 22:27) could never show. A player
            // presses E within reach: stand the tester beside the chest first, on the side it is already on.
            float reach = Mathf.Max(1f, InventoryGui.instance.m_autoCloseDistance - ChestReachMargin);
            if (best > reach)
            {
                Vector3 away = player.transform.position - chest.transform.position;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = chest.transform.forward;
                Vector3 spot = chest.transform.position + away.normalized * ChestStandMetres + Vector3.up * 0.1f;
                Record($"chest_panel: '{chest.m_name}' is {best:0.0} m away, past the {InventoryGui.instance.m_autoCloseDistance:0} m auto-close; "
                    + $"standing the tester {ChestStandMetres:0.0} m from it");
                player.TeleportTo(spot, Quaternion.LookRotation(-away.normalized), false);
                best = ChestStandMetres;
            }

            // 1.0.50: right after a teleport (a 'goto' in the round chain) the chest's ZDO has no owner yet, so vanilla sends the
            // open request to everybody and every peer answers "but im not the owner" (proof round 22:22): wait that out first.
            var nview = chest.GetComponent<ZNetView>();
            float settleUntil = Time.realtimeSinceStartup + ChestSettleSeconds;
            while (Time.realtimeSinceStartup < settleUntil
                   && (player.IsTeleporting() || nview == null || !nview.IsValid() || !nview.GetZDO().HasOwner()))
                yield return null;
            if (player.IsTeleporting()) { step.Skip($"still teleporting after {ChestSettleSeconds:0} s"); yield break; }

            var gui = InventoryGui.instance;
            var positions = new List<Vector2>();
            for (int open = 1; open <= ChestOpens; open++)
            {
                string owner = "?";
                for (int attempt = 1; attempt <= ChestOpenAttempts; attempt++)
                {
                    if (InventoryGui.IsVisible()) gui.Hide();
                    for (int frame = 0; frame < SettleFrames; frame++) yield return null;
                    owner = ChestOwner(nview);
                    Record($"chest_panel open {open} try {attempt}: owner {owner}, in use {chest.IsInUse()}");
                    chest.Interact(player, false, false);
                    float until = Time.realtimeSinceStartup + ChestOpenWaitSeconds;
                    while (Time.realtimeSinceStartup < until && !ChestPanelUp(gui)) yield return null;
                    if (ChestPanelUp(gui)) break;
                }
                for (int frame = 0; frame < SettleFrames; frame++) yield return null;
                if (!ChestPanelUp(gui))
                {
                    // Vanilla's Container.RPC_RequestOpen logs its refusal on the OWNER ("  in use", "  not yours",
                    // "  but im not the owner"): read it there.
                    step.Fail($"open {open} of '{chest.m_name}' ({best:0} m): no chest panel after {ChestOpenAttempts} tries of "
                        + $"{ChestOpenWaitSeconds:0} s (container open {gui.IsContainerOpen()}, chest owner {owner}, in use {chest.IsInUse()}, "
                        + $"inventory shown {InventoryGui.IsVisible()}; vanilla's refusal line is in the owner's log)");
                    gui.Hide();
                    yield break;
                }
                owner = ChestOwner(nview);
                string off = OffScreenEdges(gui.m_container);
                Vector2 at = gui.m_container.anchoredPosition;
                Record($"chest_panel open {open}: pos ({at.x:0.#},{at.y:0.#}), owner {owner}, {off ?? "fully on screen"}");
                if (off != null)
                {
                    step.Fail($"open {open} of '{chest.m_name}': the chest panel is off screen ({off}) at ({at.x:0},{at.y:0})");
                    gui.Hide();
                    yield break;
                }
                positions.Add(at);
            }
            gui.Hide();

            float drift = 0f;
            foreach (var at in positions) drift = Mathf.Max(drift, Vector2.Distance(at, positions[0]));
            Vector2 offset = ConfigManager.Instance?.configContainerOffset?.Value ?? Vector2.zero;
            if (drift > ChestDriftLimit)
                step.Fail($"the chest panel moved {drift:0} units across {ChestOpens} opens (ContainerOffset ({offset.x:0.#},{offset.y:0.#}))");
            else
                step.Pass($"{ChestOpens} opens of '{chest.m_name}' ({best:0} m): the chest panel showed fully on screen at "
                    + $"({positions[0].x:0},{positions[0].y:0}) every time (ContainerOffset ({offset.x:0.#},{offset.y:0.#}))");
        }

        // Null when every corner of the panel lies inside the screen, else which edges stick out.
        static string OffScreenEdges(RectTransform panel)
        {
            var corners = new Vector3[4];
            panel.GetWorldCorners(corners);
            var canvas = panel.GetComponentInParent<Canvas>();
            var screen = canvas != null ? canvas.rootCanvas.transform as RectTransform : null;
            if (screen == null) return null;
            var bounds = new Vector3[4];
            screen.GetWorldCorners(bounds);
            var edges = new List<string>();
            if (corners[0].x < bounds[0].x - 0.5f) edges.Add("left");
            if (corners[2].x > bounds[2].x + 0.5f) edges.Add("right");
            if (corners[0].y < bounds[0].y - 0.5f) edges.Add("bottom");
            if (corners[2].y > bounds[2].y + 0.5f) edges.Add("top");
            return edges.Count == 0 ? null : string.Join("+", edges);
        }

        static IEnumerator OpenInventory()
        {
            if (!InventoryGui.IsVisible()) InventoryGui.instance.Show(null);
            for (int frame = 0; frame < SettleFrames; frame++) yield return null;
        }

        static IEnumerator PreviewCamera(Step step)
        {
            if (!InGame(step)) yield break;
            yield return OpenInventory();
            var cam = CharacterPreview.PreviewCamera;
            int layer = CharacterPreview.IsolatedLayer;
            if (cam == null) { step.Fail("no preview camera with the inventory open"); yield break; }
            if (layer < 0) { step.Fail("no free layer: the preview fell back to the player's own layers"); yield break; }
            var light = CharacterPreview.PreviewLight;
            int only = 1 << layer;
            if (!cam.enabled) step.Fail("the preview camera is off with the inventory open");
            else if (cam.cullingMask != only) step.Fail($"camera mask 0x{cam.cullingMask:X} is not layer {layer} alone");
            else if (light != null && light.cullingMask != only) step.Fail($"key light mask 0x{light.cullingMask:X} is not layer {layer} alone");
            else if (!string.IsNullOrEmpty(LayerMask.LayerToName(layer))) step.Fail($"layer {layer} is named '{LayerMask.LayerToName(layer)}' by the game");
            else step.Pass($"camera and key light see only unnamed layer {layer}");
        }

        // Between renders nothing may rest on the private layer: the player's renderers go back after each preview
        // render, and nothing else is ever put there.
        static IEnumerator PreviewOnlyPlayer(Step step)
        {
            if (!InGame(step)) yield break;
            yield return OpenInventory();
            int layer = CharacterPreview.IsolatedLayer;
            if (layer < 0) { step.Skip("no private layer (see preview_camera)"); yield break; }
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            var found = new List<string>();
            foreach (var renderer in renderers)
                if (renderer != null && renderer.gameObject.layer == layer) found.Add(renderer.name);
            if (found.Count == 0) step.Pass($"no renderer rests on layer {layer} between renders ({renderers.Length} scanned)");
            else
            {
                step.Fail($"{found.Count} renderer(s) rest on layer {layer}: " + string.Join(", ", found.GetRange(0, Math.Min(ListedLimit, found.Count))));
                Record("preview_only_player on the layer: " + string.Join(", ", found));
            }
        }

        // At the default view the image's corners are background: they must be clear, not the world. A covered corner is
        // looked at again after RecheckFrames, and the player's own renderers reaching into it are named: the isolation
        // draws everything under the player (gear, hair, a torch's flame, status-effect particles), so a corner covered by
        // one of those is the player, not the world (R4, 2026-09-28: one corner at alpha 1 with a bot standing nearby).
        const int RecheckFrames = 15;

        static IEnumerator PreviewBackground(Step step)
        {
            if (!InGame(step)) yield break;
            yield return OpenInventory();
            CharacterPreview.ResetView();
            for (int frame = 0; frame < SettleFrames; frame++) yield return null;
            var texture = CharacterPreview.PreviewTexture;
            if (texture == null) { step.Fail("no preview texture"); yield break; }
            var first = SampleCorners(texture);
            if (Covered(first) == 0) { step.Pass($"{first.Length}/{first.Length} corners clear at the default view"); yield break; }
            for (int frame = 0; frame < RecheckFrames; frame++) yield return null;
            var again = SampleCorners(texture);
            string alphas = string.Join(", ", Array.ConvertAll(again, c => c.a.ToString("0.00")));
            if (Covered(again) == 0)
            {
                step.Pass($"{first.Length}/{first.Length} corners clear after {RecheckFrames} more frames (one frame had "
                          + $"{Covered(first)} covered: alpha {string.Join(", ", Array.ConvertAll(first, c => c.a.ToString("0.00")))})");
                yield break;
            }
            var own = PlayerRenderersInCorners(texture, again);
            Record("preview_background: player renderers reaching the covered corner(s): " + (own.Count == 0 ? "none" : string.Join(", ", own)));
            if (own.Count > 0)
                step.Pass($"{Covered(again)}/{again.Length} corners covered by the player's own "
                          + string.Join(", ", own.GetRange(0, Math.Min(ListedLimit, own.Count))) + $" (alpha {alphas}); nothing of the world");
            else
                step.Fail($"{Covered(again)}/{again.Length} corners show something that is not the player (alpha {alphas})");
        }

        // Bottom-left, bottom-right, top-left, top-right, CornerInset pixels in.
        static Color[] SampleCorners(RenderTexture texture)
        {
            var previous = RenderTexture.active;
            var read = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = texture;
                read.ReadPixels(new Rect(0f, 0f, texture.width, texture.height), 0, 0);
                int right = texture.width - 1 - CornerInset, top = texture.height - 1 - CornerInset;
                return new[]
                {
                    read.GetPixel(CornerInset, CornerInset), read.GetPixel(right, CornerInset),
                    read.GetPixel(CornerInset, top), read.GetPixel(right, top),
                };
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.Destroy(read);
            }
        }

        static int Covered(Color[] corners)
        {
            int covered = 0;
            foreach (var corner in corners) if (corner.a > ClearAlpha) covered++;
            return covered;
        }

        // The player's drawn renderers whose screen bounds (in the preview camera) reach a covered corner, by path.
        static List<string> PlayerRenderersInCorners(RenderTexture texture, Color[] corners)
        {
            var found = new List<string>();
            var cam = CharacterPreview.PreviewCamera;
            var player = Player.m_localPlayer;
            if (cam == null || player == null) return found;
            float inX = (float)CornerInset / texture.width, inY = (float)CornerInset / texture.height;
            var points = new[] { new Vector2(inX, inY), new Vector2(1f - inX, inY), new Vector2(inX, 1f - inY), new Vector2(1f - inX, 1f - inY) };
            foreach (var renderer in player.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled) continue;
                var box = ViewportBox(cam, renderer.bounds);
                for (int i = 0; i < corners.Length; i++)
                {
                    if (corners[i].a <= ClearAlpha || !box.Contains(points[i])) continue;
                    found.Add(PathUnder(renderer.transform, player.transform));
                    break;
                }
            }
            return found;
        }

        static Rect ViewportBox(Camera cam, Bounds bounds)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var point = cam.WorldToViewportPoint(corner);
                if (point.z <= 0f) continue;
                minX = Mathf.Min(minX, point.x); maxX = Mathf.Max(maxX, point.x);
                minY = Mathf.Min(minY, point.y); maxY = Mathf.Max(maxY, point.y);
            }
            return minX > maxX ? Rect.zero : Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        static string PathUnder(Transform leaf, Transform root)
        {
            var path = leaf.name;
            for (var t = leaf.parent; t != null && t != root; t = t.parent) path = t.name + "/" + path;
            return path;
        }

        // The panel is rebuilt when the inventory opens; at the first frame drawn after that, no empty square may show
        // its overlays (durability, food, no-teleport, quality, queued, selected, equipped).
        static IEnumerator NoFlash(Step step)
        {
            if (!InGame(step)) yield break;
            if (InventoryGui.IsVisible()) InventoryGui.instance.Hide();
            for (int frame = 0; frame < SettleFrames; frame++) yield return null;
            int showing = -1, empty = 0;
            Canvas.WillRenderCanvases firstFrame = null;
            firstFrame = () =>
            {
                Canvas.willRenderCanvases -= firstFrame;
                showing = EquipmentPanel.EmptySquaresShowingOverlays(out empty);
            };
            Canvas.willRenderCanvases += firstFrame;
            try
            {
                InventoryGui.instance.Show(null);
                for (int frame = 0; frame < FirstFrameWaitLimit && showing < 0; frame++) yield return null;
            }
            finally { Canvas.willRenderCanvases -= firstFrame; }
            if (showing < 0) step.Fail("the inventory drew no frame after opening");
            else if (empty == 0) step.Skip("every panel square holds an item: no empty square to watch");
            else if (showing == 0) step.Pass($"{empty} empty square(s) clean on the first frame");
            else step.Fail($"{showing} of {empty} empty square(s) showed their overlays on the first frame");
        }

        static bool CoreLoaded(Step step)
        {
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(CoreGuid)) return true;
            step.Skip("FiresUnifiedCore is not loaded");
            return false;
        }

        static IEnumerator ConfigOpenCold(Step step)
        {
            if (!CoreLoaded(step)) yield break;
            bool? opened = UiTestCore.ConfigWindowEverOpened();
            if (opened == null)
            {
                step.Skip("this FiresUnifiedCore cannot tell a first open (needs ConfigPanel.EverOpened, Core 0.2.122+)");
                yield break;
            }
            if (opened.Value)
            {
                step.Skip("the config window was opened earlier this session; run 'ui_test config_open_cold' right after joining");
                yield break;
            }
            yield return MeasureConfigOpen(step, "cold (first open this session)");
        }

        // Warm is a second open: when the window has not been shown yet this session (or Core cannot say), one unmeasured
        // open and close comes first, so the first open's one-off work is never counted here.
        static IEnumerator ConfigOpenWarm(Step step)
        {
            if (!CoreLoaded(step)) yield break;
            string which = "warm";
            if (UiTestCore.ConfigWindowEverOpened() != true)
            {
                yield return PrimeConfigWindow();
                which = "warm (after one unmeasured open)";
            }
            yield return MeasureConfigOpen(step, which);
        }

        static IEnumerator PrimeConfigWindow()
        {
            bool wasOpen = UiTestCore.ConfigWindowOpen();
            if (!wasOpen) UiTestCore.ToggleConfigWindow();
            for (int frame = 0; frame < SettleFrames; frame++) yield return null;
            if (UiTestCore.ConfigWindowOpen() != wasOpen) UiTestCore.ToggleConfigWindow();
            for (int frame = 0; frame < SettleFrames; frame++) yield return null;
        }

        // The frame the window opens in, against the frames before it. A stall report starts at StallMs.
        static IEnumerator MeasureConfigOpen(Step step, string which)
        {
            if (Player.m_localPlayer == null) { step.Skip("no local player (run it in a world)"); yield break; }
            bool wasOpen = UiTestCore.ConfigWindowOpen();
            if (wasOpen) UiTestCore.ToggleConfigWindow();
            float slowest = 0f;
            for (int frame = 0; frame < BaselineFrames; frame++)
            {
                yield return null;
                slowest = Mathf.Max(slowest, Time.unscaledDeltaTime * MsPerSecond);
            }
            float before = Time.realtimeSinceStartup;
            UiTestCore.ToggleConfigWindow();
            yield return null;
            float openMs = (Time.realtimeSinceStartup - before) * MsPerSecond;
            bool opened = UiTestCore.ConfigWindowOpen();
            for (int frame = 0; frame < SettleFrames; frame++) yield return null;
            if (UiTestCore.ConfigWindowOpen() != wasOpen) UiTestCore.ToggleConfigWindow();
            if (!opened) step.Fail("the config window did not open");
            else if (openMs >= StallMs) step.Fail($"{which} open took {openMs:0} ms (a stall; frames before it up to {slowest:0} ms)");
            else step.Pass($"{which} open {openMs:0} ms (frames before it up to {slowest:0} ms)");
        }

        static IEnumerator KeybindConflicts(Step step)
        {
            if (!CoreLoaded(step)) yield break;
            int total = UiTestCore.KeybindConflicts(out int unresolved, out string list);
            Record("keybind conflicts:" + Environment.NewLine + list);
            step.Pass($"{total} conflict(s), {unresolved} not ignored (list in {ResultsFile})");
            yield break;
        }

        // ── Output ──────────────────────────────────────────────────────────
        static void Say(string line)
        {
            Log.LogMessage(line);
            if (global::Console.instance != null) global::Console.instance.Print(line);
            Record(line);
        }

        static void Record(string text)
        {
            try
            {
                string folder = Path.Combine(Paths.BepInExRootPath, ResultsFolder);
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, ResultsFile), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
            }
            catch (Exception ex) { Log.LogWarning($"{Tag} could not write {ResultsFile}: {ex.Message}"); }
        }

        // The BEGIN line's DLL hash, on a pool thread from load (Tools\FIRES_F5_TESTS.md: never on the main thread;
        // SHA256 runs ~11 ms per MB).
        static void StartSha()
        {
            string path = typeof(UiTest).Assembly.Location;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string hash;
                try
                {
                    using (var hasher = SHA256.Create())
                    using (var file = File.OpenRead(path))
                        hash = BitConverter.ToString(hasher.ComputeHash(file)).Replace("-", "").Substring(0, ShaHexLength);
                }
                catch { hash = "unknown"; }
                sha = hash;
            });
        }
    }
}
