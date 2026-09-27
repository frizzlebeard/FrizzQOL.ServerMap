using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ServerMap
{
    internal sealed class MapPainter
    {
        private const int LiveIndexCap = 256;

        private bool _paintedFile;
        private bool _exploreCatchUp;
        private static bool _applyingRemote;
        private static readonly List<int> _fresh = new List<int>();
        private static readonly HashSet<int> _freshSeen = new HashSet<int>();
        private readonly Dictionary<Minimap.PinData, long> _ids = new Dictionary<Minimap.PinData, long>();
        private readonly Dictionary<long, PinSent> _sent = new Dictionary<long, PinSent>();
        private readonly HashSet<long> _seenIds = new HashSet<long>();
        private bool _loggedFog;

        internal void Reset()
        {
            _paintedFile = false;
            _exploreCatchUp = false;
            _fresh.Clear();
            _freshSeen.Clear();
            _ids.Clear();
            _sent.Clear();
            _seenIds.Clear();
        }

        internal void Tick(MapSession session)
        {
            if (!_paintedFile && session.Explored != null && ZNet.instance.IsServer())
            {
                _paintedFile = true;
                RevealMap(session.Explored);
            }
        }

        internal void CollectExplore(MapSession session)
        {
            Minimap map = Minimap.instance;
            if (map == null || session.Denied) return;
            if (!ZNet.instance.IsServer() && !session.HelloDone) return;
            BitArray bits = Bits(map);
            if (bits == null) return;
            if (session.TextureSize == 0) return;
            if (session.Known == null) session.Known = new ExploredMap(session.TextureSize);
            int length = Mathf.Min(bits.Length, session.Known.BitCount);
            if (!_exploreCatchUp)
            {
                _exploreCatchUp = true;
                SendExploreGzip(session, bits, length);
            }
            FlushFresh(session);
        }

        internal static void NoteExplored(int index)
        {
            if (_applyingRemote || index < 0) return;
            if (_freshSeen.Add(index)) _fresh.Add(index);
        }

        private void FlushFresh(MapSession session)
        {
            if (_fresh.Count == 0 || session.Denied) return;
            if (!ZNet.instance.IsServer() && !session.HelloDone) return;
            if (ZRoutedRpc.instance == null) return;
            int take = _fresh.Count < LiveIndexCap ? _fresh.Count : LiveIndexCap;
            List<int> batch = new List<int>(take);
            for (int i = 0; i < take; i++)
            {
                int index = _fresh[i];
                if (session.Known != null && session.Known.Get(index)) continue;
                batch.Add(index);
            }
            if (batch.Count > 0)
            {
                if (ZNet.instance.IsServer()) MapNet.BroadcastIndexes(session, batch, 0);
                else if (!MapNet.SendIndexes(batch)) return;
                RememberExplored(session, batch);
            }
            for (int i = 0; i < take; i++) _freshSeen.Remove(_fresh[i]);
            _fresh.RemoveRange(0, take);
        }

        private void SendExploreGzip(MapSession session, BitArray bits, int length)
        {
            ExploredMap extra = new ExploredMap(session.TextureSize);
            bool any = false;
            for (int i = 0; i < length; i++)
            {
                if (!bits[i] || session.Known.Get(i)) continue;
                extra.Set(i);
                any = true;
            }
            if (!any) return;
            if (ZNet.instance.IsServer())
            {
                if (session.Explored == null) session.Explored = new ExploredMap(session.TextureSize);
                session.Explored.OrFrom(extra);
                session.MarkDirty();
                for (int i = 0; i < length; i++) if (extra.Get(i)) session.Known.Set(i);
                MapNet.BroadcastExplore(extra.ToGzip());
                if (Plugin.RespectFog.Value) session.FogDirty = true;
            }
            else if (MapNet.SendExplore(extra.ToGzip()))
            {
                for (int i = 0; i < length; i++) if (extra.Get(i)) session.Known.Set(i);
                if (Plugin.RespectFog.Value) session.FogDirty = true;
            }
            else
            {
                _exploreCatchUp = false;
            }
        }

        private static void RememberExplored(MapSession session, List<int> extra)
        {
            if (ZNet.instance.IsServer())
            {
                if (session.Explored == null) session.Explored = new ExploredMap(session.TextureSize);
                for (int i = 0; i < extra.Count; i++) session.Explored.Set(extra[i]);
                session.MarkDirty();
            }
            for (int i = 0; i < extra.Count; i++) session.Known.Set(extra[i]);
            if (Plugin.RespectFog.Value) session.FogDirty = true;
        }

        internal void RevealIndexes(MapSession session, List<int> indexes)
        {
            Minimap map = Minimap.instance;
            if (map == null || indexes == null) return;
            _applyingRemote = true;
            try
            {
            BitArray bits = Bits(map);
            Texture2D fog = Fog(map);
            if (bits == null) return;
            if (session.Known == null && session.TextureSize > 0) session.Known = new ExploredMap(session.TextureSize);
            bool changed = false;
            for (int i = 0; i < indexes.Count; i++)
            {
                int index = indexes[i];
                if (index < 0 || index >= bits.Length) continue;
                if (session.Known != null) session.Known.Set(index);
                if (bits[index]) continue;
                if (!ExplorePixel(map, index)) continue;
                changed = true;
            }
            if (changed) ApplyFog(fog);
            if (changed && Plugin.RespectFog.Value) session.FogDirty = true;
            }
            finally { _applyingRemote = false; }
        }

        internal void RevealMap(ExploredMap incoming)
        {
            if (incoming == null) return;
            Minimap map = Minimap.instance;
            if (map == null) return;
            _applyingRemote = true;
            try
            {
            BitArray bits = Bits(map);
            Texture2D fog = Fog(map);
            if (bits == null) return;
            Color32[] pixels = null;
            if (fog != null && fog.width * fog.height == incoming.BitCount)
            {
                try { pixels = fog.GetPixels32(); }
                catch { pixels = null; }
            }
            bool changed = false;
            for (int i = 0; i < incoming.BitCount && i < bits.Length; i++)
            {
                if (!incoming.Get(i) || bits[i]) continue;
                if (pixels != null)
                {
                    bits.Set(i, true);
                    pixels[i].r = 0;
                }
                else if (!ExplorePixel(map, i)) continue;
                changed = true;
            }
            if (!changed) return;
            if (pixels != null)
            {
                try
                {
                    fog.SetPixels32(pixels);
                    fog.Apply();
                }
                catch
                {
                    if (!_loggedFog) { _loggedFog = true; Plugin.LogWarning("Could not update the fog texture."); }
                }
            }
            else if (fog != null)
            {
                try { fog.Apply(); } catch { }
            }
            }
            finally { _applyingRemote = false; }
        }

        internal void ApplyMarker(MapSession session, bool remove, long id, int type, string name, float x, float y, float z, bool isChecked)
        {
            Minimap map = Minimap.instance;
            if (map == null) return;
            Minimap.PinData pin = FindId(id);
            if (remove)
            {
                if (id < 0) pin = FindUnbound(type, name, x, y, z);
                if (pin != null)
                {
                    _ids.Remove(pin);
                    map.RemovePin(pin);
                }
                _sent.Remove(id);
                _seenIds.Remove(id);
                return;
            }
            _seenIds.Add(id);
            if (pin == null) pin = FindUnbound(type, name, x, y, z);
            if (pin == null)
            {
                pin = map.AddPin(new Vector3(x, y, z), (Minimap.PinType)type, name, true, isChecked);
                if (pin == null) return;
            }
            else
            {
                pin.m_name = name;
                pin.m_checked = isChecked;
                pin.m_pos = new Vector3(x, y, z);
                pin.m_save = true;
            }
            _ids[pin] = id;
            _sent[id] = new PinSent { Name = name ?? "", Checked = isChecked };
            ShowPinText(map, pin);
        }

        private static System.Reflection.MethodInfo _createName;
        private static System.Reflection.FieldInfo _nameRoot;

        internal static void ShowPinText(Minimap map, Minimap.PinData pin)
        {
            if (map == null || pin == null || string.IsNullOrEmpty(pin.m_name)) return;
            if (pin.m_NamePinData == null) pin.m_NamePinData = new Minimap.PinNameData(pin);
            Minimap.PinNameData data = pin.m_NamePinData;
            if (data.PinNameGameObject == null)
            {
                if (_nameRoot == null) _nameRoot = AccessTools.Field(typeof(Minimap), "m_pinNameRootLarge");
                if (_createName == null) _createName = AccessTools.Method(typeof(Minimap), "CreateMapNamePin");
                RectTransform root = _nameRoot == null ? null : _nameRoot.GetValue(map) as RectTransform;
                if (root == null || _createName == null) return;
                try { _createName.Invoke(map, new object[] { pin, root }); }
                catch (System.Exception ex)
                {
                    Plugin.LogWarning("Could not show a pin name. " + ex.Message);
                }
                return;
            }
            if (data.PinNameText != null && data.PinNameText.text != pin.m_name)
                data.PinNameText.text = pin.m_name;
        }

        internal void UploadLocalPins(MapSession session, bool duringPlay)
        {
            Minimap map = Minimap.instance;
            if (map == null || session.Denied) return;
            List<Minimap.PinData> pins = PinList(map);
            if (pins == null) return;
            for (int i = pins.Count - 1; i >= 0; i--)
            {
                Minimap.PinData pin = pins[i];
                if (pin == null) continue;
                long id;
                if (_ids.TryGetValue(pin, out id) && id != 0) continue;
                if (!MarkerBook.IsSharedType((int)pin.m_type)) continue;
                if (MarkerBook.Normalize(pin.m_name) == null)
                {
                    Plugin.LogWarning("A pin name longer than 32 characters stays on this map only.");
                    continue;
                }
                MapNet.SendMarkerCreate((int)pin.m_type, pin.m_name, pin.m_pos, pin.m_checked, duringPlay);
                long bound;
                if (!_ids.TryGetValue(pin, out bound)) _ids[pin] = 0;
            }
        }

        internal void WatchPins(MapSession session)
        {
            if (!session.HelloDone || session.Denied) return;
            Minimap map = Minimap.instance;
            if (map == null) return;
            List<Minimap.PinData> pins = PinList(map);
            if (pins == null) return;
            HashSet<Minimap.PinData> alive = new HashSet<Minimap.PinData>();
            for (int i = 0; i < pins.Count; i++)
            {
                Minimap.PinData pin = pins[i];
                if (pin == null || !MarkerBook.IsSharedType((int)pin.m_type)) continue;
                alive.Add(pin);
                long id;
                if (!_ids.TryGetValue(pin, out id))
                {
                    if (MarkerBook.Normalize(pin.m_name) == null) continue;
                    MapNet.SendMarkerCreate((int)pin.m_type, pin.m_name, pin.m_pos, pin.m_checked, true);
                    long bound;
                    if (!_ids.TryGetValue(pin, out bound)) _ids[pin] = 0;
                }
                else if (id != 0)
                {
                    string name = MarkerBook.Normalize(pin.m_name);
                    if (name == null) continue;
                    PinSent sent;
                    if (_sent.TryGetValue(id, out sent) && (sent.Name != name || sent.Checked != pin.m_checked))
                    {
                        MapNet.SendMarkerUpdate(id, name, pin.m_checked);
                        _sent[id] = new PinSent { Name = name, Checked = pin.m_checked };
                    }
                }
            }
            List<Minimap.PinData> gone = new List<Minimap.PinData>();
            foreach (KeyValuePair<Minimap.PinData, long> pair in _ids)
            {
                if (!alive.Contains(pair.Key) && pair.Value != 0) gone.Add(pair.Key);
            }
            for (int i = 0; i < gone.Count; i++)
            {
                long id = _ids[gone[i]];
                _ids.Remove(gone[i]);
                _sent.Remove(id);
                MapNet.SendMarkerDelete(id);
            }
        }

        private Minimap.PinData FindId(long id)
        {
            foreach (KeyValuePair<Minimap.PinData, long> pair in _ids)
            {
                if (pair.Value == id) return pair.Key;
            }
            return null;
        }

        private Minimap.PinData FindUnbound(int type, string name, float x, float y, float z)
        {
            foreach (KeyValuePair<Minimap.PinData, long> pair in _ids)
            {
                if (pair.Value != 0) continue;
                Minimap.PinData pin = pair.Key;
                if (pin == null || (int)pin.m_type != type) continue;
                if (MarkerBook.Normalize(pin.m_name) != name) continue;
                float dx = pin.m_pos.x - x;
                float dy = pin.m_pos.y - y;
                float dz = pin.m_pos.z - z;
                if ((dx * dx) + (dy * dy) + (dz * dz) <= 1f) return pin;
            }
            return null;
        }

        private static System.Reflection.MethodInfo _explorePixel;

        private static bool ExplorePixel(Minimap map, int index)
        {
            if (map.m_textureSize <= 0) return false;
            if (_explorePixel == null)
                _explorePixel = AccessTools.Method(typeof(Minimap), "Explore", new System.Type[] { typeof(int), typeof(int) });
            if (_explorePixel == null) return false;
            int x = index % map.m_textureSize;
            int y = index / map.m_textureSize;
            try
            {
                object result = _explorePixel.Invoke(map, new object[] { x, y });
                return result is bool && (bool)result;
            }
            catch
            {
                return false;
            }
        }

        private void ApplyFog(Texture2D fog)
        {
            if (fog == null) return;
            try { fog.Apply(); }
            catch
            {
                if (!_loggedFog)
                {
                    _loggedFog = true;
                    Plugin.LogWarning("Could not update the fog texture.");
                }
            }
        }

        private static BitArray Bits(Minimap map)
        {
            return AccessTools.Field(typeof(Minimap), "m_explored").GetValue(map) as BitArray;
        }

        private static Texture2D Fog(Minimap map)
        {
            return AccessTools.Field(typeof(Minimap), "m_fogTexture").GetValue(map) as Texture2D;
        }

        private static List<Minimap.PinData> PinList(Minimap map)
        {
            return AccessTools.Field(typeof(Minimap), "m_pins").GetValue(map) as List<Minimap.PinData>;
        }

        private struct PinSent
        {
            public string Name;
            public bool Checked;
        }
    }
}
