using UnityEngine;
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
    // Client only. Everything here is behind Player.m_localPlayer, so a dedicated server never reaches it.
    internal static class CharacterPreview
    {
        const int TextureWidth = 400;
        const int TextureHeight = 700;
        const float Distance = 2.6f;
        const float Height = 1.0f;
        const float FieldOfView = 32f;

        static Camera cam;
        static Light keyLight;
        static RenderTexture texture;
        static RawImage target;
        static Player boundTo;

        internal static void Bind(RawImage mount)
        {
            target = mount;
            if (target == null) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            EnsureTexture();

            // Rebuild when the player instance changes - death and respawn produce a new Player.
            if (cam != null && boundTo != player) Teardown();

            if (cam == null) Build(player);
            if (cam == null) return;

            target.texture = texture;
            target.color = Color.white;
            SetRendering(true);
        }

        internal static void Hide() => SetRendering(false);

        internal static void Teardown()
        {
            if (cam != null) Object.Destroy(cam.gameObject);
            cam = null;
            keyLight = null;
            boundTo = null;
            if (target != null) target.texture = null;
        }

        static void EnsureTexture()
        {
            if (texture != null) return;
            texture = new RenderTexture(TextureWidth, TextureHeight, 24, RenderTextureFormat.ARGB32)
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
            // In front of the player looking back at chest height, in the player's own local space.
            go.transform.localPosition = new Vector3(0f, Height, Distance);
            go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

            cam = go.AddComponent<Camera>();
            cam.fieldOfView = FieldOfView;
            cam.nearClipPlane = 0.1f;
            // Tight far plane so other characters standing behind the player cannot creep into frame.
            cam.farClipPlane = Distance + 2f;
            cam.cullingMask = BuildMask(player);
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
        }

        // Built from the player's OWN renderers rather than a hardcoded layer name: the visual parts sit on
        // whatever layer the prefab uses, and reading it is robust across game updates.
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
}
