using System.Collections.Generic;

namespace ServerMap
{
    public sealed class ChunkBox
    {
        public const float TimeoutSeconds = 30f;
        private const int MaxCount = 10000;

        private readonly Dictionary<long, Dictionary<int, Slot>> _bySender = new Dictionary<long, Dictionary<int, Slot>>();

        public byte[] Add(long sender, int stream, int index, int count, byte[] part, float now)
        {
            if (part == null || count <= 0 || count > MaxCount || index < 0 || index >= count) return null;
            Dictionary<int, Slot> streams;
            if (!_bySender.TryGetValue(sender, out streams))
            {
                streams = new Dictionary<int, Slot>();
                _bySender[sender] = streams;
            }
            Slot slot;
            if (!streams.TryGetValue(stream, out slot))
            {
                slot = new Slot();
                slot.Parts = new byte[count][];
                slot.Count = count;
                slot.Seen = now;
                streams[stream] = slot;
            }
            else if (slot.Count != count)
            {
                streams.Remove(stream);
                return null;
            }
            if (slot.Parts[index] != null) return null;
            slot.Parts[index] = part;
            slot.Got++;
            slot.Seen = now;
            if (slot.Got < slot.Count) return null;
            streams.Remove(stream);
            if (streams.Count == 0) _bySender.Remove(sender);
            return ByteChunks.Join(slot.Parts);
        }

        public int DropExpired(float now)
        {
            int dropped = 0;
            List<long> emptySenders = null;
            foreach (KeyValuePair<long, Dictionary<int, Slot>> sender in _bySender)
            {
                List<int> dead = null;
                foreach (KeyValuePair<int, Slot> stream in sender.Value)
                {
                    if (now - stream.Value.Seen < TimeoutSeconds) continue;
                    if (dead == null) dead = new List<int>();
                    dead.Add(stream.Key);
                }
                if (dead == null) continue;
                for (int i = 0; i < dead.Count; i++) sender.Value.Remove(dead[i]);
                dropped += dead.Count;
                if (sender.Value.Count == 0)
                {
                    if (emptySenders == null) emptySenders = new List<long>();
                    emptySenders.Add(sender.Key);
                }
            }
            if (emptySenders != null)
            {
                for (int i = 0; i < emptySenders.Count; i++) _bySender.Remove(emptySenders[i]);
            }
            return dropped;
        }

        public void Clear()
        {
            _bySender.Clear();
        }

        private sealed class Slot
        {
            public byte[][] Parts;
            public int Count;
            public int Got;
            public float Seen;
        }
    }
}
