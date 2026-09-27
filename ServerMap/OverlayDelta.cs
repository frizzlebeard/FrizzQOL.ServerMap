using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ServerMap
{
    public sealed class OverlayDelta
    {
        public static readonly byte[] Magic = { (byte)'S', (byte)'M', (byte)'O', (byte)'D' };
        public const int Version = 1;
        private const int MaxPieces = 300000;
        private const int MaxCells = 500000;
        private const int MaxTrees = 8000000;
        private const int MaxZones = 200000;

        private readonly Dictionary<PieceKey, PieceRec> _removed = new Dictionary<PieceKey, PieceRec>();
        private readonly Dictionary<PieceKey, PieceRec> _added = new Dictionary<PieceKey, PieceRec>();
        private readonly Dictionary<long, byte> _cells = new Dictionary<long, byte>();
        private readonly Dictionary<int, byte> _trees = new Dictionary<int, byte>();
        private readonly Dictionary<long, bool> _zones = new Dictionary<long, bool>();

        public bool IsEmpty
        {
            get { return _removed.Count == 0 && _added.Count == 0 && _cells.Count == 0 && _trees.Count == 0 && _zones.Count == 0; }
        }

        public bool CanPatchPaint
        {
            get { return _removed.Count == 0 && _cells.Count == 0 && _added.Count > 0; }
        }

        public bool NeedsFullPaint
        {
            get { return _removed.Count > 0 || _cells.Count > 0; }
        }

        public void CopyAddedPieces(List<PieceRec> destination)
        {
            if (destination == null) return;
            foreach (PieceRec piece in _added.Values) destination.Add(piece);
        }

        public void Clear()
        {
            _removed.Clear();
            _added.Clear();
            _cells.Clear();
            _trees.Clear();
            _zones.Clear();
        }

        public void SetCell(int x, int z, byte kind)
        {
            _cells[OverlayRules.PackKey(x, z)] = kind;
        }

        public void DiffPieces(IList<PieceRec> oldPieces, IList<PieceRec> next)
        {
            Dictionary<PieceKey, PieceRec> before = Index(oldPieces);
            Dictionary<PieceKey, PieceRec> after = Index(next);
            foreach (KeyValuePair<PieceKey, PieceRec> pair in before)
            {
                if (!after.ContainsKey(pair.Key)) RemovePiece(pair.Key, pair.Value);
            }
            foreach (KeyValuePair<PieceKey, PieceRec> pair in after)
            {
                if (!before.ContainsKey(pair.Key)) AddPiece(pair.Key, pair.Value);
            }
        }

        public void DiffTrees(byte[] oldCounts, byte[] next)
        {
            int n = Math.Max(oldCounts == null ? 0 : oldCounts.Length, next == null ? 0 : next.Length);
            for (int i = 0; i < n; i++)
            {
                byte left = oldCounts != null && i < oldCounts.Length ? oldCounts[i] : (byte)0;
                byte right = next != null && i < next.Length ? next[i] : (byte)0;
                if (left != right) _trees[i] = right;
            }
        }

        public bool DiffZones(HashSet<long> oldZones, HashSet<long> next)
        {
            bool any = false;
            if (oldZones != null)
            {
                foreach (long key in oldZones)
                {
                    if (next == null || !next.Contains(key))
                    {
                        SetZone(key, false);
                        any = true;
                    }
                }
            }
            if (next != null)
            {
                foreach (long key in next)
                {
                    if (oldZones == null || !oldZones.Contains(key))
                    {
                        SetZone(key, true);
                        any = true;
                    }
                }
            }
            return any;
        }

        public byte[] ToBytes()
        {
            using (MemoryStream body = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(body))
            {
                writer.Write(_removed.Count);
                foreach (PieceRec piece in _removed.Values) WritePiece(writer, piece);
                writer.Write(_added.Count);
                foreach (PieceRec piece in _added.Values) WritePiece(writer, piece);
                writer.Write(_cells.Count);
                foreach (KeyValuePair<long, byte> pair in _cells)
                {
                    writer.Write(OverlayRules.KeyX(pair.Key));
                    writer.Write(OverlayRules.KeyY(pair.Key));
                    writer.Write(pair.Value);
                }
                writer.Write(_trees.Count);
                foreach (KeyValuePair<int, byte> pair in _trees)
                {
                    writer.Write(pair.Key);
                    writer.Write(pair.Value);
                }
                writer.Write(_zones.Count);
                foreach (KeyValuePair<long, bool> pair in _zones)
                {
                    writer.Write(OverlayRules.KeyX(pair.Key));
                    writer.Write(OverlayRules.KeyY(pair.Key));
                    writer.Write(pair.Value);
                }
                writer.Flush();
                byte[] gzip = Gzip(body.ToArray());
                using (MemoryStream raw = new MemoryStream())
                using (BinaryWriter head = new BinaryWriter(raw))
                {
                    head.Write(Magic);
                    head.Write(Version);
                    head.Write(gzip.Length);
                    head.Write(gzip);
                    head.Flush();
                    return raw.ToArray();
                }
            }
        }

        public static bool TryRead(byte[] data, out OverlayDelta delta)
        {
            delta = null;
            try
            {
                if (data == null || data.Length < 12) return false;
                for (int i = 0; i < 4; i++) if (data[i] != Magic[i]) return false;
                using (BinaryReader reader = new BinaryReader(new MemoryStream(data)))
                {
                    reader.ReadBytes(4);
                    if (reader.ReadInt32() != Version) return false;
                    int gzipLength = reader.ReadInt32();
                    if (gzipLength < 0 || gzipLength > data.Length) return false;
                    byte[] body = Gunzip(reader.ReadBytes(gzipLength));
                    using (BinaryReader inner = new BinaryReader(new MemoryStream(body)))
                    {
                        delta = new OverlayDelta();
                        int removed = inner.ReadInt32();
                        if (removed < 0 || removed > MaxPieces) return false;
                        for (int i = 0; i < removed; i++)
                        {
                            PieceRec piece = ReadPiece(inner);
                            delta._removed[new PieceKey(piece)] = piece;
                        }
                        int added = inner.ReadInt32();
                        if (added < 0 || added > MaxPieces) return false;
                        for (int i = 0; i < added; i++)
                        {
                            PieceRec piece = ReadPiece(inner);
                            delta._added[new PieceKey(piece)] = piece;
                        }
                        int cells = inner.ReadInt32();
                        if (cells < 0 || cells > MaxCells) return false;
                        for (int i = 0; i < cells; i++)
                            delta._cells[OverlayRules.PackKey(inner.ReadInt32(), inner.ReadInt32())] = inner.ReadByte();
                        int trees = inner.ReadInt32();
                        if (trees < 0 || trees > MaxTrees) return false;
                        for (int i = 0; i < trees; i++)
                            delta._trees[inner.ReadInt32()] = inner.ReadByte();
                        int zones = inner.ReadInt32();
                        if (zones < 0 || zones > MaxZones) return false;
                        for (int i = 0; i < zones; i++)
                            delta._zones[OverlayRules.PackKey(inner.ReadInt32(), inner.ReadInt32())] = inner.ReadBoolean();
                    }
                }
                return true;
            }
            catch (Exception)
            {
                delta = null;
                return false;
            }
        }

        public void Apply(List<PieceRec> pieces, Dictionary<long, byte> cells, ref byte[] trees, HashSet<long> zones)
        {
            if (pieces != null && _removed.Count == 0)
            {
                foreach (PieceRec piece in _added.Values) pieces.Add(piece);
            }
            else if (pieces != null && _removed.Count > 0)
            {
                List<PieceRec> kept = new List<PieceRec>(pieces.Count);
                for (int i = 0; i < pieces.Count; i++)
                {
                    if (!_removed.ContainsKey(new PieceKey(pieces[i]))) kept.Add(pieces[i]);
                }
                pieces.Clear();
                pieces.AddRange(kept);
                foreach (PieceRec piece in _added.Values) pieces.Add(piece);
            }
            if (cells != null)
            {
                foreach (KeyValuePair<long, byte> pair in _cells)
                {
                    if (pair.Value == OverlayRules.KindNone) cells.Remove(pair.Key);
                    else cells[pair.Key] = pair.Value;
                }
            }
            if (_trees.Count > 0)
            {
                int size = trees == null ? 0 : trees.Length;
                foreach (int index in _trees.Keys)
                {
                    if (index + 1 > size) size = index + 1;
                }
                if (trees == null || trees.Length != size)
                {
                    byte[] grown = new byte[size];
                    if (trees != null) Buffer.BlockCopy(trees, 0, grown, 0, Math.Min(trees.Length, size));
                    trees = grown;
                }
                foreach (KeyValuePair<int, byte> pair in _trees)
                {
                    if (pair.Key >= 0 && pair.Key < trees.Length) trees[pair.Key] = pair.Value;
                }
            }
            if (zones != null)
            {
                foreach (KeyValuePair<long, bool> pair in _zones)
                {
                    if (pair.Value) zones.Add(pair.Key);
                    else zones.Remove(pair.Key);
                }
            }
        }

        private void RemovePiece(PieceKey key, PieceRec piece)
        {
            if (_added.Remove(key)) return;
            _removed[key] = piece;
        }

        private void AddPiece(PieceKey key, PieceRec piece)
        {
            if (_removed.Remove(key)) return;
            _added[key] = piece;
        }

        private void SetZone(long key, bool present)
        {
            bool current;
            if (_zones.TryGetValue(key, out current))
            {
                if (current != present) _zones.Remove(key);
                return;
            }
            _zones[key] = present;
        }

        private static Dictionary<PieceKey, PieceRec> Index(IList<PieceRec> pieces)
        {
            Dictionary<PieceKey, PieceRec> map = new Dictionary<PieceKey, PieceRec>();
            if (pieces == null) return map;
            for (int i = 0; i < pieces.Count; i++) map[new PieceKey(pieces[i])] = pieces[i];
            return map;
        }

        private static void WritePiece(BinaryWriter writer, PieceRec piece)
        {
            writer.Write(piece.X0);
            writer.Write(piece.X1);
            writer.Write(piece.Z0);
            writer.Write(piece.Z1);
            writer.Write(piece.Y);
            writer.Write(piece.Material);
            writer.Write(piece.HasCreator);
        }

        private static PieceRec ReadPiece(BinaryReader reader)
        {
            PieceRec piece = new PieceRec();
            piece.X0 = reader.ReadSingle();
            piece.X1 = reader.ReadSingle();
            piece.Z0 = reader.ReadSingle();
            piece.Z1 = reader.ReadSingle();
            piece.Y = reader.ReadSingle();
            piece.Material = reader.ReadByte();
            piece.HasCreator = reader.ReadBoolean();
            return piece;
        }

        private static byte[] Gzip(byte[] data)
        {
            using (MemoryStream raw = new MemoryStream())
            {
                using (GZipStream zip = new GZipStream(raw, CompressionMode.Compress, true))
                {
                    zip.Write(data, 0, data.Length);
                }
                return raw.ToArray();
            }
        }

        private static byte[] Gunzip(byte[] gzip)
        {
            using (MemoryStream raw = new MemoryStream(gzip))
            using (GZipStream zip = new GZipStream(raw, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                zip.CopyTo(output);
                return output.ToArray();
            }
        }

        private struct PieceKey : IEquatable<PieceKey>
        {
            private readonly int _x0;
            private readonly int _x1;
            private readonly int _z0;
            private readonly int _z1;
            private readonly int _y;
            private readonly byte _material;
            private readonly bool _creator;

            public PieceKey(PieceRec piece)
            {
                _x0 = Bits(piece.X0);
                _x1 = Bits(piece.X1);
                _z0 = Bits(piece.Z0);
                _z1 = Bits(piece.Z1);
                _y = Bits(piece.Y);
                _material = piece.Material;
                _creator = piece.HasCreator;
            }

            public bool Equals(PieceKey other)
            {
                return _x0 == other._x0 && _x1 == other._x1 && _z0 == other._z0 && _z1 == other._z1
                    && _y == other._y && _material == other._material && _creator == other._creator;
            }

            public override bool Equals(object obj)
            {
                return obj is PieceKey && Equals((PieceKey)obj);
            }

            public override int GetHashCode()
            {
                int hash = _x0;
                hash = (hash * 397) ^ _x1;
                hash = (hash * 397) ^ _z0;
                hash = (hash * 397) ^ _z1;
                hash = (hash * 397) ^ _y;
                hash = (hash * 397) ^ _material;
                hash = (hash * 397) ^ (_creator ? 1 : 0);
                return hash;
            }

            private static int Bits(float value)
            {
                FloatBits bits = new FloatBits();
                bits.Float = value;
                return bits.Int;
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float Float;
            [FieldOffset(0)] public int Int;
        }
    }
}
