using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ServerMap.Tests
{
    public class RulesTests
    {
        [Fact]
        public void OrMergeKeepsEitherBit()
        {
            ExploredMap left = new ExploredMap(2);
            ExploredMap right = new ExploredMap(2);
            left.Set(0);
            right.Set(3);
            Assert.True(left.OrFrom(right));
            Assert.False(left.OrFrom(right));
            Assert.True(left.Get(0));
            Assert.True(left.Get(3));
            Assert.False(left.Get(1));
        }

        [Fact]
        public void ExtraIndexesAreLocalBitsTheKnownMapLacks()
        {
            ExploredMap local = new ExploredMap(4);
            ExploredMap known = new ExploredMap(4);
            local.Set(1);
            local.Set(2);
            known.Set(2);
            System.Collections.Generic.List<int> extra = local.ExtraIndexes(known, 10);
            Assert.Single(extra);
            Assert.Equal(1, extra[0]);
        }

        [Fact]
        public void GzipRoundTripAndChunksReassemble()
        {
            ExploredMap map = new ExploredMap(32);
            map.Set(0);
            map.Set(100);
            map.Set(1023);
            byte[] gzip = map.ToGzip();
            byte[][] parts = ByteChunks.Split(gzip);
            byte[] joined = ByteChunks.Join(parts);
            ExploredMap back = ExploredMap.FromGzip(32, joined);
            Assert.True(back.Get(0));
            Assert.True(back.Get(100));
            Assert.True(back.Get(1023));
            Assert.False(back.Get(1));

            byte[] wide = new byte[60000];
            wide[0] = 7;
            wide[59999] = 9;
            byte[][] split = ByteChunks.Split(wide);
            Assert.Equal(2, split.Length);
            Assert.Equal(30000, split[0].Length);
            Assert.Equal(wide, ByteChunks.Join(split));
        }

        [Fact]
        public void TombstoneBlocksJoinUploadAndAllowsALaterCreate()
        {
            MarkerBook book = new MarkerBook();
            long id;
            Assert.Equal(MarkerAccept.Created, book.TryAccept(0, "Camp", 10f, 0f, 10f, false, true, 1, out id));
            Assert.True(book.Delete(id, 2));
            long blocked;
            Assert.Equal(MarkerAccept.Tombstoned, book.TryAccept(0, "Camp", 10f, 0f, 10f, false, false, 3, out blocked));
            long created;
            Assert.Equal(MarkerAccept.Created, book.TryAccept(0, "Camp", 10f, 0f, 10f, false, true, 4, out created));
            Assert.Empty(book.Tombstones);
            Assert.Single(book.Markers);
        }

        [Fact]
        public void NearbySamePinBinds()
        {
            MarkerBook book = new MarkerBook();
            long first;
            book.TryAccept(0, "Ore", 5f, 1f, 5f, false, true, 1, out first);
            long second;
            Assert.Equal(MarkerAccept.Bound, book.TryAccept(0, "Ore", 5.5f, 1f, 5f, false, true, 2, out second));
            Assert.Equal(first, second);
            Assert.Single(book.Markers);
        }

        [Fact]
        public void SharedTypesMatchTheSpec()
        {
            Assert.True(MarkerBook.IsSharedType(0));
            Assert.True(MarkerBook.IsSharedType(1));
            Assert.True(MarkerBook.IsSharedType(2));
            Assert.True(MarkerBook.IsSharedType(3));
            Assert.True(MarkerBook.IsSharedType(6));
            Assert.True(MarkerBook.IsSharedType(9));
            Assert.True(MarkerBook.IsSharedType(14));
            Assert.True(MarkerBook.IsSharedType(15));
            Assert.True(MarkerBook.IsSharedType(16));
            Assert.True(MarkerBook.IsSharedType(17));
            Assert.False(MarkerBook.IsSharedType(4));
            Assert.False(MarkerBook.IsSharedType(5));
            Assert.False(MarkerBook.IsSharedType(7));
            Assert.False(MarkerBook.IsSharedType(10));
            Assert.False(MarkerBook.IsSharedType(12));
            Assert.False(MarkerBook.IsSharedType(13));
        }

        [Fact]
        public void NamesReplaceMarkupTrimAndRejectOver32()
        {
            Assert.Equal("hi there", MarkerBook.Normalize(" $hi<there> "));
            Assert.Equal("", MarkerBook.Normalize(null));
            Assert.Null(MarkerBook.Normalize(new string('a', 33)));
            MarkerBook book = new MarkerBook();
            long id;
            Assert.Equal(MarkerAccept.Rejected, book.TryAccept(0, new string('b', 33), 0f, 0f, 0f, false, true, 1, out id));
        }

        [Fact]
        public void Version3BlobDropsPins()
        {
            bool[] explored = new bool[] { true, false, true };
            byte[] blob = TableBlob.WriteVersion3(explored, new byte[] { 9, 9, 9 });
            TableEdit edit;
            byte[] stripped = TableBlob.StripPins(blob, out edit);
            Assert.Equal(TableEdit.Rewritten, edit);
            Assert.Equal(8 + 3 + 4, stripped.Length);
            Assert.Equal(1, stripped[8]);
            Assert.Equal(0, stripped[9]);
            Assert.Equal(1, stripped[10]);
            Assert.Equal(0, stripped[stripped.Length - 1]);
        }

        [Fact]
        public void OtherTableVersionsStayUntouched()
        {
            byte[] blob = new byte[] { 2, 0, 0, 0, 0, 0, 0, 0 };
            TableEdit edit;
            Assert.Null(TableBlob.StripPins(blob, out edit));
            Assert.Equal(TableEdit.NotVersion3, edit);
        }

        [Fact]
        public void PaintPriorityAndFootprint()
        {
            Assert.Equal(OverlayRules.KindPaved, OverlayRules.ClassifyPaint(1f, 1f, 1f, 1f, true));
            Assert.Equal(OverlayRules.KindDirt, OverlayRules.ClassifyPaint(1f, 1f, 0f, 1f, true));
            Assert.Equal(OverlayRules.KindCultivated, OverlayRules.ClassifyPaint(0f, 1f, 0f, 1f, true));
            Assert.Equal(OverlayRules.KindLevelled, OverlayRules.ClassifyPaint(0f, 0f, 0f, 1f, true));
            Assert.Equal(OverlayRules.KindLevelled, OverlayRules.ClassifyPaint(0f, 0f, 0f, 0f, false));
            Assert.Equal(OverlayRules.KindNone, OverlayRules.ClassifyPaint(0f, 0f, 0f, 1f, false));
            Assert.True(OverlayRules.FootprintOk(32f, 32f));
            Assert.False(OverlayRules.FootprintOk(32.1f, 1f));
        }

        [Fact]
        public void ForestWindowRespectsCapAndGeneratedZones()
        {
            Assert.True(OverlayRules.IsCleared(true, 2, 2, true));
            Assert.False(OverlayRules.IsCleared(true, 3, 2, true));
            Assert.False(OverlayRules.IsCleared(true, 0, 2, false));
            bool generated(int x, int z) { return x == 0 && z == 0; }
            Assert.True(OverlayRules.WindowGenerated(8, 8, 16, 12f, 12f, generated));
            Assert.False(OverlayRules.WindowGenerated(0, 0, 16, 12f, 12f, generated));
        }

        [Fact]
        public void PlantedForestSkipsVanillaForestAndSwamp()
        {
            Assert.True(OverlayRules.IsPlanted(false, 6, 6, OverlayRules.Meadows, false));
            Assert.False(OverlayRules.IsPlanted(true, 20, 6, OverlayRules.Meadows, false));
            Assert.False(OverlayRules.IsPlanted(false, 6, 6, OverlayRules.Swamp, false));
            Assert.True(OverlayRules.IsPlanted(false, 6, 6, OverlayRules.Swamp, true));
        }

        [Fact]
        public void OnlyPlayerBuiltHidesUnownedPieces()
        {
            Assert.False(OverlayRules.ShowPiece(true, false));
            Assert.True(OverlayRules.ShowPiece(true, true));
            Assert.True(OverlayRules.ShowPiece(false, false));
        }

        [Fact]
        public void FilesRoundTripAndRejectABadMagic()
        {
            string dir = Path.Combine(Path.GetTempPath(), "servermap-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                ExploredMap map = new ExploredMap(4);
                map.Set(2);
                string exploredPath = Path.Combine(dir, "1.explored.bin");
                MapFiles.SaveExplored(exploredPath, map);
                ExploredMap loaded = MapFiles.LoadExplored(exploredPath);
                Assert.True(loaded.Get(2));
                Assert.False(loaded.Get(0));

                MarkerBook book = new MarkerBook();
                long id;
                book.TryAccept(9, "Eikthyr", 1f, 2f, 3f, true, true, 9, out id);
                book.Delete(id, 10);
                string markerPath = Path.Combine(dir, "1.markers.bin");
                MapFiles.SaveMarkers(markerPath, book);
                MarkerBook markers = MapFiles.LoadMarkers(markerPath);
                Assert.Empty(markers.Markers);
                Assert.Single(markers.Tombstones);
                Assert.Equal("Eikthyr", markers.Tombstones[0].Name);

                PieceRec piece = new PieceRec { X0 = 1f, X1 = 2f, Z0 = 3f, Z1 = 4f, Y = 5f, Material = 0, HasCreator = true };
                PathCell cell = new PathCell { X = 6, Z = 7, Kind = OverlayRules.KindDirt };
                ZoneRec zone = new ZoneRec { X = -2, Y = 4 };
                string overlayPath = Path.Combine(dir, "1.overlay.bin");
                MapFiles.SaveOverlay(overlayPath, 4, new[] { piece }, new[] { cell }, new byte[] { 0, 2, 0, 0 }, new[] { zone });
                int size;
                PieceRec[] pieces;
                PathCell[] cells;
                byte[] trees;
                ZoneRec[] zones;
                Assert.True(MapFiles.LoadOverlay(overlayPath, out size, out pieces, out cells, out trees, out zones));
                Assert.Equal(4, size);
                Assert.True(pieces[0].HasCreator);
                Assert.Equal(OverlayRules.KindDirt, cells[0].Kind);
                Assert.Equal(2, trees[1]);
                Assert.Equal(-2, zones[0].X);

                File.WriteAllBytes(exploredPath, new byte[] { 1, 2, 3, 4, 1, 0, 0, 0 });
                Assert.Null(MapFiles.LoadExplored(exploredPath));
                Assert.False(File.Exists(exploredPath));
                Assert.NotEmpty(Directory.GetFiles(dir, "*.rejected-*"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void OverlayDeltaCancelsAndRoundTrips()
        {
            PieceRec piece = new PieceRec { X0 = 1f, X1 = 3f, Z0 = 4f, Z1 = 6f, Y = 2f, Material = 1, HasCreator = true };
            OverlayDelta cancelled = new OverlayDelta();
            cancelled.DiffPieces(new PieceRec[0], new[] { piece });
            cancelled.DiffPieces(new[] { piece }, new PieceRec[0]);
            Assert.True(cancelled.IsEmpty);

            OverlayDelta delta = new OverlayDelta();
            delta.DiffPieces(new PieceRec[0], new[] { piece });
            delta.SetCell(8, 9, OverlayRules.KindPaved);
            delta.SetCell(8, 9, OverlayRules.KindDirt);
            delta.DiffTrees(new byte[] { 1, 0 }, new byte[] { 1, 4 });
            HashSet<long> zones = new HashSet<long>();
            zones.Add(OverlayRules.PackKey(2, 3));
            Assert.True(delta.DiffZones(new HashSet<long>(), zones));
            Assert.False(delta.CanPatchPaint);
            Assert.True(delta.NeedsFullPaint);

            OverlayDelta addedOnly = new OverlayDelta();
            addedOnly.DiffPieces(new PieceRec[0], new[] { piece });
            Assert.True(addedOnly.CanPatchPaint);
            Assert.False(addedOnly.NeedsFullPaint);
            List<PieceRec> patch = new List<PieceRec>();
            addedOnly.CopyAddedPieces(patch);
            Assert.Single(patch);

            OverlayDelta read;
            Assert.True(OverlayDelta.TryRead(delta.ToBytes(), out read));
            List<PieceRec> pieces = new List<PieceRec>();
            Dictionary<long, byte> cells = new Dictionary<long, byte>();
            byte[] trees = new byte[0];
            HashSet<long> applied = new HashSet<long>();
            read.Apply(pieces, cells, ref trees, applied);
            Assert.Single(pieces);
            Assert.True(pieces[0].HasCreator);
            Assert.Equal(OverlayRules.KindDirt, cells[OverlayRules.PackKey(8, 9)]);
            Assert.Equal(4, trees[1]);
            Assert.Contains(OverlayRules.PackKey(2, 3), applied);

            read.Apply(pieces, cells, ref trees, applied);
            OverlayDelta removal = new OverlayDelta();
            removal.DiffPieces(pieces, new PieceRec[0]);
            removal.SetCell(8, 9, OverlayRules.KindNone);
            OverlayDelta removalRead;
            Assert.True(OverlayDelta.TryRead(removal.ToBytes(), out removalRead));
            Assert.False(removalRead.CanPatchPaint);
            Assert.True(removalRead.NeedsFullPaint);
            removalRead.Apply(pieces, cells, ref trees, applied);
            Assert.Empty(pieces);
            Assert.False(cells.ContainsKey(OverlayRules.PackKey(8, 9)));
        }

        [Fact]
        public void ChunksIgnoreDuplicatesAndExpirePerSender()
        {
            ChunkBox box = new ChunkBox();
            Assert.Null(box.Add(1L, 7, 0, 2, new byte[] { 1 }, 0f));
            Assert.Null(box.Add(1L, 7, 0, 2, new byte[] { 9 }, 1f));
            Assert.Equal(new byte[] { 1, 2 }, box.Add(1L, 7, 1, 2, new byte[] { 2 }, 1f));
            Assert.Null(box.Add(2L, 7, 0, 2, new byte[] { 5 }, 1f));
            Assert.Equal(0, box.DropExpired(10f));
            Assert.Equal(1, box.DropExpired(31f));
            Assert.Null(box.Add(2L, 7, 1, 2, new byte[] { 6 }, 32f));
        }
    }
}
