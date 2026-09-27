using System;

namespace ServerMap
{
    public static class ByteChunks
    {
        public const int MaxBytes = 30000;

        public static byte[][] Split(byte[] data)
        {
            if (data == null) data = new byte[0];
            if (data.Length == 0) return new byte[][] { new byte[0] };
            int count = (data.Length + MaxBytes - 1) / MaxBytes;
            byte[][] parts = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                int start = i * MaxBytes;
                int len = Math.Min(MaxBytes, data.Length - start);
                byte[] part = new byte[len];
                Buffer.BlockCopy(data, start, part, 0, len);
                parts[i] = part;
            }
            return parts;
        }

        public static byte[] Join(byte[][] parts)
        {
            if (parts == null || parts.Length == 0) return new byte[0];
            int total = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] != null) total += parts[i].Length;
            }
            byte[] all = new byte[total];
            int offset = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null || parts[i].Length == 0) continue;
                Buffer.BlockCopy(parts[i], 0, all, offset, parts[i].Length);
                offset += parts[i].Length;
            }
            return all;
        }
    }
}
