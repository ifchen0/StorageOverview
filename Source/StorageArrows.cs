using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace StorageOverview
{
    /// <summary>
    /// Vanilla letter-style arrows pointing at every part of the hovered column's storages.
    /// The vanilla TargetHighlighter places its arrows around the screen centre, which lies
    /// under the table, so the same arrow is drawn here around the centre of the map strip
    /// right of the table instead: below a target inside the strip, on the strip edge
    /// towards a target outside it.
    /// </summary>
    public static class StorageArrows
    {
        /// <summary>Distance kept between edge arrows and the strip border, in cells.</summary>
        private const float EdgeInset = 1.5f;

        private static StorageColumn shownColumn;
        private static Map shownMap;
        private static float stripLeftUI;
        private static int shownFrame = -1;

        private static readonly Dictionary<StorageColumn, List<Vector3>> targetCache =
            new Dictionary<StorageColumn, List<Vector3>>();

        /// <summary>Called from the table every frame the column is hovered.</summary>
        public static void Show(StorageColumn column, Map map, float stripLeft)
        {
            shownColumn = column;
            shownMap = map;
            stripLeftUI = stripLeft;
            shownFrame = Time.frameCount;
        }

        /// <summary>Columns are rebuilt on open and on storage changes; drop the old targets.</summary>
        public static void ClearCache() => targetCache.Clear();

        /// <summary>
        /// One point per storage part: a zone, or a cluster of touching buildings of a
        /// storage group. Cached per column; storages cannot move while the table is open.
        /// </summary>
        public static List<Vector3> Targets(StorageColumn column)
        {
            if (targetCache.TryGetValue(column, out List<Vector3> targets))
            {
                return targets;
            }
            targets = new List<Vector3>();
            foreach (StorageColumn member in column.members)
            {
                AddTargets(member.owner, targets);
            }
            targetCache[column] = targets;
            return targets;
        }

        /// <summary>The target nearest the middle of all targets; the camera is centred on it.</summary>
        public static bool TryGetAnchor(StorageColumn column, out Vector3 anchor)
        {
            List<Vector3> targets = Targets(column);
            anchor = Vector3.zero;
            if (targets.Count == 0)
            {
                return false;
            }
            Vector3 mean = Vector3.zero;
            foreach (Vector3 t in targets)
            {
                mean += t;
            }
            mean /= targets.Count;
            anchor = Nearest(targets, mean);
            return true;
        }

        private static void AddTargets(IStoreSettingsParent owner, List<Vector3> targets)
        {
            var pieces = new List<CellRect>();
            switch (owner)
            {
                case Zone zone:
                    // A zone is one target: the cell nearest its middle.
                    var cells = new List<Vector3>();
                    foreach (IntVec3 cell in zone.Cells)
                    {
                        cells.Add(cell.ToVector3Shifted());
                    }
                    if (cells.Count > 0)
                    {
                        targets.Add(Nearest(cells, Mean(cells)));
                    }
                    return;
                case StorageGroup group:
                    foreach (IStorageGroupMember member in group.members)
                    {
                        if (member is Thing thing && thing.Spawned)
                        {
                            pieces.Add(thing.OccupiedRect());
                        }
                    }
                    break;
                case Thing thing when thing.Spawned:
                    pieces.Add(thing.OccupiedRect());
                    break;
            }
            foreach (List<CellRect> cluster in Clusters(pieces))
            {
                var centers = new List<Vector3>(cluster.Count);
                foreach (CellRect r in cluster)
                {
                    centers.Add(r.CenterVector3);
                }
                targets.Add(Nearest(centers, Mean(centers)));
            }
        }

        /// <summary>Groups buildings that touch, diagonals included, so a row of shelves gets one arrow.</summary>
        private static List<List<CellRect>> Clusters(List<CellRect> pieces)
        {
            var clusters = new List<List<CellRect>>();
            var done = new bool[pieces.Count];
            for (int i = 0; i < pieces.Count; i++)
            {
                if (done[i])
                {
                    continue;
                }
                done[i] = true;
                var cluster = new List<CellRect> { pieces[i] };
                for (int k = 0; k < cluster.Count; k++)
                {
                    CellRect grown = cluster[k].ExpandedBy(1);
                    for (int j = 0; j < pieces.Count; j++)
                    {
                        if (!done[j] && grown.Overlaps(pieces[j]))
                        {
                            done[j] = true;
                            cluster.Add(pieces[j]);
                        }
                    }
                }
                clusters.Add(cluster);
            }
            return clusters;
        }

        private static Vector3 Mean(List<Vector3> points)
        {
            Vector3 sum = Vector3.zero;
            foreach (Vector3 p in points)
            {
                sum += p;
            }
            return sum / points.Count;
        }

        private static Vector3 Nearest(List<Vector3> points, Vector3 to)
        {
            Vector3 best = points[0];
            float bestDist = float.MaxValue;
            foreach (Vector3 p in points)
            {
                float d = (p - to).MagnitudeHorizontalSquared();
                if (d < bestDist)
                {
                    bestDist = d;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>Draws the arrows; runs in the map update right after the vanilla highlighter.</summary>
        public static void Draw()
        {
            // Shown only while the table keeps calling Show (hovered and open).
            if (shownColumn == null || Time.frameCount - shownFrame > 1)
            {
                shownColumn = null;
                return;
            }
            if (!WorldRendererUtility.DrawingMap || shownMap != Find.CurrentMap || Find.Camera == null)
            {
                return;
            }
            // Strip corners as in the table's VisibleMapCells.
            Vector3 a = UI.UIToMapPosition(new Vector2(stripLeftUI, 35f));
            Vector3 b = UI.UIToMapPosition(new Vector2(UI.screenWidth, UI.screenHeight));
            float minX = Mathf.Min(a.x, b.x), maxX = Mathf.Max(a.x, b.x);
            float minZ = Mathf.Min(a.z, b.z), maxZ = Mathf.Max(a.z, b.z);
            var center = new Vector3((minX + maxX) / 2f, 0f, (minZ + maxZ) / 2f);
            float halfX = Mathf.Max(0.5f, (maxX - minX) / 2f - EdgeInset);
            float halfZ = Mathf.Max(0.5f, (maxZ - minZ) / 2f - EdgeInset);
            float altitude = AltitudeLayer.MetaOverlays.AltitudeFor();

            foreach (Vector3 target in Targets(shownColumn))
            {
                Vector3 pos;
                Quaternion rotation;
                if (target.x >= minX && target.x <= maxX && target.z >= minZ && target.z <= maxZ)
                {
                    // Vanilla near arrow: just below the target, pointing up at it.
                    pos = target;
                    pos.z -= 1.5f;
                    rotation = Quaternion.identity;
                }
                else
                {
                    // Vanilla far arrow, placed where the line towards the target meets the strip edge.
                    Vector3 dir = (target - center).Yto0();
                    float scale = Mathf.Min(
                        Mathf.Abs(dir.x) > 0.001f ? halfX / Mathf.Abs(dir.x) : float.MaxValue,
                        Mathf.Abs(dir.z) > 0.001f ? halfZ / Mathf.Abs(dir.z) : float.MaxValue);
                    pos = center + dir * scale;
                    rotation = Quaternion.LookRotation(dir.normalized);
                }
                pos.y = altitude;
                Graphics.DrawMesh(MeshPool.plane20, pos, rotation, GenDraw.ArrowMatWhite, 0);
            }
        }
    }

    [HarmonyPatch(typeof(TargetHighlighter), nameof(TargetHighlighter.TargetHighlighterUpdate))]
    public static class TargetHighlighter_Update_Patch
    {
        public static void Postfix() => StorageArrows.Draw();
    }
}
