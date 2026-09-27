using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ServerMap
{
    public static class MapFiles
    {
        public static readonly byte[] ExploredMagic = Encoding.ASCII.GetBytes("SMEX");
        public static readonly byte[] MarkerMagic = Encoding.ASCII.GetBytes("SMMK");
        public static readonly byte[] OverlayMagic = Encoding.ASCII.GetBytes("SMOY");
        public const int Version = 1;

        public static string FileName(long worldUid)
        {
            string text = worldUid.ToString();
            char[] bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < bad.Length; i++) text = text.Replace(bad[i], '_');
            return text;
        }

        public static void SaveExplored(string path, ExploredMap map)
        {
            using (BinaryWriter writer = Open(path))
            {
                writer.Write(ExploredMagic);
                writer.Write(Version);
                writer.Write(map.TextureSize);
                writer.Write(map.BitCount);
                byte[] gzip = map.ToGzip();
                writer.Write(gzip.Length);
                writer.Write(gzip);
            }
        }

        public static ExploredMap LoadExplored(string path)
        {
            using (BinaryReader reader = ReadChecked(path, ExploredMagic))
            {
                if (reader == null) return null;
                int size = reader.ReadInt32();
                int bits = reader.ReadInt32();
                if (size < 1 || bits != size * size) throw new InvalidDataException("explored size");
                int gzipLength = reader.ReadInt32();
                byte[] gzip = reader.ReadBytes(gzipLength);
                ExploredMap map = ExploredMap.FromGzip(size, gzip);
                return map;
            }
        }

        public static void SaveMarkers(string path, MarkerBook book)
        {
            using (BinaryWriter writer = Open(path))
            {
                writer.Write(MarkerMagic);
                writer.Write(Version);
                writer.Write(book.Markers.Count);
                for (int i = 0; i < book.Markers.Count; i++)
                {
                    MarkerRecord marker = book.Markers[i];
                    writer.Write(marker.Id);
                    writer.Write(marker.Type);
                    writer.Write(marker.Name ?? "");
                    writer.Write(marker.X);
                    writer.Write(marker.Y);
                    writer.Write(marker.Z);
                    writer.Write(marker.Checked);
                    writer.Write(marker.Revision);
                }
                writer.Write(book.Tombstones.Count);
                for (int i = 0; i < book.Tombstones.Count; i++)
                {
                    MarkerTombstone tomb = book.Tombstones[i];
                    writer.Write(tomb.X);
                    writer.Write(tomb.Y);
                    writer.Write(tomb.Z);
                    writer.Write(tomb.Type);
                    writer.Write(tomb.Name ?? "");
                    writer.Write(tomb.Revision);
                }
            }
        }

        public static MarkerBook LoadMarkers(string path)
        {
            using (BinaryReader reader = ReadChecked(path, MarkerMagic))
            {
                if (reader == null) return null;
                MarkerBook book = new MarkerBook();
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    MarkerRecord marker = new MarkerRecord();
                    marker.Id = reader.ReadInt64();
                    marker.Type = reader.ReadInt32();
                    marker.Name = reader.ReadString();
                    marker.X = reader.ReadSingle();
                    marker.Y = reader.ReadSingle();
                    marker.Z = reader.ReadSingle();
                    marker.Checked = reader.ReadBoolean();
                    marker.Revision = reader.ReadInt64();
                    book.Restore(marker, null);
                }
                int tombs = reader.ReadInt32();
                MarkerTombstone[] list = new MarkerTombstone[tombs];
                for (int i = 0; i < tombs; i++)
                {
                    MarkerTombstone tomb = new MarkerTombstone();
                    tomb.X = reader.ReadSingle();
                    tomb.Y = reader.ReadSingle();
                    tomb.Z = reader.ReadSingle();
                    tomb.Type = reader.ReadInt32();
                    tomb.Name = reader.ReadString();
                    tomb.Revision = reader.ReadInt64();
                    list[i] = tomb;
                }
                book.Restore(null, list);
                return book;
            }
        }

        public static byte[] ExportOverlay(int textureSize, PieceRec[] pieces, PathCell[] cells, byte[] treeCounts, ZoneRec[] zones)
        {
            string temp = Path.Combine(Path.GetTempPath(), "servermap-overlay-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                SaveOverlay(temp, textureSize, pieces, cells, treeCounts, zones);
                return File.ReadAllBytes(temp);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        public static bool ImportOverlay(byte[] data, out int textureSize, out PieceRec[] pieces, out PathCell[] cells, out byte[] treeCounts, out ZoneRec[] zones)
        {
            string temp = Path.Combine(Path.GetTempPath(), "servermap-overlay-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                File.WriteAllBytes(temp, data);
                return LoadOverlay(temp, out textureSize, out pieces, out cells, out treeCounts, out zones);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        public static void SaveOverlay(string path, int textureSize, PieceRec[] pieces, PathCell[] cells, byte[] treeCounts, ZoneRec[] zones)
        {
            using (BinaryWriter writer = Open(path))
            {
                writer.Write(OverlayMagic);
                writer.Write(Version);
                writer.Write(textureSize);
                int pieceCount = pieces == null ? 0 : pieces.Length;
                writer.Write(pieceCount);
                for (int i = 0; i < pieceCount; i++)
                {
                    PieceRec piece = pieces[i];
                    writer.Write(piece.X0);
                    writer.Write(piece.X1);
                    writer.Write(piece.Z0);
                    writer.Write(piece.Z1);
                    writer.Write(piece.Y);
                    writer.Write(piece.Material);
                    writer.Write(piece.HasCreator);
                }
                int cellCount = cells == null ? 0 : cells.Length;
                writer.Write(cellCount);
                for (int i = 0; i < cellCount; i++)
                {
                    writer.Write(cells[i].X);
                    writer.Write(cells[i].Z);
                    writer.Write(cells[i].Kind);
                }
                byte[] counts = treeCounts ?? new byte[0];
                byte[] gzip = Gzip(counts);
                writer.Write(gzip.Length);
                writer.Write(gzip);
                int zoneCount = zones == null ? 0 : zones.Length;
                writer.Write(zoneCount);
                for (int i = 0; i < zoneCount; i++)
                {
                    writer.Write(zones[i].X);
                    writer.Write(zones[i].Y);
                }
            }
        }

        public static bool LoadOverlay(string path, out int textureSize, out PieceRec[] pieces, out PathCell[] cells, out byte[] treeCounts, out ZoneRec[] zones)
        {
            textureSize = 0;
            pieces = new PieceRec[0];
            cells = new PathCell[0];
            treeCounts = new byte[0];
            zones = new ZoneRec[0];
            using (BinaryReader reader = ReadChecked(path, OverlayMagic))
            {
                if (reader == null) return false;
                textureSize = reader.ReadInt32();
                int pieceCount = reader.ReadInt32();
                pieces = new PieceRec[pieceCount];
                for (int i = 0; i < pieceCount; i++)
                {
                    PieceRec piece = new PieceRec();
                    piece.X0 = reader.ReadSingle();
                    piece.X1 = reader.ReadSingle();
                    piece.Z0 = reader.ReadSingle();
                    piece.Z1 = reader.ReadSingle();
                    piece.Y = reader.ReadSingle();
                    piece.Material = reader.ReadByte();
                    piece.HasCreator = reader.ReadBoolean();
                    pieces[i] = piece;
                }
                int cellCount = reader.ReadInt32();
                cells = new PathCell[cellCount];
                for (int i = 0; i < cellCount; i++)
                {
                    PathCell cell = new PathCell();
                    cell.X = reader.ReadInt32();
                    cell.Z = reader.ReadInt32();
                    cell.Kind = reader.ReadByte();
                    cells[i] = cell;
                }
                int gzipLength = reader.ReadInt32();
                treeCounts = Gunzip(reader.ReadBytes(gzipLength));
                int zoneCount = reader.ReadInt32();
                zones = new ZoneRec[zoneCount];
                for (int i = 0; i < zoneCount; i++)
                {
                    ZoneRec zone = new ZoneRec();
                    zone.X = reader.ReadInt32();
                    zone.Y = reader.ReadInt32();
                    zones[i] = zone;
                }
                return true;
            }
        }

        private static BinaryWriter Open(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            return new BinaryWriter(File.Create(path));
        }

        private static BinaryReader ReadChecked(string path, byte[] magic)
        {
            if (!File.Exists(path)) return null;
            byte[] header = new byte[8];
            using (FileStream stream = File.OpenRead(path))
            {
                if (stream.Read(header, 0, 8) != 8) { Reject(path); return null; }
            }
            for (int i = 0; i < 4; i++)
            {
                if (header[i] != magic[i]) { Reject(path); return null; }
            }
            int version = header[4] | (header[5] << 8) | (header[6] << 16) | (header[7] << 24);
            if (version != Version) { Reject(path); return null; }
            FileStream body = File.OpenRead(path);
            body.Position = 8;
            return new BinaryReader(body);
        }

        private static void Reject(string path)
        {
            string rejected = path + ".rejected-" + DateTime.UtcNow.Ticks;
            if (File.Exists(path)) File.Move(path, rejected);
        }

        private static byte[] Gzip(byte[] data)
        {
            using (MemoryStream raw = new MemoryStream())
            {
                using (System.IO.Compression.GZipStream zip = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Compress, true))
                {
                    zip.Write(data, 0, data.Length);
                }
                return raw.ToArray();
            }
        }

        private static byte[] Gunzip(byte[] gzip)
        {
            using (MemoryStream raw = new MemoryStream(gzip))
            using (System.IO.Compression.GZipStream zip = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                zip.CopyTo(output);
                return output.ToArray();
            }
        }
    }

    public struct PieceRec
    {
        public float X0;
        public float X1;
        public float Z0;
        public float Z1;
        public float Y;
        public byte Material;
        public bool HasCreator;
    }

    public struct PathCell
    {
        public int X;
        public int Z;
        public byte Kind;
    }

    public struct ZoneRec
    {
        public int X;
        public int Y;
    }
}
