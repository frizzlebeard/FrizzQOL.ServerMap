using System;

namespace ServerMap
{
    public static class OverlayRules
    {
        public const float MaxFootprint = 32f;
        public const byte KindNone = 0;
        public const byte KindDirt = 1;
        public const byte KindCultivated = 2;
        public const byte KindPaved = 3;
        public const byte KindLevelled = 4;
        public const byte MaterialUnknown = 255;

        public const int Meadows = 1;
        public const int Swamp = 2;
        public const int BlackForest = 8;
        public const int Plains = 16;

        public static byte ClassifyPaint(float r, float g, float b, float a, bool heightModified)
        {
            if (b > 0.5f) return KindPaved;
            if (r > 0.5f) return KindDirt;
            if (g > 0.5f) return KindCultivated;
            if (heightModified || a < 0.5f) return KindLevelled;
            return KindNone;
        }

        public static bool FootprintOk(float widthX, float widthZ)
        {
            return widthX <= MaxFootprint && widthZ <= MaxFootprint && widthX >= 0f && widthZ >= 0f;
        }

        public static bool ShowPiece(bool onlyPlayerBuilt, bool hasCreator)
        {
            if (onlyPlayerBuilt && !hasCreator) return false;
            return true;
        }

        public static bool IsCleared(bool maskForest, int treesInWindow, int maxTrees, bool windowGenerated)
        {
            return maskForest && windowGenerated && treesInWindow <= maxTrees;
        }

        public static bool IsPlanted(bool maskForest, int treesOnPixel, int minTrees, int biome, bool anyBiome)
        {
            if (maskForest) return false;
            if (treesOnPixel < minTrees) return false;
            if (anyBiome) return true;
            return biome == Meadows || biome == BlackForest || biome == Plains;
        }

        public static int ZoneCoord(float world)
        {
            return (int)Math.Floor((world + 32.0) / 64.0);
        }

        public static bool WindowGenerated(int pixelX, int pixelY, int textureSize, float pixelSize, float radiusMeters, Func<int, int, bool> generated)
        {
            if (generated == null) return false;
            float half = textureSize / 2f;
            float worldX = (pixelX - half) * pixelSize;
            float worldZ = (pixelY - half) * pixelSize;
            int x0 = ZoneCoord(worldX - radiusMeters);
            int x1 = ZoneCoord(worldX + radiusMeters);
            int z0 = ZoneCoord(worldZ - radiusMeters);
            int z1 = ZoneCoord(worldZ + radiusMeters);
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    if (!generated(x, z)) return false;
                }
            }
            return true;
        }

        public static long PackKey(int x, int y)
        {
            return ((long)x << 32) | (uint)y;
        }

        public static int KeyX(long key)
        {
            return (int)(key >> 32);
        }

        public static int KeyY(long key)
        {
            return (int)key;
        }
    }
}
