using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ServerMap
{
    internal sealed class MapNet
    {
        private const float OverlayInterval = 1f;

        private static bool _registered;
        private static MapSession _session;
        private static int _stream;
        private static FieldInfo _peersField;
        private static bool _loggedTimeout;
        private readonly ChunkBox _chunks = new ChunkBox();
        private float _nextOverlay;

        internal void Tick(MapSession session)
        {
            _session = session;
            Register();
            int dropped = _chunks.DropExpired(Time.time);
            if (dropped > 0 && !_loggedTimeout)
            {
                _loggedTimeout = true;
                Plugin.LogWarning("A map transfer did not finish and was dropped.");
            }
            if (ZNet.instance.IsServer())
            {
                Prune(session);
                if (Time.time >= _nextOverlay)
                {
                    _nextOverlay = Time.time + OverlayInterval;
                    FlushOverlay();
                }
                return;
            }
            if (session.Denied || session.HelloSent || Minimap.instance == null || ZRoutedRpc.instance == null) return;
            long server = ServerId();
            if (server == 0L) return;
            ZPackage hello = new ZPackage();
            hello.Write(Plugin.PluginVersion);
            hello.Write(Minimap.instance.m_textureSize);
            ZRoutedRpc.instance.InvokeRoutedRPC(server, "ServerMap Hello", hello);
            session.HelloSent = true;
        }

        internal void Reset()
        {
            _chunks.Clear();
            _nextOverlay = 0f;
        }

        private static void Register()
        {
            if (_registered || ZRoutedRpc.instance == null) return;
            _registered = true;
            try
            {
                ZRoutedRpc.instance.Register<ZPackage>("ServerMap Hello", OnHello);
                ZRoutedRpc.instance.Register<string>("ServerMap Deny", OnDeny);
                ZRoutedRpc.instance.Register<ZPackage>("ServerMap Explore", OnExplore);
                ZRoutedRpc.instance.Register<ZPackage>("ServerMap Indexes", OnIndexes);
                ZRoutedRpc.instance.Register<ZPackage>("ServerMap Markers", OnMarkers);
                ZRoutedRpc.instance.Register<ZPackage>("ServerMap Marker", OnMarker);
                ZRoutedRpc.instance.Register<ZPackage>("ServerMap MarkerState", OnMarkerState);
                ZRoutedRpc.instance.Register<ZPackage>("ServerMap Overlay", OnOverlay);
            }
            catch (System.Exception ex)
            {
                Plugin.LogWarning("Could not register map messages. " + ex.Message);
            }
        }

        private static void OnHello(long sender, ZPackage pkg)
        {
            if (_session == null || !ZNet.instance.IsServer()) return;
            string version = pkg.ReadString();
            int size = pkg.ReadInt();
            if (version != Plugin.PluginVersion)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, "ServerMap Deny", "This server map is " + Plugin.PluginVersion + ".");
                return;
            }
            if (_session.TextureSize == 0)
            {
                _session.TextureSize = size;
                _session.Explored = new ExploredMap(size);
                _session.Known = new ExploredMap(size);
                _session.MarkDirty();
            }
            else if (size != _session.TextureSize)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, "ServerMap Deny", "Map size does not match this world.");
                return;
            }
            FlushOverlay();
            if (_session.Explored != null)
                SendChunks(sender, "ServerMap Explore", ++_stream, _session.Explored.ToGzip());
            SendMarkers(sender);
            if (Plugin.FootprintsOn())
            {
                byte[] overlay = MapFiles.ExportOverlay(_session.TextureSize, _session.Pieces.ToArray(), Cells(_session), _session.TreeCounts, Zones(_session));
                SendChunks(sender, "ServerMap Overlay", ++_stream, overlay);
            }
            _session.Peers.Add(sender);
        }

        private static void OnDeny(long sender, string reason)
        {
            if (_session == null) return;
            _session.Denied = true;
            _session.HelloDone = false;
            Plugin.LogWarning(reason);
        }

        private static void OnExplore(long sender, ZPackage pkg)
        {
            if (_session == null) return;
            if (ZNet.instance.IsServer() && !Joined(sender)) return;
            byte[] raw = _session.Net.Take(sender, pkg, Time.time);
            if (raw == null) return;
            int size = _session.TextureSize > 0 ? _session.TextureSize : (Minimap.instance != null ? Minimap.instance.m_textureSize : 0);
            if (size <= 0) return;
            ExploredMap map;
            try { map = ExploredMap.FromGzip(size, raw); }
            catch (System.Exception) { return; }
            if (ZNet.instance.IsServer())
            {
                if (_session.Explored == null) _session.Explored = new ExploredMap(size);
                if (_session.Explored.OrFrom(map))
                {
                    _session.MarkDirty();
                    BroadcastChunks("ServerMap Explore", raw, sender);
                }
            }
            if (Minimap.instance != null)
            {
                _session.TextureSize = size;
                if (_session.Known == null) _session.Known = new ExploredMap(size);
                _session.Known.OrFrom(map);
                _session.Painter.RevealMap(map);
                if (!ZNet.instance.IsServer())
                {
                    _session.ExploreReady = true;
                    FinishJoin();
                }
            }
        }

        private static void FinishJoin()
        {
            if (_session.HelloDone || !_session.ExploreReady || !_session.MarkersReady) return;
            _session.Painter.UploadLocalPins(_session, false);
            _session.HelloDone = true;
        }

        private static void OnIndexes(long sender, ZPackage pkg)
        {
            if (_session == null) return;
            if (ZNet.instance.IsServer() && !Joined(sender)) return;
            int count = pkg.ReadInt();
            if (count < 0 || count > 4096) return;
            List<int> indexes = new List<int>(count);
            for (int i = 0; i < count; i++) indexes.Add(pkg.ReadInt());
            if (ZNet.instance.IsServer())
            {
                if (_session.Explored == null && _session.TextureSize > 0)
                    _session.Explored = new ExploredMap(_session.TextureSize);
                if (_session.Explored != null)
                {
                    for (int i = 0; i < indexes.Count; i++) _session.Explored.Set(indexes[i]);
                    _session.MarkDirty();
                }
                BroadcastIndexes(_session, indexes, sender);
            }
            if (Minimap.instance != null) _session.Painter.RevealIndexes(_session, indexes);
        }

        private static void OnMarkers(long sender, ZPackage pkg)
        {
            if (_session == null || Minimap.instance == null) return;
            int count = pkg.ReadInt();
            for (int i = 0; i < count; i++)
            {
                long id = pkg.ReadLong();
                int type = pkg.ReadInt();
                string name = pkg.ReadString();
                float x = pkg.ReadSingle();
                float y = pkg.ReadSingle();
                float z = pkg.ReadSingle();
                bool isChecked = pkg.ReadBool();
                _session.Painter.ApplyMarker(_session, false, id, type, name, x, y, z, isChecked);
            }
            int tombs = pkg.ReadInt();
            for (int i = 0; i < tombs; i++)
            {
                pkg.ReadSingle();
                pkg.ReadSingle();
                pkg.ReadSingle();
                pkg.ReadInt();
                pkg.ReadString();
                pkg.ReadLong();
            }
            if (pkg.ReadBool())
            {
                _session.MarkersReady = true;
                FinishJoin();
            }
        }

        private static void OnMarker(long sender, ZPackage pkg)
        {
            if (_session == null || !ZNet.instance.IsServer()) return;
            if (sender != 0L && !Joined(sender)) return;
            byte op = pkg.ReadByte();
            if (op == 1)
            {
                int type = pkg.ReadInt();
                string name = pkg.ReadString();
                float x = pkg.ReadSingle();
                float y = pkg.ReadSingle();
                float z = pkg.ReadSingle();
                bool isChecked = pkg.ReadBool();
                bool duringPlay = pkg.ReadBool();
                long id;
                MarkerAccept accept = _session.Markers.TryAccept(type, name, x, y, z, isChecked, duringPlay, System.DateTime.UtcNow.Ticks, out id);
                if (accept == MarkerAccept.Tombstoned)
                {
                    RejectPin(sender, type, MarkerBook.Normalize(name), x, y, z);
                    return;
                }
                if (accept == MarkerAccept.Rejected) return;
                _session.MarkDirty();
                MarkerRecord marker = null;
                for (int i = 0; i < _session.Markers.Markers.Count; i++)
                {
                    if (_session.Markers.Markers[i].Id == id) marker = _session.Markers.Markers[i];
                }
                if (marker != null) BroadcastMarker(marker, false);
            }
            else if (op == 2)
            {
                long id = pkg.ReadLong();
                string name = pkg.ReadString();
                bool isChecked = pkg.ReadBool();
                if (!_session.Markers.Update(id, name, isChecked, System.DateTime.UtcNow.Ticks)) return;
                _session.MarkDirty();
                for (int i = 0; i < _session.Markers.Markers.Count; i++)
                {
                    if (_session.Markers.Markers[i].Id == id) BroadcastMarker(_session.Markers.Markers[i], false);
                }
            }
            else if (op == 3)
            {
                long id = pkg.ReadLong();
                if (!_session.Markers.Delete(id, System.DateTime.UtcNow.Ticks)) return;
                _session.MarkDirty();
                BroadcastDelete(id);
            }
        }

        private static void OnMarkerState(long sender, ZPackage pkg)
        {
            if (_session == null || Minimap.instance == null) return;
            bool remove = pkg.ReadBool();
            long id = pkg.ReadLong();
            int type = pkg.ReadInt();
            string name = pkg.ReadString();
            float x = pkg.ReadSingle();
            float y = pkg.ReadSingle();
            float z = pkg.ReadSingle();
            bool isChecked = pkg.ReadBool();
            _session.Painter.ApplyMarker(_session, remove, id, type, name, x, y, z, isChecked);
        }

        private static void OnOverlay(long sender, ZPackage pkg)
        {
            if (_session == null || ZNet.instance.IsServer()) return;
            byte[] raw = _session.Net.Take(sender, pkg, Time.time);
            if (raw == null || !Plugin.FootprintsOn()) return;
            if (raw.Length >= 4 && raw[0] == OverlayDelta.Magic[0] && raw[1] == OverlayDelta.Magic[1] && raw[2] == OverlayDelta.Magic[2] && raw[3] == OverlayDelta.Magic[3])
            {
                OverlayDelta delta;
                if (!OverlayDelta.TryRead(raw, out delta)) return;
                byte[] trees = _session.TreeCounts;
                delta.Apply(_session.Pieces, _session.Cells, ref trees, _session.Zones);
                _session.TreeCounts = trees;
                if (delta.NeedsFullPaint)
                    _session.OverlayDirty = true;
                else if (delta.CanPatchPaint)
                    delta.CopyAddedPieces(_session.PatchPieces);
                return;
            }
            int size;
            PieceRec[] pieces;
            PathCell[] cells;
            byte[] treeCounts;
            ZoneRec[] zones;
            if (!MapFiles.ImportOverlay(raw, out size, out pieces, out cells, out treeCounts, out zones)) return;
            if (_session.TextureSize != 0 && size != _session.TextureSize) return;
            _session.TextureSize = size;
            _session.Pieces.Clear();
            _session.Pieces.AddRange(pieces);
            _session.Cells.Clear();
            for (int i = 0; i < cells.Length; i++)
                _session.Cells[OverlayRules.PackKey(cells[i].X, cells[i].Z)] = cells[i].Kind;
            _session.TreeCounts = treeCounts ?? new byte[0];
            _session.Zones.Clear();
            for (int i = 0; i < zones.Length; i++)
                _session.Zones.Add(OverlayRules.PackKey(zones[i].X, zones[i].Y));
            _session.PatchPieces.Clear();
            _session.OverlayDirty = true;
        }

        internal byte[] Take(long sender, ZPackage pkg, float now)
        {
            int stream = pkg.ReadInt();
            int index = pkg.ReadInt();
            int count = pkg.ReadInt();
            byte[] part = pkg.ReadByteArray();
            return _chunks.Add(sender, stream, index, count, part, now);
        }

        internal static void BroadcastIndexes(MapSession session, List<int> indexes, long except)
        {
            if (ZRoutedRpc.instance == null || indexes == null || indexes.Count == 0) return;
            foreach (long peer in session.Peers)
            {
                if (peer == except) continue;
                ZRoutedRpc.instance.InvokeRoutedRPC(peer, "ServerMap Indexes", WriteIndexes(indexes));
            }
        }

        internal static bool SendIndexes(List<int> indexes)
        {
            if (ZRoutedRpc.instance == null || indexes == null || indexes.Count == 0) return false;
            long server = ServerId();
            if (server == 0L) return false;
            ZRoutedRpc.instance.InvokeRoutedRPC(server, "ServerMap Indexes", WriteIndexes(indexes));
            return true;
        }

        internal static bool SendExplore(byte[] gzip)
        {
            long server = ServerId();
            if (server == 0L) return false;
            SendChunks(server, "ServerMap Explore", ++_stream, gzip);
            return true;
        }

        internal static void SendMarkerCreate(int type, string name, Vector3 pos, bool isChecked, bool duringPlay)
        {
            ZPackage pkg = new ZPackage();
            pkg.Write((byte)1);
            pkg.Write(type);
            pkg.Write(name ?? "");
            pkg.Write(pos.x);
            pkg.Write(pos.y);
            pkg.Write(pos.z);
            pkg.Write(isChecked);
            pkg.Write(duringPlay);
            SubmitMarker(pkg);
        }

        internal static void SendMarkerUpdate(long id, string name, bool isChecked)
        {
            ZPackage pkg = new ZPackage();
            pkg.Write((byte)2);
            pkg.Write(id);
            pkg.Write(name ?? "");
            pkg.Write(isChecked);
            SubmitMarker(pkg);
        }

        internal static void SendMarkerDelete(long id)
        {
            ZPackage pkg = new ZPackage();
            pkg.Write((byte)3);
            pkg.Write(id);
            SubmitMarker(pkg);
        }

        private static void SubmitMarker(ZPackage pkg)
        {
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                pkg.SetPos(0);
                OnMarker(0L, pkg);
                return;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerId(), "ServerMap Marker", pkg);
        }

        private static bool Joined(long sender)
        {
            return _session != null && _session.Peers.Contains(sender);
        }

        private static long ServerId()
        {
            ZNetPeer peer = ZNet.instance.GetServerPeer();
            return peer == null ? 0L : peer.m_uid;
        }

        private static void Prune(MapSession session)
        {
            if (_peersField == null) _peersField = AccessTools.Field(typeof(ZNet), "m_peers");
            if (_peersField == null || ZNet.instance == null) return;
            List<ZNetPeer> peers = _peersField.GetValue(ZNet.instance) as List<ZNetPeer>;
            if (peers == null) return;
            HashSet<long> live = new HashSet<long>();
            for (int i = 0; i < peers.Count; i++)
            {
                if (peers[i] != null) live.Add(peers[i].m_uid);
            }
            List<long> gone = null;
            foreach (long id in session.Peers)
            {
                if (live.Contains(id)) continue;
                if (gone == null) gone = new List<long>();
                gone.Add(id);
            }
            if (gone == null) return;
            for (int i = 0; i < gone.Count; i++) session.Peers.Remove(gone[i]);
        }

        private static ZPackage WriteIndexes(List<int> indexes)
        {
            ZPackage pkg = new ZPackage();
            pkg.Write(indexes.Count);
            for (int i = 0; i < indexes.Count; i++) pkg.Write(indexes[i]);
            return pkg;
        }

        private static void SendMarkers(long sender)
        {
            int count = _session.Markers.Markers.Count;
            int tombs = _session.Markers.Tombstones.Count;
            int sentM = 0;
            int sentT = 0;
            bool any = false;
            while (sentM < count || sentT < tombs || !any)
            {
                any = true;
                ZPackage pkg = new ZPackage();
                int batch = Mathf.Min(50, count - sentM);
                pkg.Write(batch);
                for (int i = 0; i < batch; i++)
                {
                    MarkerRecord marker = _session.Markers.Markers[sentM++];
                    pkg.Write(marker.Id);
                    pkg.Write(marker.Type);
                    pkg.Write(marker.Name ?? "");
                    pkg.Write(marker.X);
                    pkg.Write(marker.Y);
                    pkg.Write(marker.Z);
                    pkg.Write(marker.Checked);
                }
                int tombBatch = batch == 0 ? Mathf.Min(50, tombs - sentT) : 0;
                if (sentM >= count) tombBatch = Mathf.Min(50, tombs - sentT);
                pkg.Write(tombBatch);
                for (int i = 0; i < tombBatch; i++)
                {
                    MarkerTombstone tomb = _session.Markers.Tombstones[sentT++];
                    pkg.Write(tomb.X);
                    pkg.Write(tomb.Y);
                    pkg.Write(tomb.Z);
                    pkg.Write(tomb.Type);
                    pkg.Write(tomb.Name ?? "");
                    pkg.Write(tomb.Revision);
                }
                pkg.Write(sentM >= count && sentT >= tombs);
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, "ServerMap Markers", pkg);
                if (sentM >= count && sentT >= tombs) break;
            }
        }

        private static void BroadcastMarker(MarkerRecord marker, bool remove)
        {
            ZPackage pkg = new ZPackage();
            pkg.Write(remove);
            pkg.Write(marker.Id);
            pkg.Write(marker.Type);
            pkg.Write(marker.Name ?? "");
            pkg.Write(marker.X);
            pkg.Write(marker.Y);
            pkg.Write(marker.Z);
            pkg.Write(marker.Checked);
            foreach (long peer in _session.Peers)
                ZRoutedRpc.instance.InvokeRoutedRPC(peer, "ServerMap MarkerState", pkg);
            if (Minimap.instance != null) _session.Painter.ApplyMarker(_session, false, marker.Id, marker.Type, marker.Name, marker.X, marker.Y, marker.Z, marker.Checked);
        }

        private static void RejectPin(long sender, int type, string name, float x, float y, float z)
        {
            if (sender == 0L)
            {
                if (Minimap.instance != null)
                    _session.Painter.ApplyMarker(_session, true, -1L, type, name, x, y, z, false);
                return;
            }
            ZPackage pkg = new ZPackage();
            pkg.Write(true);
            pkg.Write(-1L);
            pkg.Write(type);
            pkg.Write(name ?? "");
            pkg.Write(x);
            pkg.Write(y);
            pkg.Write(z);
            pkg.Write(false);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "ServerMap MarkerState", pkg);
        }

        private static void BroadcastDelete(long id)
        {
            ZPackage pkg = new ZPackage();
            pkg.Write(true);
            pkg.Write(id);
            pkg.Write(0);
            pkg.Write("");
            pkg.Write(0f);
            pkg.Write(0f);
            pkg.Write(0f);
            pkg.Write(false);
            foreach (long peer in _session.Peers)
                ZRoutedRpc.instance.InvokeRoutedRPC(peer, "ServerMap MarkerState", pkg);
            if (Minimap.instance != null) _session.Painter.ApplyMarker(_session, true, id, 0, "", 0f, 0f, 0f, false);
        }

        private static void FlushOverlay()
        {
            if (_session == null || _session.Pending.IsEmpty) return;
            byte[] bytes = _session.Pending.ToBytes();
            _session.Pending.Clear();
            BroadcastChunks("ServerMap Overlay", bytes, 0L);
        }

        internal static void BroadcastExplore(byte[] gzip)
        {
            BroadcastChunks("ServerMap Explore", gzip, 0L);
        }

        private static void BroadcastChunks(string method, byte[] bytes, long except)
        {
            if (_session == null || _session.Peers.Count == 0) return;
            int stream = ++_stream;
            byte[][] parts = ByteChunks.Split(bytes ?? new byte[0]);
            foreach (long peer in _session.Peers)
            {
                if (peer == except) continue;
                for (int i = 0; i < parts.Length; i++)
                {
                    ZPackage pkg = new ZPackage();
                    pkg.Write(stream);
                    pkg.Write(i);
                    pkg.Write(parts.Length);
                    pkg.Write(parts[i]);
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer, method, pkg);
                }
            }
        }

        private static void SendChunks(long peer, string method, int stream, byte[] bytes)
        {
            byte[][] parts = ByteChunks.Split(bytes ?? new byte[0]);
            for (int i = 0; i < parts.Length; i++)
            {
                ZPackage pkg = new ZPackage();
                pkg.Write(stream);
                pkg.Write(i);
                pkg.Write(parts.Length);
                pkg.Write(parts[i]);
                ZRoutedRpc.instance.InvokeRoutedRPC(peer, method, pkg);
            }
        }

        private static PathCell[] Cells(MapSession session)
        {
            PathCell[] cells = new PathCell[session.Cells.Count];
            int i = 0;
            foreach (KeyValuePair<long, byte> pair in session.Cells)
            {
                cells[i].X = OverlayRules.KeyX(pair.Key);
                cells[i].Z = OverlayRules.KeyY(pair.Key);
                cells[i].Kind = pair.Value;
                i++;
            }
            return cells;
        }

        private static ZoneRec[] Zones(MapSession session)
        {
            ZoneRec[] zones = new ZoneRec[session.Zones.Count];
            int i = 0;
            foreach (long key in session.Zones)
            {
                zones[i].X = OverlayRules.KeyX(key);
                zones[i].Y = OverlayRules.KeyY(key);
                i++;
            }
            return zones;
        }
    }
}
