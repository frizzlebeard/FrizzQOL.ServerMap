using System;
using System.Collections.Generic;

namespace ServerMap
{
    public enum MarkerAccept
    {
        Created,
        Bound,
        Rejected,
        Tombstoned
    }

    public sealed class MarkerRecord
    {
        public long Id;
        public int Type;
        public string Name;
        public float X;
        public float Y;
        public float Z;
        public bool Checked;
        public long Revision;
    }

    public sealed class MarkerTombstone
    {
        public float X;
        public float Y;
        public float Z;
        public int Type;
        public string Name;
        public long Revision;
    }

    public sealed class MarkerBook
    {
        public const int MaxNameLength = 32;
        public const float BindMeters = 1f;

        private readonly List<MarkerRecord> _markers = new List<MarkerRecord>();
        private readonly List<MarkerTombstone> _tombs = new List<MarkerTombstone>();
        private long _nextId = 1;

        public IList<MarkerRecord> Markers { get { return _markers; } }
        public IList<MarkerTombstone> Tombstones { get { return _tombs; } }
        public long NextId { get { return _nextId; } }

        public static bool IsSharedType(int type)
        {
            return type == 0 || type == 1 || type == 2 || type == 3 || type == 6
                || type == 9 || type == 14 || type == 15 || type == 16 || type == 17;
        }

        public static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string cleaned = name.Replace('$', ' ').Replace('<', ' ').Replace('>', ' ').Trim();
            if (cleaned.Length > MaxNameLength) return null;
            return cleaned;
        }

        public void NoteHighestId(long id)
        {
            if (id >= _nextId) _nextId = id + 1;
        }

        public void Restore(MarkerRecord marker, MarkerTombstone[] tombs)
        {
            if (marker != null)
            {
                _markers.Add(marker);
                NoteHighestId(marker.Id);
            }
            if (tombs == null) return;
            for (int i = 0; i < tombs.Length; i++) _tombs.Add(tombs[i]);
        }

        public MarkerAccept TryAccept(int type, string rawName, float x, float y, float z, bool isChecked, bool duringPlay, long revision, out long id)
        {
            id = 0;
            if (!IsSharedType(type)) return MarkerAccept.Rejected;
            if (!Finite(x) || !Finite(y) || !Finite(z)) return MarkerAccept.Rejected;
            string name = Normalize(rawName);
            if (name == null) return MarkerAccept.Rejected;

            MarkerRecord existing = FindMarker(type, name, x, y, z);
            if (existing != null)
            {
                id = existing.Id;
                return MarkerAccept.Bound;
            }

            int tomb = FindTomb(type, name, x, y, z);
            if (tomb >= 0)
            {
                if (!duringPlay) return MarkerAccept.Tombstoned;
                _tombs.RemoveAt(tomb);
            }

            MarkerRecord created = new MarkerRecord();
            created.Id = _nextId++;
            created.Type = type;
            created.Name = name;
            created.X = x;
            created.Y = y;
            created.Z = z;
            created.Checked = isChecked;
            created.Revision = revision;
            _markers.Add(created);
            id = created.Id;
            return MarkerAccept.Created;
        }

        public bool Update(long id, string rawName, bool isChecked, long revision)
        {
            MarkerRecord marker = FindId(id);
            if (marker == null) return false;
            string name = Normalize(rawName);
            if (name == null) return false;
            marker.Name = name;
            marker.Checked = isChecked;
            marker.Revision = revision;
            return true;
        }

        public bool Delete(long id, long revision)
        {
            MarkerRecord marker = FindId(id);
            if (marker == null) return false;
            MarkerTombstone tomb = new MarkerTombstone();
            tomb.X = marker.X;
            tomb.Y = marker.Y;
            tomb.Z = marker.Z;
            tomb.Type = marker.Type;
            tomb.Name = marker.Name;
            tomb.Revision = revision;
            _tombs.Add(tomb);
            _markers.Remove(marker);
            return true;
        }

        private MarkerRecord FindId(long id)
        {
            for (int i = 0; i < _markers.Count; i++)
            {
                if (_markers[i].Id == id) return _markers[i];
            }
            return null;
        }

        private MarkerRecord FindMarker(int type, string name, float x, float y, float z)
        {
            for (int i = 0; i < _markers.Count; i++)
            {
                MarkerRecord marker = _markers[i];
                if (marker.Type == type && marker.Name == name && Near(marker.X, marker.Y, marker.Z, x, y, z))
                    return marker;
            }
            return null;
        }

        private int FindTomb(int type, string name, float x, float y, float z)
        {
            for (int i = 0; i < _tombs.Count; i++)
            {
                MarkerTombstone tomb = _tombs[i];
                if (tomb.Type == type && tomb.Name == name && Near(tomb.X, tomb.Y, tomb.Z, x, y, z))
                    return i;
            }
            return -1;
        }

        private static bool Near(float ax, float ay, float az, float bx, float by, float bz)
        {
            float dx = ax - bx;
            float dy = ay - by;
            float dz = az - bz;
            return (dx * dx) + (dy * dy) + (dz * dz) <= BindMeters * BindMeters;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
