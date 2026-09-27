using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ServerMap
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.frizzqol.servermap";
        public const string PluginName = "FrizzQOL Server Map";
        public const string PluginVersion = "0.1.0";

        internal static Plugin Instance { get; private set; }
        internal static ConfigEntry<bool> Enabled;

        internal static ConfigEntry<bool> DrawFootprints;
        internal static ConfigEntry<bool> ShowBuildings;
        internal static ConfigEntry<bool> OnlyPlayerBuilt;
        internal static ConfigEntry<bool> ShowPaths;
        internal static ConfigEntry<bool> ShowClearedGround;
        internal static ConfigEntry<bool> ShowClearedForest;
        internal static ConfigEntry<float> ClearedForestRadius;
        internal static ConfigEntry<int> ClearedForestMaxTrees;
        internal static ConfigEntry<bool> ShowPlantedForest;
        internal static ConfigEntry<int> PlantedForestMinTrees;
        internal static ConfigEntry<bool> PlantedForestAnyBiome;
        internal static ConfigEntry<bool> RespectFog;
        internal static ConfigEntry<int> MapTextureScale;
        internal static ConfigEntry<float> MinPieceSizeMeters;
        internal static ConfigEntry<bool> BuildingOutline;
        internal static ConfigEntry<float> OutlineWidthMeters;
        internal static ConfigEntry<Color> OutlineColor;
        internal static ConfigEntry<Color>[] MaterialColors;
        internal static ConfigEntry<Color> UnknownColor;
        internal static ConfigEntry<Color> DirtColor;
        internal static ConfigEntry<Color> PavedColor;
        internal static ConfigEntry<Color> CultivatedColor;
        internal static ConfigEntry<Color> ClearedColor;

        private Harmony _harmony;
        private readonly MapSession _session = new MapSession();

        private void Awake()
        {
            Instance = this;
            Bind();
            _harmony = new Harmony(PluginGuid);
            try
            {
                _harmony.PatchAll();
                Logger.LogInfo(PluginName + " " + PluginVersion + " loaded");
                if (!FootprintsOn())
                    Logger.LogInfo("Building and path overlay is off. Shared exploration and markers stay on.");
            }
            catch (System.Exception ex)
            {
                Logger.LogError("Harmony patch failed. The cartography table can still copy pins. " + ex.Message);
            }
        }

        private string _tickError;

        private void Update()
        {
            if (Enabled != null && !Enabled.Value) return;
            try
            {
                _session.Tick();
            }
            catch (System.Exception ex)
            {
                if (_tickError == ex.Message) return;
                _tickError = ex.Message;
                Logger.LogError("Server Map hit an error and skipped this frame. " + ex);
            }
        }

        private void OnDestroy()
        {
            _session.SaveNow();
        }

        internal static void LogWarning(string message)
        {
            if (Instance != null) Instance.Logger.LogWarning(message);
        }

        internal static void LogInfo(string message)
        {
            if (Instance != null) Instance.Logger.LogInfo(message);
        }

        private void Bind()
        {
            Enabled = Config.Bind("General", "Enabled", true, "When false, this process does not send or apply the shared map.");
            DrawFootprints = Config.Bind("Layers", "DrawFootprints", false, "When false, buildings and paths are not scanned or drawn. Shared exploration and markers stay on.");
            ShowBuildings = Config.Bind("Layers", "ShowBuildings", true, "Draw build footprints. Used only while DrawFootprints is true.");
            OnlyPlayerBuilt = Config.Bind("Layers", "OnlyPlayerBuilt", false, "Hide pieces that have no creator.");
            ShowPaths = Config.Bind("Layers", "ShowPaths", true, "Draw dirt, paving, and cultivated soil.");
            ShowClearedGround = Config.Bind("Layers", "ShowClearedGround", false, "Also draw levelled ground with no paint.");
            ShowClearedForest = Config.Bind("Layers", "ShowClearedForest", false, "Remove the vanilla forest pattern where the trees are gone.");
            ClearedForestRadius = Config.Bind("Layers", "ClearedForestRadius", 12f, "Metres checked around a map pixel.");
            ClearedForestMaxTrees = Config.Bind("Layers", "ClearedForestMaxTrees", 2, "Trees still allowed in that window.");
            ShowPlantedForest = Config.Bind("Layers", "ShowPlantedForest", false, "Add forest where enough trees stand outside the mask.");
            PlantedForestMinTrees = Config.Bind("Layers", "PlantedForestMinTrees", 6, "Trees one vanilla map pixel must hold.");
            PlantedForestAnyBiome = Config.Bind("Layers", "PlantedForestAnyBiome", false, "Also plant forest outside Meadows, Black Forest, and Plains.");
            RespectFog = Config.Bind("Layers", "RespectFog", true, "Draw the overlay only on explored pixels.");
            MapTextureScale = Config.Bind("Rendering", "MapTextureScale", 1, "The overlay uses the vanilla map size. Larger values are ignored because the corner map stays on screen.");
            MinPieceSizeMeters = Config.Bind("Rendering", "MinPieceSizeMeters", 2f, "Smallest footprint drawn, in metres.");
            BuildingOutline = Config.Bind("Rendering", "BuildingOutline", true, "Dark halo behind pieces.");
            OutlineWidthMeters = Config.Bind("Rendering", "OutlineWidthMeters", 1f, "How far the halo extends.");
            OutlineColor = Config.Bind("Colors", "Building_Outline", new Color(0.10f, 0.07f, 0.04f, 0.85f), "Halo colour.");
            string[] names = { "Wood", "Stone", "Iron", "HardWood", "Marble", "Ashstone", "Ancient", "Ice", "Timberwood" };
            Color[] defaults =
            {
                new Color(0.95f, 0.55f, 0.15f, 1f),
                new Color(0.72f, 0.74f, 0.80f, 1f),
                new Color(0.40f, 0.44f, 0.52f, 1f),
                new Color(0.55f, 0.34f, 0.16f, 1f),
                new Color(0.90f, 0.87f, 0.80f, 1f),
                new Color(0.45f, 0.20f, 0.20f, 1f),
                new Color(0.36f, 0.52f, 0.36f, 1f),
                new Color(0.65f, 0.85f, 0.95f, 1f),
                new Color(0.70f, 0.45f, 0.30f, 1f)
            };
            MaterialColors = new ConfigEntry<Color>[names.Length];
            for (int i = 0; i < names.Length; i++)
                MaterialColors[i] = Config.Bind("Colors", "Material_" + names[i], defaults[i], "Colour for " + names[i] + " pieces.");
            UnknownColor = Config.Bind("Colors", "Material_Unknown", new Color(0.85f, 0.30f, 0.75f, 1f), "Colour for pieces with no material.");
            DirtColor = Config.Bind("Colors", "Terrain_DirtPath", new Color(0.42f, 0.36f, 0.30f, 0.8f), "Dirt paths.");
            PavedColor = Config.Bind("Colors", "Terrain_Paved", new Color(0.62f, 0.62f, 0.66f, 0.9f), "Paving.");
            CultivatedColor = Config.Bind("Colors", "Terrain_Cultivated", new Color(0.36f, 0.26f, 0.12f, 0.85f), "Cultivated soil.");
            ClearedColor = Config.Bind("Colors", "Terrain_Cleared", new Color(0.50f, 0.47f, 0.36f, 0.5f), "Levelled ground.");
        }

        internal static bool FootprintsOn()
        {
            return DrawFootprints != null && DrawFootprints.Value;
        }
    }

    internal sealed class MapSession
    {
        internal const float SaveInterval = 60f;
        internal const float ExploreInterval = 1f;

        internal ExploredMap Explored;
        internal ExploredMap Known;
        internal MarkerBook Markers = new MarkerBook();
        internal readonly List<PieceRec> Pieces = new List<PieceRec>();
        internal readonly List<PieceRec> PatchPieces = new List<PieceRec>();
        internal readonly Dictionary<long, byte> Cells = new Dictionary<long, byte>();
        internal byte[] TreeCounts = new byte[0];
        internal readonly HashSet<long> Zones = new HashSet<long>();
        internal int TextureSize;
        internal bool Dirty;
        internal bool HelloSent;
        internal bool HelloDone;
        internal bool ExploreReady;
        internal bool MarkersReady;
        internal bool Denied;
        internal bool OverlayDirty = true;
        internal bool FogDirty;
        internal readonly OverlayDelta Pending = new OverlayDelta();
        internal readonly HashSet<long> Peers = new HashSet<long>();
        internal MapNet Net { get { return _net; } }

        private string _dir;
        private string _stem;
        private float _nextSave;
        private float _nextExplore = -1f;
        private bool _loaded;
        private readonly MapNet _net = new MapNet();
        private readonly WorldScan _scan = new WorldScan();
        private readonly OverlayDraw _draw = new OverlayDraw();
        private readonly MapPainter _painter = new MapPainter();
        internal MapPainter Painter { get { return _painter; } }

        private static System.Reflection.FieldInfo _world;

        private static bool WorldOpen()
        {
            if (ZNet.instance == null) return false;
            if (_world == null) _world = AccessTools.Field(typeof(ZNet), "m_world");
            if (_world == null || _world.GetValue(null) == null) return false;
            return ZNet.instance.GetWorldUID() != 0;
        }

        internal void Tick()
        {
            if (!WorldOpen())
            {
                LeaveWorld();
                return;
            }
            if (!ReadyWorld()) return;
            EnsureLoaded();
            AdoptLocalMap();
            _net.Tick(this);
            if (ZNet.instance.IsServer() && Minimap.instance != null && !HelloDone)
            {
                _painter.UploadLocalPins(this, false);
                HelloDone = true;
            }
            if (Plugin.FootprintsOn() && ZNet.instance.IsServer()) _scan.Tick(this);
            if (Minimap.instance != null)
            {
                _painter.Tick(this);
                if (Plugin.FootprintsOn()) _draw.Tick(this);
            }
            if (ZNet.instance.IsServer() && Dirty && Time.time >= _nextSave)
            {
                SaveNow();
                _nextSave = Time.time + SaveInterval;
            }
            if (Time.time >= _nextExplore)
            {
                _nextExplore = Time.time + ExploreInterval;
                _painter.CollectExplore(this);
                _painter.WatchPins(this);
            }
        }

        internal void SaveNow()
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || _stem == null || !Dirty) return;
            try
            {
                if (Explored != null) MapFiles.SaveExplored(Path(MapFiles.ExploredMagic), Explored);
                MapFiles.SaveMarkers(MarkerPath(), Markers);
                if (Plugin.FootprintsOn())
                    MapFiles.SaveOverlay(OverlayPath(), TextureSize, Pieces.ToArray(), CellsToArray(), TreeCounts, ZonesToArray());
                Dirty = false;
            }
            catch (System.Exception ex)
            {
                Plugin.LogWarning("Could not save the server map. Will try again. " + ex.Message);
            }
        }

        internal void MarkDirty()
        {
            Dirty = true;
        }

        private bool _loggedMapSize;

        private void AdoptLocalMap()
        {
            Minimap map = Minimap.instance;
            if (map == null || map.m_textureSize <= 0) return;
            if (TextureSize != 0 && TextureSize != map.m_textureSize) return;
            if (TextureSize == 0)
            {
                TextureSize = map.m_textureSize;
                if (!_loggedMapSize)
                {
                    _loggedMapSize = true;
                    Plugin.LogInfo(Plugin.FootprintsOn()
                        ? "Using this world's map (" + TextureSize + " px). Buildings and paths are read from the world."
                        : "Using this world's map (" + TextureSize + " px). Shared exploration and markers are on. Building footprints are off.");
                }
            }
            if (ZNet.instance.IsServer() && Explored == null) Explored = new ExploredMap(TextureSize);
            if (Known == null) Known = new ExploredMap(TextureSize);
        }

        private void LeaveWorld()
        {
            if (!_loaded) return;
            SaveNow();
            _loaded = false;
            _stem = null;
            HelloSent = false;
            HelloDone = false;
            ExploreReady = false;
            MarkersReady = false;
            Denied = false;
            Explored = null;
            Known = null;
            TextureSize = 0;
            TreeCounts = new byte[0];
            Peers.Clear();
            Pending.Clear();
            Pieces.Clear();
            PatchPieces.Clear();
            Cells.Clear();
            Zones.Clear();
            OverlayDirty = true;
            FogDirty = false;
            _loggedMapSize = false;
            _painter.Reset();
            _net.Reset();
        }

        internal string Dir()
        {
            return _dir;
        }

        private bool ReadyWorld()
        {
            if (ZNet.instance == null || ZNet.instance.GetWorldUID() == 0) return false;
            if (!ZNet.instance.IsServer() && Player.m_localPlayer == null) return false;
            return true;
        }

        private void EnsureLoaded()
        {
            string stem = MapFiles.FileName(ZNet.instance.GetWorldUID());
            if (_loaded && _stem == stem) return;
            _dir = System.IO.Path.Combine(Paths.ConfigPath, "ServerMap");
            _stem = stem;
            _loaded = true;
            HelloSent = false;
            HelloDone = false;
            ExploreReady = false;
            MarkersReady = false;
            Denied = false;
            Explored = null;
            Known = null;
            TextureSize = 0;
            TreeCounts = new byte[0];
            Peers.Clear();
            Pending.Clear();
            OverlayDirty = true;
            _painter.Reset();
            _net.Reset();
            Markers = new MarkerBook();
            Pieces.Clear();
            PatchPieces.Clear();
            Cells.Clear();
            Zones.Clear();
            if (!ZNet.instance.IsServer()) return;
            ExploredMap loaded = MapFiles.LoadExplored(Path(MapFiles.ExploredMagic));
            if (loaded != null)
            {
                Explored = loaded;
                TextureSize = loaded.TextureSize;
                Known = new ExploredMap(TextureSize);
                Known.OrFrom(Explored);
            }
            MarkerBook markers = MapFiles.LoadMarkers(MarkerPath());
            if (markers != null) Markers = markers;
            if (!Plugin.FootprintsOn())
            {
                _nextSave = Time.time + SaveInterval;
                return;
            }
            int size;
            PieceRec[] pieces;
            PathCell[] cells;
            byte[] trees;
            ZoneRec[] zones;
            if (MapFiles.LoadOverlay(OverlayPath(), out size, out pieces, out cells, out trees, out zones))
            {
                if (TextureSize == 0) TextureSize = size;
                if (size == TextureSize)
                {
                    Pieces.AddRange(pieces);
                    for (int i = 0; i < cells.Length; i++)
                        Cells[OverlayRules.PackKey(cells[i].X, cells[i].Z)] = cells[i].Kind;
                    TreeCounts = trees ?? new byte[0];
                    for (int i = 0; i < zones.Length; i++)
                        Zones.Add(OverlayRules.PackKey(zones[i].X, zones[i].Y));
                }
            }
            _nextSave = Time.time + SaveInterval;
        }

        private string Path(byte[] magic)
        {
            string ext = magic == MapFiles.ExploredMagic ? ".explored.bin" : ".bin";
            return System.IO.Path.Combine(_dir, _stem + ext);
        }

        private string MarkerPath()
        {
            return System.IO.Path.Combine(_dir, _stem + ".markers.bin");
        }

        private string OverlayPath()
        {
            return System.IO.Path.Combine(_dir, _stem + ".overlay.bin");
        }

        private PathCell[] CellsToArray()
        {
            PathCell[] cells = new PathCell[Cells.Count];
            int i = 0;
            foreach (KeyValuePair<long, byte> pair in Cells)
            {
                cells[i].X = OverlayRules.KeyX(pair.Key);
                cells[i].Z = OverlayRules.KeyY(pair.Key);
                cells[i].Kind = pair.Value;
                i++;
            }
            return cells;
        }

        private ZoneRec[] ZonesToArray()
        {
            ZoneRec[] zones = new ZoneRec[Zones.Count];
            int i = 0;
            foreach (long key in Zones)
            {
                zones[i].X = OverlayRules.KeyX(key);
                zones[i].Y = OverlayRules.KeyY(key);
                i++;
            }
            return zones;
        }
    }
}
