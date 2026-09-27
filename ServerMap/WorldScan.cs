using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ServerMap
{
    internal sealed class WorldScan
    {
        private const float Interval = 5f;
        private const int PerFrame = 4000;
        private const int ZonesPerFrame = 4;
        private const int MaxPieces = 300000;
        private const int MaxCells = 500000;
        private const int MaxErrors = 10;

        private readonly List<ZDO> _snapshot = new List<ZDO>();
        private readonly List<ZDO> _terrain = new List<ZDO>();
        private readonly Dictionary<int, PrefabInfo> _prefabs = new Dictionary<int, PrefabInfo>();
        private readonly Dictionary<ZDOID, uint> _revisions = new Dictionary<ZDOID, uint>();
        private readonly List<PieceRec> _built = new List<PieceRec>();
        private int _cursor = -1;
        private int _terrainCursor = -1;
        private float _next = -1f;
        private int _errors;
        private bool _stopped;
        private bool _loggedField;
        private bool _loggedCap;
        private bool _loggedPieces;
        private FieldInfo _objects;
        private FieldInfo _zones;
        private int _pieceLayer = -1;
        private byte[] _treeScratch;

        internal void Tick(MapSession session)
        {
            if (_stopped || session.TextureSize <= 0 || ZDOMan.instance == null || ZNetScene.instance == null) return;
            try
            {
                if (_cursor < 0 && _terrainCursor < 0)
                {
                    if (Time.time < _next) return;
                    if (!Begin(session)) _next = Time.time + Interval;
                    return;
                }
                if (_cursor >= 0)
                {
                    int end = Mathf.Min(_snapshot.Count, _cursor + PerFrame);
                    for (; _cursor < end; _cursor++) Examine(_snapshot[_cursor], session);
                    if (_cursor >= _snapshot.Count)
                    {
                        _cursor = -1;
                        _snapshot.Clear();
                        FinishPieces(session);
                        CopyZones(session);
                        if (_terrain.Count == 0) _next = Time.time + Interval;
                        else _terrainCursor = 0;
                    }
                    return;
                }
                int zoneEnd = Mathf.Min(_terrain.Count, _terrainCursor + ZonesPerFrame);
                for (; _terrainCursor < zoneEnd; _terrainCursor++) ReadTerrain(_terrain[_terrainCursor], session);
                if (_terrainCursor >= _terrain.Count)
                {
                    _terrainCursor = -1;
                    _terrain.Clear();
                    _next = Time.time + Interval;
                }
            }
            catch (Exception ex)
            {
                _errors++;
                _cursor = -1;
                _terrainCursor = -1;
                _snapshot.Clear();
                _terrain.Clear();
                _next = Time.time + Interval;
                if (_errors >= MaxErrors && !_stopped)
                {
                    _stopped = true;
                    Plugin.LogWarning("The world overlay scan is off for this session. Explored ground and markers keep working. " + ex.Message);
                }
            }
        }

        private bool Begin(MapSession session)
        {
            if (_objects == null) _objects = typeof(ZDOMan).GetField("m_objectsByID", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_objects == null)
            {
                if (!_loggedField)
                {
                    _loggedField = true;
                    Plugin.LogWarning("The object database could not be read. The overlay is off for this session.");
                }
                _stopped = true;
                return false;
            }
            object value = _objects.GetValue(ZDOMan.instance);
            Dictionary<ZDOID, ZDO> all = value as Dictionary<ZDOID, ZDO>;
            if (all == null) return false;
            _snapshot.Clear();
            _built.Clear();
            _terrain.Clear();
            if (!ForestOn()) _treeScratch = null;
            else if (_treeScratch == null || _treeScratch.Length != session.TextureSize * session.TextureSize)
                _treeScratch = new byte[session.TextureSize * session.TextureSize];
            else Array.Clear(_treeScratch, 0, _treeScratch.Length);
            foreach (ZDO zdo in all.Values) _snapshot.Add(zdo);
            _cursor = 0;
            if (_pieceLayer < 0) _pieceLayer = LayerMask.NameToLayer("piece");
            return true;
        }

        private void Examine(ZDO zdo, MapSession session)
        {
            if (zdo == null || !zdo.IsValid()) return;
            int hash = zdo.GetPrefab();
            if (hash == 0) return;
            PrefabInfo info;
            if (!_prefabs.TryGetValue(hash, out info))
            {
                info = BuildInfo(hash);
                _prefabs[hash] = info;
            }
            if (info.Kind == 3)
            {
                _terrain.Add(zdo);
                return;
            }
            if (info.Kind == 2)
            {
                if (!ForestOn()) return;
                int index = Index(zdo.GetPosition(), session);
                if (index >= 0 && _treeScratch[index] < 255) _treeScratch[index]++;
                return;
            }
            if (info.Kind != 1 || _built.Count >= MaxPieces) return;
            Vector3 pos = zdo.GetPosition();
            Quaternion rot = zdo.GetRotation();
            float minX, maxX, minZ, maxZ, maxY;
            if (!Footprint(info, rot, out minX, out maxX, out minZ, out maxZ, out maxY)) return;
            if (!OverlayRules.FootprintOk(maxX - minX, maxZ - minZ)) return;
            PieceRec rec = new PieceRec();
            rec.X0 = pos.x + minX;
            rec.X1 = pos.x + maxX;
            rec.Z0 = pos.z + minZ;
            rec.Z1 = pos.z + maxZ;
            rec.Y = pos.y + maxY;
            rec.Material = info.Material;
            rec.HasCreator = zdo.GetLong(ZDOVars.s_creator, 0L) != 0L;
            _built.Add(rec);
        }

        private void FinishPieces(MapSession session)
        {
            bool treesChanged = false;
            if (ForestOn() && _treeScratch != null)
            {
                treesChanged = session.TreeCounts == null || session.TreeCounts.Length != _treeScratch.Length;
                if (!treesChanged)
                {
                    for (int i = 0; i < _treeScratch.Length; i++)
                    {
                        if (session.TreeCounts[i] != _treeScratch[i]) { treesChanged = true; break; }
                    }
                }
            }
            bool piecesChanged = session.Pieces.Count != _built.Count;
            if (!piecesChanged)
            {
                for (int i = 0; i < _built.Count; i++)
                {
                    PieceRec a = session.Pieces[i];
                    PieceRec b = _built[i];
                    if (a.X0 != b.X0 || a.X1 != b.X1 || a.Z0 != b.Z0 || a.Z1 != b.Z1 || a.Material != b.Material || a.HasCreator != b.HasCreator)
                    {
                        piecesChanged = true;
                        break;
                    }
                }
            }
            if (!piecesChanged && !treesChanged) return;
            if (piecesChanged) session.Pending.DiffPieces(session.Pieces, _built);
            if (treesChanged) session.Pending.DiffTrees(session.TreeCounts, _treeScratch);
            session.Pieces.Clear();
            session.Pieces.AddRange(_built);
            if (treesChanged)
            {
                session.TreeCounts = _treeScratch;
                _treeScratch = null;
            }
            session.MarkDirty();
            if (treesChanged || (piecesChanged && session.Pending.NeedsFullPaint))
                session.OverlayDirty = true;
            else if (piecesChanged && session.Pending.CanPatchPaint)
                session.Pending.CopyAddedPieces(session.PatchPieces);
            else if (piecesChanged)
                session.OverlayDirty = true;
            if (!_loggedPieces)
            {
                _loggedPieces = true;
                Plugin.LogInfo("Map overlay stored " + session.Pieces.Count + " buildings.");
            }
        }

        private void CopyZones(MapSession session)
        {
            if (ZoneSystem.instance == null) return;
            if (_zones == null) _zones = typeof(ZoneSystem).GetField("m_generatedZones", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_zones == null) return;
            object value = _zones.GetValue(ZoneSystem.instance);
            if (value == null) return;
            HashSet<long> next = new HashSet<long>();
            foreach (object zone in (System.Collections.IEnumerable)value)
            {
                Vector2s point = (Vector2s)zone;
                next.Add(OverlayRules.PackKey(point.x, point.y));
            }
            if (session.Pending.DiffZones(session.Zones, next))
            {
                session.MarkDirty();
                session.OverlayDirty = true;
            }
            session.Zones.Clear();
            foreach (long key in next) session.Zones.Add(key);
        }

        private void ReadTerrain(ZDO zdo, MapSession session)
        {
            if (zdo == null || !zdo.IsValid()) return;
            uint rev = zdo.DataRevision;
            uint seen;
            if (_revisions.TryGetValue(zdo.m_uid, out seen) && seen == rev) return;
            _revisions[zdo.m_uid] = rev;
            byte[] raw = zdo.GetByteArray(ZDOVars.s_TCData, null);
            if (raw == null) return;
            byte[] data = Utils.Decompress(raw);
            ZPackage pkg = new ZPackage(data);
            pkg.ReadInt();
            pkg.ReadInt();
            pkg.ReadVector3();
            pkg.ReadSingle();
            int heightCount = pkg.ReadInt();
            int pitch = Mathf.RoundToInt(Mathf.Sqrt(heightCount));
            if (pitch < 2 || pitch * pitch != heightCount) return;
            bool[] modified = new bool[heightCount];
            for (int i = 0; i < heightCount; i++)
            {
                modified[i] = pkg.ReadBool();
                if (modified[i]) { pkg.ReadSingle(); pkg.ReadSingle(); }
            }
            int paintCount = pkg.ReadInt();
            int paintPitch = Mathf.RoundToInt(Mathf.Sqrt(paintCount));
            if (paintPitch < 2 || paintPitch * paintPitch != paintCount) return;
            byte[] kinds = new byte[paintCount];
            for (int i = 0; i < paintCount; i++)
            {
                bool levelled = paintPitch == pitch && modified[i];
                if (!pkg.ReadBool())
                {
                    kinds[i] = levelled ? OverlayRules.KindLevelled : OverlayRules.KindNone;
                    continue;
                }
                float r = pkg.ReadSingle();
                float g = pkg.ReadSingle();
                float b = pkg.ReadSingle();
                float a = pkg.ReadSingle();
                kinds[i] = OverlayRules.ClassifyPaint(r, g, b, a, levelled);
            }
            Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
            float centerX = zone.x * 64f;
            float centerZ = zone.y * 64f;
            float scale = 64f / (paintPitch - 1);
            int halfMask = paintPitch / 2;
            const float grid = 2f;
            int gx0 = Mathf.FloorToInt((centerX - 32f) / grid);
            int gx1 = Mathf.CeilToInt((centerX + 32f) / grid);
            int gz0 = Mathf.FloorToInt((centerZ - 32f) / grid);
            int gz1 = Mathf.CeilToInt((centerZ + 32f) / grid);
            List<long> drop = new List<long>();
            foreach (KeyValuePair<long, byte> pair in session.Cells)
            {
                float wx = (OverlayRules.KeyX(pair.Key) + 0.5f) * grid;
                float wz = (OverlayRules.KeyY(pair.Key) + 0.5f) * grid;
                if (Mathf.Abs(wx - centerX) < 32f && Mathf.Abs(wz - centerZ) < 32f) drop.Add(pair.Key);
            }
            for (int i = 0; i < drop.Count; i++)
            {
                session.Pending.SetCell(OverlayRules.KeyX(drop[i]), OverlayRules.KeyY(drop[i]), OverlayRules.KindNone);
                session.Cells.Remove(drop[i]);
            }
            for (int gz = gz0; gz <= gz1; gz++)
            {
                int lz = Mathf.FloorToInt(((gz + 0.5f) * grid - 0.5f - centerZ) / scale + 0.5f + halfMask);
                if (lz < 0 || lz >= paintPitch) continue;
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    int lx = Mathf.FloorToInt(((gx + 0.5f) * grid - 0.5f - centerX) / scale + 0.5f + halfMask);
                    if (lx < 0 || lx >= paintPitch) continue;
                    byte kind = kinds[lz * paintPitch + lx];
                    if (kind == OverlayRules.KindNone) continue;
                    if (session.Cells.Count >= MaxCells)
                    {
                        if (!_loggedCap)
                        {
                            _loggedCap = true;
                            Plugin.LogWarning("Path cell cap reached. Extra ground is not stored.");
                        }
                        continue;
                    }
                    long key = OverlayRules.PackKey(gx, gz);
                    session.Cells[key] = kind;
                    session.Pending.SetCell(gx, gz, kind);
                }
            }
            session.MarkDirty();
            session.OverlayDirty = true;
        }

        private PrefabInfo BuildInfo(int hash)
        {
            PrefabInfo info = new PrefabInfo();
            GameObject prefab = ZNetScene.instance.GetPrefab(hash);
            if (prefab == null) return info;
            if (prefab.GetComponent<TerrainComp>() != null)
            {
                info.Kind = 3;
                return info;
            }
            // Trees, bushes, and pickables are not buildings. Plant Everything and similar
            // mods add Piece to those prefabs, which otherwise paints every tree as wood.
            if (IsVegetation(prefab))
            {
                if (prefab.GetComponent<TreeBase>() != null || prefab.GetComponentInChildren<TreeBase>(true) != null)
                    info.Kind = 2;
                return info;
            }
            if (prefab.GetComponent<Piece>() == null)
            {
                if (prefab.GetComponent<TreeBase>() != null) info.Kind = 2;
                return info;
            }
            WearNTear wear = prefab.GetComponent<WearNTear>();
            info.Material = OverlayRules.MaterialUnknown;
            if (wear != null)
            {
                int material = (int)wear.m_materialType;
                if (material >= 0 && material <= 8) info.Material = (byte)material;
            }
            Collider[] cols = prefab.GetComponentsInChildren<Collider>(true);
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;
            Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
            for (int i = 0; i < cols.Length; i++)
            {
                Collider col = cols[i];
                if (col == null || !col.enabled || col.gameObject.layer != _pieceLayer) continue;
                Vector3 center;
                Vector3 extents;
                if (!LocalBox(col, out center, out extents)) continue;
                Matrix4x4 matrix = toRoot * col.transform.localToWorldMatrix;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 corner = matrix.MultiplyPoint3x4(new Vector3(
                        (k & 1) == 0 ? center.x - extents.x : center.x + extents.x,
                        (k & 2) == 0 ? center.y - extents.y : center.y + extents.y,
                        (k & 4) == 0 ? center.z - extents.z : center.z + extents.z));
                    min = Vector3.Min(min, corner);
                    max = Vector3.Max(max, corner);
                }
                any = true;
            }
            if (!any || !OverlayRules.FootprintOk(max.x - min.x, max.z - min.z)) return info;
            info.Kind = 1;
            info.Min = min;
            info.Max = max;
            return info;
        }

        private static bool IsVegetation(GameObject prefab)
        {
            if (prefab.GetComponent<TreeBase>() != null || prefab.GetComponentInChildren<TreeBase>(true) != null) return true;
            if (prefab.GetComponent<TreeLog>() != null || prefab.GetComponentInChildren<TreeLog>(true) != null) return true;
            if (prefab.GetComponent<Plant>() != null || prefab.GetComponentInChildren<Plant>(true) != null) return true;
            if (prefab.GetComponent<Pickable>() != null) return true;
            return false;
        }

        private static bool LocalBox(Collider col, out Vector3 center, out Vector3 extents)
        {
            BoxCollider box = col as BoxCollider;
            if (box != null)
            {
                center = box.center;
                extents = box.size * 0.5f;
                return true;
            }
            SphereCollider sphere = col as SphereCollider;
            if (sphere != null)
            {
                center = sphere.center;
                extents = Vector3.one * sphere.radius;
                return true;
            }
            CapsuleCollider capsule = col as CapsuleCollider;
            if (capsule != null)
            {
                float half = Mathf.Max(capsule.radius, capsule.height * 0.5f);
                center = capsule.center;
                extents = Vector3.one * capsule.radius;
                if (capsule.direction == 0) extents.x = half;
                else if (capsule.direction == 1) extents.y = half;
                else extents.z = half;
                return true;
            }
            MeshCollider mesh = col as MeshCollider;
            if (mesh != null && mesh.sharedMesh != null)
            {
                Bounds bounds = mesh.sharedMesh.bounds;
                center = bounds.center;
                extents = bounds.extents;
                return true;
            }
            center = Vector3.zero;
            extents = Vector3.zero;
            return false;
        }

        private static bool Footprint(PrefabInfo info, Quaternion rot, out float minX, out float maxX, out float minZ, out float maxZ, out float maxY)
        {
            if (rot.x == 0f && rot.y == 0f && rot.z == 0f)
            {
                minX = info.Min.x;
                maxX = info.Max.x;
                minZ = info.Min.z;
                maxZ = info.Max.z;
                maxY = info.Max.y;
                return true;
            }
            minX = minZ = float.MaxValue;
            maxX = maxZ = maxY = float.MinValue;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = rot * new Vector3(
                    (c & 1) == 0 ? info.Min.x : info.Max.x,
                    (c & 2) == 0 ? info.Min.y : info.Max.y,
                    (c & 4) == 0 ? info.Min.z : info.Max.z);
                if (corner.x < minX) minX = corner.x;
                if (corner.x > maxX) maxX = corner.x;
                if (corner.z < minZ) minZ = corner.z;
                if (corner.z > maxZ) maxZ = corner.z;
                if (corner.y > maxY) maxY = corner.y;
            }
            return true;
        }

        private static bool ForestOn()
        {
            return Plugin.ShowClearedForest.Value || Plugin.ShowPlantedForest.Value;
        }

        private static int Index(Vector3 pos, MapSession session)
        {
            float pixel = Minimap.instance != null ? Minimap.instance.m_pixelSize : 24576f / session.TextureSize;
            int half = session.TextureSize / 2;
            int px = Mathf.RoundToInt(pos.x / pixel + half);
            int py = Mathf.RoundToInt(pos.z / pixel + half);
            if (px < 0 || py < 0 || px >= session.TextureSize || py >= session.TextureSize) return -1;
            return py * session.TextureSize + px;
        }

        private struct PrefabInfo
        {
            public byte Kind;
            public byte Material;
            public Vector3 Min;
            public Vector3 Max;
        }
    }
}
