using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CutOnce.Core;
using CutOnce.Net;
using UnityEngine;

namespace CutOnce.Device
{
    /// <summary>
    /// The model files a plan's parts point to (shape.uri, a .glb), read into meshes by node name. The server's copy
    /// comes first, because a newly approved plan brings its own; the app's bundled copy (Resources/CutOnce/&lt;uri&gt;.bytes)
    /// is the fallback when offline. Never throws: a file that cannot be had leaves those parts drawn as boxes.
    /// </summary>
    public static class PlanModels
    {
        public static async Task<Dictionary<string, GlbMesh>> Load(ApiClient api, PlanDto plan, bool tryServer)
        {
            var uris = plan.parts.Where(p => p.shape?.type == "mesh" && !string.IsNullOrEmpty(p.shape.uri)).Select(p => p.shape.uri).Distinct().ToList();
            if (uris.Count == 0) return null;
            var meshes = new Dictionary<string, GlbMesh>();
            foreach (var uri in uris)
            {
                byte[] bytes = null; string from = "server";
                if (tryServer)
                {
                    try { bytes = await api.GetAsset(plan.plan_id, uri); }
                    catch (Exception e) { Debug.LogWarning($"[CutOnce] Could not fetch {uri}: {e.Message}"); }
                }
                if (bytes == null) { bytes = Resources.Load<TextAsset>("CutOnce/" + uri)?.bytes; from = "bundled copy"; }
                if (bytes == null) { Debug.LogWarning($"[CutOnce] No model file {uri} for {plan.plan_id}: its parts are drawn as boxes."); continue; }
                try
                {
                    foreach (var pair in GlbReader.ReadNamedMeshes(bytes)) meshes[pair.Key] = pair.Value;
                    Debug.Log($"[CutOnce] {uri} from the {from}: {meshes.Count} meshes");
                }
                catch (Exception e) { Debug.LogWarning($"[CutOnce] {uri} ({from}) could not be read, so its parts are drawn as boxes: {e.Message}"); }
            }
            return meshes;
        }
    }
}
