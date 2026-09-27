using System;

namespace ServerMap
{
    public enum TableEdit
    {
        Rewritten,
        NotVersion3,
        Bad
    }

    public static class TableBlob
    {
        public static byte[] StripPins(byte[] data, out TableEdit edit)
        {
            edit = TableEdit.Bad;
            if (data == null || data.Length < 8) return null;
            int version = ReadInt(data, 0);
            if (version != 3)
            {
                edit = TableEdit.NotVersion3;
                return null;
            }
            int length = ReadInt(data, 4);
            if (length < 0) return null;
            long boolEnd = 8L + length;
            if (boolEnd > data.Length || data.Length < boolEnd + 4) return null;
            byte[] stripped = new byte[boolEnd + 4];
            Buffer.BlockCopy(data, 0, stripped, 0, (int)boolEnd);
            edit = TableEdit.Rewritten;
            return stripped;
        }

        public static byte[] WriteVersion3(bool[] explored, byte[] pinTail)
        {
            int length = explored == null ? 0 : explored.Length;
            int tail = pinTail == null ? 0 : pinTail.Length;
            byte[] data = new byte[8 + length + 4 + tail];
            WriteInt(data, 0, 3);
            WriteInt(data, 4, length);
            if (length > 0)
            {
                for (int i = 0; i < length; i++) data[8 + i] = explored[i] ? (byte)1 : (byte)0;
            }
            WriteInt(data, 8 + length, tail > 0 ? 1 : 0);
            if (tail > 0) Buffer.BlockCopy(pinTail, 0, data, 12 + length, tail);
            return data;
        }

        private static int ReadInt(byte[] data, int offset)
        {
            return data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24);
        }

        private static void WriteInt(byte[] data, int offset, int value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }
    }
}
