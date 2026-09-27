using HarmonyLib;

namespace ServerMap
{
    [HarmonyPatch(typeof(Minimap), "Explore", new System.Type[] { typeof(int), typeof(int) })]
    internal static class ExploreShare
    {
        static void Postfix(int x, int y, bool __result)
        {
            if (!__result) return;
            Minimap map = Minimap.instance;
            if (map == null || map.m_textureSize <= 0) return;
            if (x < 0 || y < 0 || x >= map.m_textureSize || y >= map.m_textureSize) return;
            MapPainter.NoteExplored((y * map.m_textureSize) + x);
        }
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.GetSharedMapData))]
    internal static class TablePinGuardWrite
    {
        static void Postfix(ref byte[] __result)
        {
            __result = TablePinGuard.Rewrite(__result);
        }
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.AddSharedMapData))]
    internal static class TablePinGuardRead
    {
        static void Prefix(ref byte[] dataArray)
        {
            dataArray = TablePinGuard.Rewrite(dataArray);
        }
    }

    internal static class TablePinGuard
    {
        private static bool _loggedVersion;
        private static bool _loggedBad;

        internal static byte[] Rewrite(byte[] data)
        {
            if (data == null) return data;
            TableEdit edit;
            byte[] stripped = TableBlob.StripPins(data, out edit);
            if (edit == TableEdit.Rewritten) return stripped;
            if (edit == TableEdit.NotVersion3 && !_loggedVersion)
            {
                _loggedVersion = true;
                Plugin.LogWarning("Cartography table data is not version 3. Pins were left unchanged.");
            }
            else if (edit == TableEdit.Bad && !_loggedBad)
            {
                _loggedBad = true;
                Plugin.LogWarning("Cartography table data could not be parsed. Pins were left unchanged.");
            }
            return data;
        }
    }
}
