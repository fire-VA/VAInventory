using System;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.353 (halo ring, Fire 10-24): where VAInventory drops an item in front of the player. "Up" is Core's GravityUp.At (the
    /// ring's local up inside a gravity field, world up everywhere else), so on a ring the item spawns above the floor in front
    /// of the player and is rotated to stand on it, not pushed sideways along world +Y. Without GravityUp (an older Core), or
    /// outside every field, the position and rotation are exactly what the callers used before.
    /// </summary>
    internal static class HaloDrop
    {
        private static bool s_looked, s_logged;
        private static Func<Vector3, Vector3> s_at;
        private static Func<Vector3, bool> s_inField; private static Func<bool> s_active;   // bound once (no Invoke / object[] per call)

        private static void Bind()
        {
            if (s_looked) return;
            s_looked = true;
            var t = Type.GetType("FiresCore.World.GravityUp, FiresUnifiedCore", false);
            var at = t?.GetMethod("At", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector3) }, null);
            var inf = t?.GetMethod("InField", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector3) }, null);
            var act = t?.GetProperty("Active", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            try { if (inf != null && act != null && inf.ReturnType == typeof(bool)) { s_inField = (Func<Vector3, bool>)Delegate.CreateDelegate(typeof(Func<Vector3, bool>), inf); s_active = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), act); } } catch { s_inField = null; s_active = null; }
            if (at != null) s_at = (Func<Vector3, Vector3>)Delegate.CreateDelegate(typeof(Func<Vector3, Vector3>), at);
        }

        private static bool InField(Vector3 p)
        {
            Bind();
            if (s_inField == null || s_at == null || s_active == null) return false;
            // Fire 10-26 (no ring logic on a non-ring world): Core's GravityUp.Active (the one ring authority) first
            try { return s_active() && s_inField(p); } catch { return false; }
        }

        /// <summary>
        /// The drop spot <paramref name="ahead"/> m in front of <paramref name="from"/> and <paramref name="height"/> m up.
        /// <paramref name="flatRot"/> is the rotation the caller used off the ring; <paramref name="rotFollowsBody"/> = it is the player's own rotation (already aligned on a ring).
        /// </summary>
        internal static void Pose(Transform from, float ahead, float height, Quaternion flatRot, out Vector3 pos, out Quaternion rot, bool rotFollowsBody = false)
        {
            Vector3 p = from.position;
            if (!InField(p))
            {
                pos = p + from.forward * ahead + Vector3.up * height;
                rot = flatRot;
                return;
            }
            Vector3 up = Vector3.up;
            try { var u = s_at(p); if (u.sqrMagnitude > 0.5f) up = u.normalized; } catch { }
            Vector3 fwd = Vector3.ProjectOnPlane(from.forward, up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(from.up == up ? from.right : from.up, up);
            fwd.Normalize();
            pos = p + fwd * ahead + up * height;
            rot = rotFollowsBody ? flatRot : Quaternion.FromToRotation(Vector3.up, up) * flatRot;   // the player's own rotation is already on the ring
            if (!s_logged)
            {
                s_logged = true;
                Debug.Log($"[VAInventory] halo ring: item drops follow the ring's up ({up.x:F2},{up.y:F2},{up.z:F2}) at {p.x:F0},{p.y:F0},{p.z:F0}: spawned {height:F1} m above the floor, {ahead:F1} m ahead");
            }
        }
    }
}
