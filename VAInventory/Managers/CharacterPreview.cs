using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // Renders the LIVE local player into the character panel, so the preview always matches what is actually
    // worn - no mannequin to keep in sync with VisEquipment.
    //
    // The camera is a CHILD of the player, which is what makes this cheap and correct: it inherits the
    // player's rotation, so the portrait stays front-on however the player turns, and it needs no per-frame
    // follow code. It only renders while the inventory is open.
    //
    // The view orbits the player (Fire, 2026-09-27): wheel zooms, left-drag spins and tilts, right- or
    // middle-drag moves the view, double-click puts it back. The view is kept between inventory opens.
    //
    // Only the player is drawn (Fire, 2026-09-28): no world behind them and no other player, creature or
    // object near them. The player's own renderers move to an empty layer just before the preview camera
    // culls and back just after it renders, and the camera and its key light see only that layer. The main
    // camera and physics never see the move.
    //
    // Client only. Everything here is behind Player.m_localPlayer, so a dedicated server never reaches it.
    internal static class CharacterPreview
    {
        const int TextureHeight = 700;
        const int MinTextureWidth = 200;
        const int MaxTextureWidth = 1400;
        const float FieldOfView = 32f;
        const float NearClip = 0.1f;
        const float FarMargin = 2f;

        const float DefaultDistance = 3.4f;
        const float DefaultHeight = 1.0f;
        const float MinDistance = 1.2f;
        const float MaxDistance = 8f;
        const float MinHeight = 0.1f;
        const float MaxHeight = 2.2f;
        const float MaxSideOffset = 1.5f;
        const float MinPitch = -30f;
        const float MaxPitch = 60f;
        const float SpinDegreesPerPixel = 0.4f;
        const float TiltDegreesPerPixel = 0.25f;
        const float PanMetresPerPixel = 0.004f;   // at the default distance; scales with zoom
        const float ZoomStep = 0.9f;               // distance multiplier per wheel notch in
        const int FirstUserLayer = 8;
        const int LastLayer = 31;
        const int NoLayer = -1;

        static Camera cam;
        static int isolatedLayer = NoLayer;
        static bool renderHooked;
        static readonly List<Renderer> isolated = new List<Renderer>();
        static readonly List<int> isolatedFrom = new List<int>();
        static Light keyLight;
        static RenderTexture texture;
        static RawImage target;
        static Player boundTo;

        static float yaw;
        static float pitch;
        static float distance = DefaultDistance;
        static float height = DefaultHeight;
        static float side;

        internal static void Bind(RawImage mount)
        {
            target = mount;
            if (target == null) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            EnsureTexture(target.rectTransform.rect);

            // Rebuild when the player instance changes - death and respawn produce a new Player.
            if (cam != null && boundTo != player) Teardown();

            if (cam == null) Build(player);
            if (cam == null) return;

            AttachTexture();
            target.color = Color.white;
            target.raycastTarget = true;
            if (target.GetComponent<CharacterPreviewControls>() == null)
                target.gameObject.AddComponent<CharacterPreviewControls>();
            SetRendering(true);
        }

        internal static void Hide() => SetRendering(false);

        // For ui_test: the preview's camera, light, texture and private layer, and its view, saved and put back.
        internal static Camera PreviewCamera => cam;
        internal static Light PreviewLight => keyLight;
        internal static RenderTexture PreviewTexture => texture;
        internal static int IsolatedLayer => isolatedLayer;

        internal readonly struct View
        {
            internal readonly float Yaw, Pitch, Distance, Height, Side;

            internal View(float yaw, float pitch, float distance, float height, float side)
            {
                Yaw = yaw;
                Pitch = pitch;
                Distance = distance;
                Height = height;
                Side = side;
            }
        }

        internal static View SaveView() => new View(yaw, pitch, distance, height, side);

        internal static void RestoreView(View view)
        {
            yaw = view.Yaw;
            pitch = view.Pitch;
            distance = view.Distance;
            height = view.Height;
            side = view.Side;
            Place();
        }

        internal static void Teardown()
        {
            if (cam != null) Object.Destroy(cam.gameObject);
            cam = null;
            keyLight = null;
            boundTo = null;
            if (target != null) target.texture = null;
        }

        internal static void Spin(Vector2 pixels)
        {
            yaw += pixels.x * SpinDegreesPerPixel;
            pitch = Mathf.Clamp(pitch - pixels.y * TiltDegreesPerPixel, MinPitch, MaxPitch);
            Place();
        }

        internal static void Pan(Vector2 pixels)
        {
            float metresPerPixel = PanMetresPerPixel * distance / DefaultDistance;
            height = Mathf.Clamp(height - pixels.y * metresPerPixel, MinHeight, MaxHeight);
            side = Mathf.Clamp(side - pixels.x * metresPerPixel, -MaxSideOffset, MaxSideOffset);
            Place();
        }

        internal static void Zoom(float notchesIn)
        {
            distance = Mathf.Clamp(distance * Mathf.Pow(ZoomStep, notchesIn), MinDistance, MaxDistance);
            Place();
        }

        internal static void ResetView()
        {
            yaw = 0f;
            pitch = 0f;
            side = 0f;
            distance = DefaultDistance;
            height = DefaultHeight;
            Place();
        }

        // The mount's rect is final only after the layout pass that follows Bind (the fat-player screenshot: the texture
        // was sized from the rect before the panel's layout groups had widened it). So the mount reports every size
        // change, and the texture and the camera's aspect follow it.
        internal static void Refit()
        {
            if (target == null || cam == null) return;
            var mount = target.rectTransform.rect;
            if (mount.width <= 0f || mount.height <= 0f) return;
            EnsureTexture(mount);
            AttachTexture();
        }

        // A camera keeps the aspect it last computed until told otherwise, so it is set from the texture every time.
        static void AttachTexture()
        {
            cam.targetTexture = texture;
            cam.aspect = (float)texture.width / texture.height;
            target.texture = texture;
        }

        // The texture takes the mount's shape, so the portrait is never stretched whatever size the prefab gives it.
        static void EnsureTexture(Rect mount)
        {
            int width = mount.height > 0f
                ? Mathf.Clamp(Mathf.RoundToInt(TextureHeight * mount.width / mount.height), MinTextureWidth, MaxTextureWidth)
                : MinTextureWidth;
            if (texture != null && texture.width == width) return;
            if (texture != null)
            {
                if (cam != null) cam.targetTexture = null;
                texture.Release();
                Object.Destroy(texture);
            }
            texture = new RenderTexture(width, TextureHeight, 24, RenderTextureFormat.ARGB32)
            {
                name = "VAInventory_CharacterPreview",
                antiAliasing = 2
            };
            texture.Create();
        }

        static void Build(Player player)
        {
            var go = new GameObject("VAInventory_PreviewCamera");
            go.transform.SetParent(player.transform, false);

            cam = go.AddComponent<Camera>();
            cam.fieldOfView = FieldOfView;
            cam.nearClipPlane = NearClip;
            isolatedLayer = FreeLayer();
            cam.cullingMask = isolatedLayer != NoLayer ? 1 << isolatedLayer : BuildMask(player);
            HookRender();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);   // transparent, so the parchment shows through
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.targetTexture = texture;
            cam.enabled = false;

            // The world's own lighting can leave the player black indoors or at night. A key light parented
            // to the camera, culled to the same layers, keeps the portrait readable without touching the
            // scene - it cannot light anything the preview camera does not already see.
            var lightGo = new GameObject("VAInventory_PreviewLight");
            lightGo.transform.SetParent(go.transform, false);
            lightGo.transform.localRotation = Quaternion.Euler(25f, 15f, 0f);
            keyLight = lightGo.AddComponent<Light>();
            keyLight.type = LightType.Directional;
            keyLight.intensity = 1.1f;
            keyLight.cullingMask = cam.cullingMask;
            keyLight.enabled = false;

            boundTo = player;
            Place();
        }

        // Orbits a look point on the player, all in the player's own local space: yaw 0 is straight in front.
        // The far plane rides just past the player so other characters behind them never creep into frame.
        static void Place()
        {
            if (cam == null) return;
            var right = Quaternion.Euler(0f, yaw, 0f) * Vector3.left;
            var lookAt = new Vector3(0f, height, 0f) + right * side;
            var offset = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward * distance;
            cam.transform.localPosition = lookAt + offset;
            cam.transform.localRotation = Quaternion.LookRotation(-offset, Vector3.up);
            cam.farClipPlane = distance + FarMargin;
        }

        // A layer the game never named, so nothing in the world is drawn on it. 7 and 6 first: no mod on this stack
        // draws on them, where 30 is the stack's usual private render layer (item icons, shader prewarm, impostor
        // bakes) and 3 is PieceManager's snapshot layer.
        static readonly int[] PreferredLayers = { 7, 6 };

        static int FreeLayer()
        {
            foreach (int layer in PreferredLayers)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(layer))) return layer;
            for (int layer = LastLayer; layer >= FirstUserLayer; layer--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(layer))) return layer;
            return NoLayer;
        }

        static void HookRender()
        {
            if (renderHooked) return;
            renderHooked = true;
            Camera.onPreCull += Isolate;
            Camera.onPostRender += Restore;
        }

        // Every renderer under the player, gear and hair included, as it is this frame.
        static void Isolate(Camera rendering)
        {
            if (rendering != cam || isolatedLayer == NoLayer || boundTo == null) return;
            isolated.Clear();
            isolatedFrom.Clear();
            boundTo.GetComponentsInChildren(true, isolated);
            foreach (var renderer in isolated)
            {
                isolatedFrom.Add(renderer.gameObject.layer);
                renderer.gameObject.layer = isolatedLayer;
            }
        }

        static void Restore(Camera rendering)
        {
            if (rendering != cam) return;
            for (int index = 0; index < isolated.Count; index++)
                if (isolated[index] != null) isolated[index].gameObject.layer = isolatedFrom[index];
            isolated.Clear();
            isolatedFrom.Clear();
        }

        // Without a free layer: built from the player's OWN renderers rather than a hardcoded layer name: the visual
        // parts sit on whatever layer the prefab uses, and reading it is robust across game updates.
        static int BuildMask(Player player)
        {
            int mask = 1 << player.gameObject.layer;
            foreach (var renderer in player.GetComponentsInChildren<Renderer>(true))
                mask |= 1 << renderer.gameObject.layer;
            return mask;
        }

        static void SetRendering(bool on)
        {
            if (cam != null) cam.enabled = on;
            if (keyLight != null) keyLight.enabled = on;
        }
    }

    // Mouse input on the preview image. A double-click resets the view.
    internal sealed class CharacterPreviewControls : MonoBehaviour, IPointerDownHandler, IDragHandler, IScrollHandler, IPointerClickHandler
    {
        const int ResetClicks = 2;

        // Claims the press so a drag that starts on the preview stays with the preview.
        public void OnPointerDown(PointerEventData eventData) { }

        void OnRectTransformDimensionsChange() => CharacterPreview.Refit();

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left) CharacterPreview.Spin(eventData.delta);
            else CharacterPreview.Pan(eventData.delta);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (!Mathf.Approximately(eventData.scrollDelta.y, 0f)) CharacterPreview.Zoom(Mathf.Sign(eventData.scrollDelta.y));
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left && eventData.clickCount == ResetClicks)
                CharacterPreview.ResetView();
        }
    }
}
