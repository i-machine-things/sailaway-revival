using System.Collections.Generic;
using UnityEngine;

namespace SailawayRevival
{
    // Renders a small preview of each boat type from the player's own already
    // -installed game assets (PackagedBoatPrototypes.GetBoatOfType's prefabs,
    // the same ones the game instantiates for the player's actual boat) so the
    // grid in BoatSwitcher.cs can show pictures instead of plain text. Never
    // ships any image: each one is baked at runtime and cached in memory only.
    public static class BoatThumbnails
    {
        const int Size = 512;

        static readonly Dictionary<int, Texture2D> cache = new Dictionary<int, Texture2D>();

        public static Texture2D Get(int btpNr)
        {
            if (!cache.TryGetValue(btpNr, out var tex))
            {
                tex = Capture(btpNr);
                cache[btpNr] = tex; // cache a miss too, so we don't retry every frame
            }
            return tex;
        }

        static Texture2D Capture(int btpNr)
        {
            if (!Global.packagedBoatPrototypes.GetBoatOfType(btpNr, out GameObject prototype, out _, out _) || prototype == null)
            {
                return null;
            }

            // Far from the sailing area and spread out per type so nothing overlaps.
            var stagePos = new Vector3(0f, 0f, 50000f + btpNr * 500f);
            var clone = Object.Instantiate(prototype, stagePos, Quaternion.Euler(0f, 35f, 0f));
            var bounds = ComputeBounds(clone, stagePos);

            var camGo = new GameObject("SailawayRevivalThumbCam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.09f, 0.12f, 1f);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = bounds.extents.magnitude * 10f + 50f;
            cam.fieldOfView = 35f;

            var radius = Mathf.Max(bounds.extents.magnitude, 1f);
            // Boat-relative, not world-relative: port quarter (confirmed
            // in-game), aft of the mast so the headsail doesn't block the
            // hull, at a shallow elevation rather than near-overhead.
            var dir = clone.transform.rotation * new Vector3(-0.6f, 0.3f, -0.9f).normalized;
            camGo.transform.position = bounds.center + dir * radius * 3.3f;
            camGo.transform.LookAt(bounds.center);

            var rt = new RenderTexture(Size, Size, 16);
            cam.targetTexture = rt;
            cam.Render();

            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Size, Size, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;

            cam.targetTexture = null;
            rt.Release();
            Object.Destroy(rt);
            Object.Destroy(camGo);
            Object.Destroy(clone);

            return tex;
        }

        static Bounds ComputeBounds(GameObject go, Vector3 fallbackCenter)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return new Bounds(fallbackCenter, Vector3.one * 5f);
            }
            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }
            return bounds;
        }
    }
}
