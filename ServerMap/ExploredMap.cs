using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace ServerMap
{
    public sealed class ExploredMap
    {
        public int TextureSize { get; }
        public int BitCount { get { return TextureSize * TextureSize; } }

        private readonly byte[] _packed;

        public ExploredMap(int textureSize)
        {
            if (textureSize < 1) throw new ArgumentOutOfRangeException("textureSize");
            TextureSize = textureSize;
            _packed = new byte[(BitCount + 7) / 8];
        }

        public bool Get(int index)
        {
            if (index < 0 || index >= BitCount) return false;
            return (_packed[index >> 3] & (1 << (index & 7))) != 0;
        }

        public void Set(int index)
        {
            if (index < 0 || index >= BitCount) return;
            _packed[index >> 3] |= (byte)(1 << (index & 7));
        }

        public bool OrFrom(ExploredMap other)
        {
            if (other == null || other.BitCount != BitCount) return false;
            int n = Math.Min(_packed.Length, other._packed.Length);
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                byte merged = (byte)(_packed[i] | other._packed[i]);
                if (merged != _packed[i])
                {
                    _packed[i] = merged;
                    changed = true;
                }
            }
            return changed;
        }

        public List<int> ExtraIndexes(ExploredMap known, int max)
        {
            List<int> found = new List<int>();
            if (max <= 0) return found;
            for (int i = 0; i < BitCount && found.Count < max; i++)
            {
                if (Get(i) && (known == null || !known.Get(i))) found.Add(i);
            }
            return found;
        }

        public byte[] ToGzip()
        {
            using (MemoryStream raw = new MemoryStream())
            {
                using (GZipStream zip = new GZipStream(raw, CompressionMode.Compress, true))
                {
                    zip.Write(_packed, 0, _packed.Length);
                }
                return raw.ToArray();
            }
        }

        public static ExploredMap FromGzip(int textureSize, byte[] gzip)
        {
            ExploredMap map = new ExploredMap(textureSize);
            using (MemoryStream raw = new MemoryStream(gzip))
            using (GZipStream zip = new GZipStream(raw, CompressionMode.Decompress))
            {
                int offset = 0;
                while (offset < map._packed.Length)
                {
                    int read = zip.Read(map._packed, offset, map._packed.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }
            }
            return map;
        }
    }
}
