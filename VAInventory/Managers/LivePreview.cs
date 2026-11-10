using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // CharacterPreview's live render as an INSTANCE, so more than one can exist (the player panel, the companion panel, and
    // 's Dressing NPC via VAInventoryPreviewApi). Same technique, moved here unchanged:
    //   - the camera is a CHILD of the target (inherits its rotation, no follow code) and renders into a texture shaped like the mount;
    //   - only the target is drawn: its renderers move to an empty layer just before THIS camera culls and back just after it renders;
    //   - the directional key light is on only during this camera's own render (1.0.149);
    //   - wheel zoom, left-drag spin/tilt, right/middle-drag move, double-click reset (LivePreviewControls).
    // FrameToBounds (for non-human targets, e.g. FCC creature models): the default distance/height come from the target's renderer
    // bounds so the whole body is in frame; humans keep the player panel's 3.4 m / 1.0 m.
    internal sealed class LivePreview
    {
        const int TextureHeight = 700;
        const int MinTextureWidth = 200;
        const int MaxTextureWidth = 1400;
        const float FieldOfView = 32f;
        const float NearClip = 0.1f;
        const float FarMargin = 2f;

        internal const float HumanDistance = 3.4f;
        internal const float HumanHeight = 1.0f;
        const float MinDistance = 1.2f;
        const float MaxDistance = 12f;
        const float MinHeight = 0.1f;
        const float MaxHeight = 4f;
        const float MaxSideOffset = 1.5f;
        const float MinPitch = -30f;
        const float MaxPitch = 60f;
        const float SpinDegreesPerPixel = 0.4f;
        const float TiltDegreesPerPixel = 0.25f;
        const float PanMetresPerPixel = 0.004f;
        const float ZoomStep = 0.9f;
        const int FirstUserLayer = 8;
        const int LastLayer = 31;
        const int NoLayer = -1;

        static readonly int[] PreferredLayers = { 7, 6 };
        static readonly List<LivePreview> s_all = new List<LivePreview>();
        static bool s_hooked;

        internal readonly string Name;
        internal Camera Cam;
        internal Light KeyLight;
        internal RenderTexture Texture;
        internal RawImage Target;
        internal Character BoundTo;
        internal int IsolatedLayer = NoLayer;
        ZDOID boundZdo = ZDOID.None;
        readonly List<Renderer> isolated = new List<Renderer>();
        readonly List<int> isolatedFrom = new List<int>();

        internal float Yaw, Pitch, Distance = HumanDistance, Height = HumanHeight, Side;
        float defaultDistance = HumanDistance, defaultHeight = HumanHeight;
        bool frameToBounds;

        internal LivePreview(string name) { Name = name; }

        internal bool Bind(Character character, RawImage mount, bool frameBounds)
        {
            Target = mount;
            if (Target == null || character == null) return false;
            EnsureTexture(Target.rectTransform.rect);
            if (Cam != null && BoundTo != character) Teardown();
            frameToBounds = frameBounds;
            if (Cam == null) Build(character);
            if (Cam == null) return false;
            AttachTexture();
            Target.color = Color.white;
            Target.raycastTarget = true;
            var controls = Target.GetComponent<LivePreviewControls>() ?? Target.gameObject.AddComponent<LivePreviewControls>();
            controls.Preview = this;
            SetRendering(true);
            return true;
        }

        internal void Hide() => SetRendering(false);

        internal void Teardown()
        {
            if (Cam != null) Object.Destroy(Cam.gameObject);
            Cam = null;
            KeyLight = null;
            BoundTo = null;
            if (Target != null) Target.texture = null;
            s_all.Remove(this);
        }

        internal void Spin(Vector2 pixels) { Yaw += pixels.x * SpinDegreesPerPixel; Pitch = Mathf.Clamp(Pitch - pixels.y * TiltDegreesPerPixel, MinPitch, MaxPitch); Place(); }

        internal void Pan(Vector2 pixels)
        {
            float metresPerPixel = PanMetresPerPixel * Distance / HumanDistance;
            Height = Mathf.Clamp(Height - pixels.y * metresPerPixel, MinHeight, MaxHeight);
            Side = Mathf.Clamp(Side - pixels.x * metresPerPixel, -MaxSideOffset, MaxSideOffset);
            Place();
        }

        internal void Zoom(float notchesIn) { Distance = Mathf.Clamp(Distance * Mathf.Pow(ZoomStep, notchesIn), MinDistance, MaxDistance); Place(); }

        internal void ResetView() { Yaw = 0f; Pitch = 0f; Side = 0f; Distance = defaultDistance; Height = defaultHeight; Place(); }

        internal void Refit()
        {
            if (Target == null || Cam == null) return;
            var mount = Target.rectTransform.rect;
            if (mount.width <= 0f || mount.height <= 0f) return;
            EnsureTexture(mount);
            AttachTexture();
        }

        internal void SetView(float yaw, float pitch, float distance, float height, float side) { Yaw = yaw; Pitch = pitch; Distance = distance; Height = height; Side = side; Place(); }

        /// <summary>Re-reads the target's bounds (after an equipment / model change) when framing to bounds.</summary>
        internal void Reframe() { if (frameToBounds && BoundTo != null) { FrameFromBounds(BoundTo); ResetView(); } }

        void AttachTexture()
        {
            Cam.targetTexture = Texture;
            Cam.aspect = (float)Texture.width / Texture.height;
            Target.texture = Texture;
        }

        void EnsureTexture(Rect mount)
        {
            int width = mount.height > 0f ? Mathf.Clamp(Mathf.RoundToInt(TextureHeight * mount.width / mount.height), MinTextureWidth, MaxTextureWidth) : MinTextureWidth;
            if (Texture != null && Texture.width == width) return;
            if (Texture != null) { if (Cam != null) Cam.targetTexture = null; Texture.Release(); Object.Destroy(Texture); }
            Texture = new RenderTexture(width, TextureHeight, 24, RenderTextureFormat.ARGB32) { name = "VAInventory_Preview_" + Name, antiAliasing = 2 };
            Texture.Create();
        }

        void Build(Character character)
        {
            var go = new GameObject("VAInventory_PreviewCamera_" + Name);
            go.transform.SetParent(character.transform, false);
            Cam = go.AddComponent<Camera>();
            Cam.fieldOfView = FieldOfView;
            Cam.nearClipPlane = NearClip;
            IsolatedLayer = FreeLayer();
            Cam.cullingMask = IsolatedLayer != NoLayer ? 1 << IsolatedLayer : BuildMask(character);
            Hook();
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            Cam.allowHDR = false;
            Cam.allowMSAA = false;
            Cam.targetTexture = Texture;
            Cam.enabled = false;

            var lightGo = new GameObject("VAInventory_PreviewLight_" + Name);
            lightGo.transform.SetParent(go.transform, false);
            lightGo.transform.localRotation = Quaternion.Euler(25f, 15f, 0f);
            KeyLight = lightGo.AddComponent<Light>();
            KeyLight.type = LightType.Directional;
            KeyLight.intensity = 1.1f;
            KeyLight.cullingMask = Cam.cullingMask;
            KeyLight.enabled = false;

            BoundTo = character;
            boundZdo = ZdoOf(character);   // Kept so a replaced body (same ZDO) is found again
            EquipmentPanel.PanelLog?.LogInfo($"[VAInventory] preview {Name}: model '{ModelId(character)}'");   // Once per bind / re-bind
            if (!s_all.Contains(this)) s_all.Add(this);
            if (frameToBounds) FrameFromBounds(character);
            else { defaultDistance = HumanDistance; defaultHeight = HumanHeight; }
            ResetView();
        }

        // Whole body in frame from the target's renderer bounds (local space): height = the bounds' middle, distance so the bounds'
        // height fits the vertical field of view with a margin.
        void FrameFromBounds(Character character)
        {
            var renderers = character.GetComponentsInChildren<Renderer>(false);
            if (renderers.Length == 0) { defaultDistance = HumanDistance; defaultHeight = HumanHeight; return; }
            Bounds b = renderers[0].bounds;
            foreach (var r in renderers) if (r is SkinnedMeshRenderer || r is MeshRenderer) b.Encapsulate(r.bounds);   // meshes only (no particles / trails)
            float bottom = character.transform.InverseTransformPoint(new Vector3(b.center.x, b.min.y, b.center.z)).y;
            float top = character.transform.InverseTransformPoint(new Vector3(b.center.x, b.max.y, b.center.z)).y;
            float tall = Mathf.Max(0.5f, top - Mathf.Max(0f, bottom));
            defaultHeight = Mathf.Clamp(Mathf.Max(0f, bottom) + tall * 0.5f, MinHeight, MaxHeight);
            defaultDistance = Mathf.Clamp(tall * 0.62f / Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad), MinDistance, MaxDistance);
            EquipmentPanel.PanelLog?.LogDebug($"preview {Name}: framed '{character.name}' from its bounds: {tall:0.00} m tall -> distance {defaultDistance:0.00} m, look height {defaultHeight:0.00} m");
        }

        void Place()
        {
            if (Cam == null) return;
            var right = Quaternion.Euler(0f, Yaw, 0f) * Vector3.left;
            var lookAt = new Vector3(0f, Height, 0f) + right * Side;
            var offset = Quaternion.Euler(-Pitch, Yaw, 0f) * Vector3.forward * Distance;
            Cam.transform.localPosition = lookAt + offset;
            Cam.transform.localRotation = Quaternion.LookRotation(-offset, Vector3.up);
            Cam.farClipPlane = Distance + FarMargin;
        }

        static int FreeLayer()
        {
            foreach (int layer in PreferredLayers) if (string.IsNullOrEmpty(LayerMask.LayerToName(layer))) return layer;
            for (int layer = LastLayer; layer >= FirstUserLayer; layer--) if (string.IsNullOrEmpty(LayerMask.LayerToName(layer))) return layer;
            return NoLayer;
        }

        static void Hook()
        {
            if (s_hooked) return;
            s_hooked = true;
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
            HookVisualsChanged();
        }

        // The camera
        // is a child of the bound body, so a swap that REPLACES the body destroyed it and the panel kept its last frame. Core 0.2.690's one
        // signal FiresCore.Visuals.CharacterVisuals.Changed(GameObject, reason) re-binds a preview whose body (same ZDO) was replaced, and
        // re-frames / re-fits it on every model or equipment change. Reflected: VAInventory loads without Core 0.2.690.
        static bool s_visualsHooked;
        static void HookVisualsChanged()
        {
            if (s_visualsHooked) return;
            s_visualsHooked = true;
            try
            {
                var ty = HarmonyLib.AccessTools.TypeByName("FiresCore.Visuals.CharacterVisuals");
                var ev = ty?.GetEvent("Changed");
                if (ev == null) { EquipmentPanel.PanelLog?.LogDebug("preview: Core has no CharacterVisuals.Changed (older than 0.2.690)"); return; }
                ev.AddEventHandler(null, new System.Action<GameObject, string>(OnVisualsChanged));
                EquipmentPanel.PanelLog?.LogInfo("[VAInventory] preview: listening to Core CharacterVisuals.Changed");
            }
            catch (System.Exception e) { EquipmentPanel.PanelLog?.LogWarning("preview: CharacterVisuals.Changed hook failed: " + (e.InnerException ?? e).Message); }
        }

        // What the preview shows - FCC's model swap (ZDO "fcc_model"), else Core's BodyModelState.Applied, else the body's own prefab
        static System.Reflection.FieldInfo s_coreApplied; static System.Type s_coreState; static bool s_coreLooked;
        static string ModelId(Character c)
        {
            if (c == null) return "-";
            var nv = c.GetComponent<ZNetView>(); var z = nv != null && nv.IsValid() ? nv.GetZDO() : null;
            string fcc = z != null ? z.GetString("fcc_model", "") : "";
            if (!string.IsNullOrEmpty(fcc)) return fcc;
            if (!s_coreLooked) { s_coreLooked = true; s_coreState = HarmonyLib.AccessTools.TypeByName("FiresCore.Npc.BodyModelState"); s_coreApplied = s_coreState?.GetField("Applied"); }
            var st = s_coreState != null ? c.GetComponent(s_coreState) : null;
            if (st != null && s_coreApplied?.GetValue(st) is string a && a.Length > 0) return a;
            return Utils.GetPrefabName(c.gameObject.name);
        }

        static ZDOID ZdoOf(Character c) { var nv = c != null ? c.GetComponent<ZNetView>() : null; var z = nv != null && nv.IsValid() ? nv.GetZDO() : null; return z != null ? z.m_uid : ZDOID.None; }

        static void OnVisualsChanged(GameObject go, string reason)
        {
            try
            {
                var ch = go != null ? go.GetComponent<Character>() : null;
                if (ch == null) return;
                var zid = ZdoOf(ch);
                foreach (var p in s_all.ToArray())
                {
                    bool same = p.BoundTo == ch || (zid != ZDOID.None && p.boundZdo == zid);
                    if (!same) continue;
                    if (p.BoundTo != ch || p.Cam == null)
                    {
                        var mount = p.Target; bool fb = p.frameToBounds; bool on = mount != null && mount.isActiveAndEnabled;
                        p.Teardown();
                        if (mount != null && p.Bind(ch, mount, fb) && !on) p.Hide();
                        EquipmentPanel.PanelLog?.LogInfo($"[VAInventory] preview {p.Name}: re-bound to the new body '{ch.name}' ({reason})");
                    }
                    else { p.Reframe(); EquipmentPanel.PanelLog?.LogDebug($"preview {p.Name}: {reason} changed on '{ch.name}', re-framed"); }
                    p.Refit();
                }
            }
            catch (System.Exception e) { EquipmentPanel.PanelLog?.LogWarning("preview: visuals change handling failed: " + e.Message); }
        }

        static LivePreview For(Camera c) { foreach (var p in s_all) if (p.Cam == c) return p; return null; }

        static void OnPreCull(Camera rendering) { For(rendering)?.Isolate(); }
        static void OnPostRender(Camera rendering) { For(rendering)?.Restore(); }

        // Only this preview's target, and its key light, during this camera's render; previews share the layer safely because each
        // moves its target on and off around its own render only.
        void Isolate()
        {
            if (KeyLight != null) KeyLight.enabled = true;
            // With no free layer the mask was built once at bind, so renderers created while the preview is
            // open (FCC's hair / beard copies, re-equipped gear) on another layer were missing - the mask now takes in every renderer's layer
            // 1.0.357 (F1 fps, [classes-ui] for [seasons]): the renderer list is CACHED (re-read on a new target or every
            // RendererRefreshSeconds), so a render no longer walks the body's whole hierarchy
            if (BoundTo != null && (cachedFor != BoundTo || Time.unscaledTime - cachedAt > RendererRefreshSeconds))
            {
                BoundTo.GetComponentsInChildren(true, cached);
                cachedFor = BoundTo; cachedAt = Time.unscaledTime;
                if (cached.Count != cachedCount) { cachedCount = cached.Count; dirty = true; }   // gear came or went: the next renders come at the change rate
            }
            if (IsolatedLayer == NoLayer && BoundTo != null && Cam != null)
            {
                int mask = 1 << BoundTo.gameObject.layer;
                foreach (var r in cached) if (r != null) mask |= 1 << r.gameObject.layer;
                if (mask != Cam.cullingMask) { Cam.cullingMask = mask; if (KeyLight != null) KeyLight.cullingMask = mask; }
            }
            if (IsolatedLayer == NoLayer || BoundTo == null) return;
            isolated.Clear(); isolatedFrom.Clear();
            foreach (var renderer in cached) { if (renderer == null) continue; isolated.Add(renderer); isolatedFrom.Add(renderer.gameObject.layer); renderer.gameObject.layer = IsolatedLayer; }
            if (probedFor != BoundTo) { probedFor = BoundTo; ProbeHair(); }
        }

        // Once per bound target, during this camera's render,
        // every hair / beard renderer under it with what decides whether and where it draws
        Character probedFor;
        void ProbeHair()
        {
            var parts = new List<string>();
            foreach (var r in isolated)
            {
                if (r == null) continue;
                string path = r.name; for (var t = r.transform.parent; t != null && t != BoundTo.transform && parts.Count < 40; t = t.parent) path = t.name + "/" + path;
                string lower = path.ToLowerInvariant();
                if (!lower.Contains("hair") && !lower.Contains("beard")) continue;
                var b = r.bounds; var vp = Cam.WorldToViewportPoint(b.center);
                parts.Add($"'{path}' {r.GetType().Name} active {r.gameObject.activeInHierarchy} enabled {r.enabled} forceOff {r.forceRenderingOff} layer {r.gameObject.layer} " +
                          $"mat '{(r.sharedMaterial != null ? r.sharedMaterial.name + "/" + r.sharedMaterial.shader.name : "none")}' centre in view {vp.x:0.00},{vp.y:0.00} z {vp.z:0.00} size {b.size.y:0.00} m");
            }
            EquipmentPanel.PanelLog?.LogDebug($"preview {Name}: hair/beard on '{BoundTo.name}': {(parts.Count > 0 ? string.Join(" | ", parts.ToArray()) : "NONE under the target")} ({isolated.Count} renderer(s) isolated on layer {IsolatedLayer})");
        }

        void Restore()
        {
            if (KeyLight != null) KeyLight.enabled = false;
            for (int i = 0; i < isolated.Count; i++) if (isolated[i] != null) isolated[i].gameObject.layer = isolatedFrom[i];
            isolated.Clear(); isolatedFrom.Clear();
        }

        static int BuildMask(Character character)
        {
            int mask = 1 << character.gameObject.layer;
            foreach (var renderer in character.GetComponentsInChildren<Renderer>(true)) mask |= 1 << renderer.gameObject.layer;
            return mask;
        }

        // ── 1.0.357 (F1 fps, Fire's #2 priority; [classes-ui] for [seasons], the same pattern as the Stats page in 1.0.356): the camera
        // NEVER renders by itself. LivePreviewControls (on the preview image, so it doesn't run while the image is hidden) calls Tick
        // every frame; Tick renders by hand at most ChangeHz while something changes (spin, pan, zoom, a re-frame / refit / re-bind, the
        // body moving or turning, gear coming or going), and IdleHz otherwise (the idle animation), never while hidden.
        const float ChangeHz = 10f, IdleHz = 2f, RendererRefreshSeconds = 2f, ProofSeconds = 60f;
        readonly List<Renderer> cached = new List<Renderer>();
        Character cachedFor;
        float cachedAt, lastRender = -1f, proofAt;
        int cachedCount = -1, renders, skips;
        bool wanted, dirty = true;
        Vector3 lastPos; Quaternion lastRot;
        float lastYaw, lastPitch, lastDistance, lastHeight, lastSide;

        internal void MarkDirty() => dirty = true;

        internal void Tick()
        {
            if (!wanted || Cam == null || BoundTo == null || Target == null || !Target.isActiveAndEnabled) return;
            float now = Time.unscaledTime;
            var t = BoundTo.transform;
            if (t.position != lastPos || t.rotation != lastRot || Yaw != lastYaw || Pitch != lastPitch || Distance != lastDistance || Height != lastHeight || Side != lastSide)
            {
                dirty = true;
                lastPos = t.position; lastRot = t.rotation; lastYaw = Yaw; lastPitch = Pitch; lastDistance = Distance; lastHeight = Height; lastSide = Side;
            }
            if (now - lastRender < 1f / (dirty ? ChangeHz : IdleHz)) { skips++; ProofLine(now); return; }
            lastRender = now;
            dirty = false;
            Cam.Render();   // onPreCull / onPostRender isolate and restore as before
            renders++;
            ProofLine(now);
        }

        void ProofLine(float now)
        {
            if (now - proofAt < ProofSeconds) return;
            if (proofAt > 0f)
                EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] preview {Name}: renders on demand - {renders} render(s), {skips} frame(s) skipped in the last {ProofSeconds:0} s ({ChangeHz:0} Hz on change, {IdleHz:0} Hz idle, none while hidden), {cached.Count} cached renderer(s)");
            proofAt = now; renders = 0; skips = 0;
        }

        static bool s_lightLogged;
        void SetRendering(bool on)
        {
            wanted = on;   // 1.0.357: the camera stays disabled; Tick renders it by hand
            if (Cam != null) Cam.enabled = false;
            if (on) { dirty = true; lastRender = -1f; }
            if (KeyLight != null) KeyLight.enabled = false;
            if (on && KeyLight != null && !s_lightLogged)
            {
                s_lightLogged = true;
                Debug.Log($"[VAInventory] preview light {KeyLight.name} cullingMask 0x{KeyLight.cullingMask:X}, type {KeyLight.type}, on only during the preview camera's render; camera mask 0x{(Cam != null ? Cam.cullingMask : 0):X}, isolated layer {IsolatedLayer}");
            }
        }
    }

    // Mouse input on a preview image (any LivePreview). A double-click resets the view.
    internal sealed class LivePreviewControls : MonoBehaviour, IPointerDownHandler, IDragHandler, IScrollHandler, IPointerClickHandler
    {
        internal LivePreview Preview;
        const int ResetClicks = 2;

        public void OnPointerDown(PointerEventData eventData) { }
        void OnRectTransformDimensionsChange() { Preview?.Refit(); Preview?.MarkDirty(); }
        void LateUpdate() => Preview?.Tick();   // 1.0.357: the on-demand renders (no Tick while this image is hidden)

        public void OnDrag(PointerEventData eventData)
        {
            if (Preview == null) return;
            if (eventData.button == PointerEventData.InputButton.Left) Preview.Spin(eventData.delta); else Preview.Pan(eventData.delta);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (Preview != null && !Mathf.Approximately(eventData.scrollDelta.y, 0f)) Preview.Zoom(Mathf.Sign(eventData.scrollDelta.y));
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (Preview != null && eventData.button == PointerEventData.InputButton.Left && eventData.clickCount == ResetClicks) Preview.ResetView();
        }
    }
}
