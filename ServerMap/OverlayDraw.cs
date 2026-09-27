using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ServerMap
{
    internal sealed class OverlayDraw
    {
        private RenderTexture _texture;
        private int _scale = -1;
        private Material _color;
        private bool _failed;
        private float _nextFogCheck;
        private int _options = -1;
        private Texture2D _vanillaMap;
        private readonly List<long> _heldCells = new List<long>();
        private readonly List<int> _heldPieces = new List<int>();

        internal void Tick(MapSession session)
        {
            if (_failed || session.TextureSize <= 0) return;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            Minimap map = Minimap.instance;
            if (map == null || map.m_mapTexture == null || map.m_mapImageLarge == null) return;
            int scale = Scale();
            int options = OptionBits();
            if (_texture == null || _scale != scale || _vanillaMap != map.m_mapTexture)
            {
                _scale = scale;
                _vanillaMap = map.m_mapTexture;
                if (!Create(map, session.TextureSize * scale)) return;
                session.OverlayDirty = true;
            }
            if (_options != options)
            {
                _options = options;
                session.OverlayDirty = true;
            }
            if (session.PatchPieces.Count > 0 && (_texture == null || !_texture.IsCreated()))
                session.OverlayDirty = true;
            if (session.OverlayDirty)
            {
                session.OverlayDirty = false;
                session.FogDirty = false;
                session.PatchPieces.Clear();
                Rebuild(session, map);
            }
            else if (session.PatchPieces.Count > 0)
            {
                DrawPatch(session, map);
            }
            else if (session.FogDirty || (Time.time >= _nextFogCheck && (_heldCells.Count > 0 || _heldPieces.Count > 0)))
            {
                session.FogDirty = false;
                _nextFogCheck = Time.time + 15f;
                ReleaseFog(session, map);
            }
        }

        private bool Create(Minimap map, int size)
        {
            Texture2D source = map.m_mapTexture;
            if (source == null) return false;
            while (size >= map.m_textureSize)
            {
                RenderTexture texture = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32);
                texture.name = "ServerMap";
                texture.useMipMap = true;
                texture.autoGenerateMips = false;
                texture.filterMode = source.filterMode;
                texture.wrapMode = source.wrapMode;
                if (texture.Create())
                {
                    if (_texture != null)
                    {
                        _texture.Release();
                        UnityEngine.Object.Destroy(_texture);
                    }
                    _texture = texture;
                    _vanillaMap = source;
                    ApplyMain(map, _texture);
                    Poke(map);
                    return true;
                }
                UnityEngine.Object.Destroy(texture);
                size /= 2;
                _scale = Mathf.Max(1, _scale / 2);
            }
            _failed = true;
            Plugin.LogWarning("The map overlay texture could not be created. The overlay is off for this session.");
            return false;
        }

        private void Rebuild(MapSession session, Minimap map)
        {
            if (map.m_mapTexture == null || !EnsureColor()) return;
            Graphics.Blit(map.m_mapTexture, _texture);
            _heldCells.Clear();
            _heldPieces.Clear();
            float pixel = map.m_pixelSize;
            int half = session.TextureSize / 2;
            BitArray explored = AccessTools.Field(typeof(Minimap), "m_explored").GetValue(map) as BitArray;
            BitArray others = AccessTools.Field(typeof(Minimap), "m_exploredOthers").GetValue(map) as BitArray;
            RenderTexture.active = _texture;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, _texture.width, 0, _texture.height);
            _color.SetPass(0);
            GL.Begin(GL.QUADS);
            if (Plugin.ShowPaths.Value)
            {
                foreach (KeyValuePair<long, byte> pair in session.Cells)
                {
                    byte kind = pair.Value;
                    if (kind == OverlayRules.KindLevelled && !Plugin.ShowClearedGround.Value) continue;
                    float wx = (OverlayRules.KeyX(pair.Key) + 0.5f) * 2f;
                    float wz = (OverlayRules.KeyY(pair.Key) + 0.5f) * 2f;
                    if (!Visible(session, explored, others, wx, wz, pixel, half))
                    {
                        _heldCells.Add(pair.Key);
                        continue;
                    }
                    Quad(wx - 1f, wz - 1f, wx + 1f, wz + 1f, pixel, half, ColorFor(kind));
                }
            }
            if (Plugin.ShowBuildings.Value)
            {
                float halo = Plugin.OutlineWidthMeters.Value;
                for (int pass = 0; pass < 2; pass++)
                {
                    if (pass == 0 && !Plugin.BuildingOutline.Value) continue;
                    for (int i = 0; i < session.Pieces.Count; i++)
                    {
                        PieceRec piece = session.Pieces[i];
                        if (!OverlayRules.ShowPiece(Plugin.OnlyPlayerBuilt.Value, piece.HasCreator)) continue;
                        float cx = (piece.X0 + piece.X1) * 0.5f;
                        float cz = (piece.Z0 + piece.Z1) * 0.5f;
                        if (!Visible(session, explored, others, cx, cz, pixel, half))
                        {
                            if (pass == (Plugin.BuildingOutline.Value ? 0 : 1)) _heldPieces.Add(i);
                            continue;
                        }
                        float x0 = piece.X0;
                        float x1 = piece.X1;
                        float z0 = piece.Z0;
                        float z1 = piece.Z1;
                        float min = Plugin.MinPieceSizeMeters.Value;
                        if (x1 - x0 < min) { float mid = (x0 + x1) * 0.5f; x0 = mid - min * 0.5f; x1 = mid + min * 0.5f; }
                        if (z1 - z0 < min) { float mid = (z0 + z1) * 0.5f; z0 = mid - min * 0.5f; z1 = mid + min * 0.5f; }
                        if (pass == 0) Quad(x0 - halo, z0 - halo, x1 + halo, z1 + halo, pixel, half, Drawn(Plugin.OutlineColor.Value));
                        else Quad(x0, z0, x1, z1, pixel, half, MaterialColor(piece.Material));
                    }
                }
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = null;
            if (_texture != null && _texture.useMipMap) _texture.GenerateMips();
            ApplyMain(map, _texture);
        }

        private readonly List<int> _patchSet = new List<int>();

        private void DrawPatch(MapSession session, Minimap map)
        {
            if (_texture == null || map.m_mapTexture == null || !EnsureColor())
            {
                if (_failed) session.PatchPieces.Clear();
                return;
            }
            PieceRec[] added = session.PatchPieces.ToArray();
            session.PatchPieces.Clear();
            BitArray explored = AccessTools.Field(typeof(Minimap), "m_explored").GetValue(map) as BitArray;
            BitArray others = AccessTools.Field(typeof(Minimap), "m_exploredOthers").GetValue(map) as BitArray;
            float pixel = map.m_pixelSize;
            int half = session.TextureSize / 2;
            float halo = Plugin.OutlineWidthMeters.Value;
            RenderTexture.active = _texture;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, _texture.width, 0, _texture.height);
            _color.SetPass(0);
            GL.Begin(GL.QUADS);
            for (int n = 0; n < added.Length; n++)
            {
                PieceRec piece = added[n];
                int index = FindPiece(session.Pieces, piece);
                float cx = (piece.X0 + piece.X1) * 0.5f;
                float cz = (piece.Z0 + piece.Z1) * 0.5f;
                if (!Visible(session, explored, others, cx, cz, pixel, half))
                {
                    if (index >= 0) _heldPieces.Add(index);
                    continue;
                }
                _patchSet.Clear();
                if (index >= 0) _patchSet.Add(index);
                else _patchSet.Add(-1);
                float margin = (Plugin.BuildingOutline.Value ? halo : 0f) + Plugin.MinPieceSizeMeters.Value;
                for (int i = 0; i < session.Pieces.Count; i++)
                {
                    if (i == index) continue;
                    if (Near(session.Pieces[i], piece, margin)) _patchSet.Add(i);
                }
                for (int pass = 0; pass < 2; pass++)
                {
                    if (pass == 0 && !Plugin.BuildingOutline.Value) continue;
                    for (int k = 0; k < _patchSet.Count; k++)
                    {
                        int i = _patchSet[k];
                        PieceRec drawn = i >= 0 && i < session.Pieces.Count ? session.Pieces[i] : piece;
                        if (i >= 0 && i >= session.Pieces.Count) continue;
                        DrawPiecePass(drawn, pixel, half, halo, pass);
                    }
                }
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = null;
            if (_texture != null && _texture.useMipMap) _texture.GenerateMips();
        }

        private void ReleaseFog(MapSession session, Minimap map)
        {
            if (_texture == null || !EnsureColor() || (!_texture.IsCreated() && !_texture.Create())) return;
            BitArray explored = AccessTools.Field(typeof(Minimap), "m_explored").GetValue(map) as BitArray;
            BitArray others = AccessTools.Field(typeof(Minimap), "m_exploredOthers").GetValue(map) as BitArray;
            float pixel = map.m_pixelSize;
            int half = session.TextureSize / 2;
            RenderTexture.active = _texture;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, _texture.width, 0, _texture.height);
            _color.SetPass(0);
            GL.Begin(GL.QUADS);
            if (Plugin.ShowPaths.Value)
            {
                int write = 0;
                for (int i = 0; i < _heldCells.Count; i++)
                {
                    long key = _heldCells[i];
                    byte kind;
                    if (!session.Cells.TryGetValue(key, out kind)) continue;
                    if (kind == OverlayRules.KindLevelled && !Plugin.ShowClearedGround.Value) continue;
                    float wx = (OverlayRules.KeyX(key) + 0.5f) * 2f;
                    float wz = (OverlayRules.KeyY(key) + 0.5f) * 2f;
                    if (!Visible(session, explored, others, wx, wz, pixel, half))
                    {
                        _heldCells[write++] = key;
                        continue;
                    }
                    Quad(wx - 1f, wz - 1f, wx + 1f, wz + 1f, pixel, half, ColorFor(kind));
                }
                if (write < _heldCells.Count) _heldCells.RemoveRange(write, _heldCells.Count - write);
            }
            if (Plugin.ShowBuildings.Value)
            {
                float halo = Plugin.OutlineWidthMeters.Value;
                int write = 0;
                for (int n = 0; n < _heldPieces.Count; n++)
                {
                    int i = _heldPieces[n];
                    if (i < 0 || i >= session.Pieces.Count)
                        continue;
                    PieceRec piece = session.Pieces[i];
                    float cx = (piece.X0 + piece.X1) * 0.5f;
                    float cz = (piece.Z0 + piece.Z1) * 0.5f;
                    if (!Visible(session, explored, others, cx, cz, pixel, half))
                    {
                        _heldPieces[write++] = i;
                        continue;
                    }
                    DrawPiece(piece, pixel, half, halo);
                }
                if (write < _heldPieces.Count) _heldPieces.RemoveRange(write, _heldPieces.Count - write);
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = null;
            if (_texture != null && _texture.useMipMap) _texture.GenerateMips();
        }

        private void DrawPiece(PieceRec piece, float pixel, int half, float halo)
        {
            if (Plugin.BuildingOutline.Value) DrawPiecePass(piece, pixel, half, halo, 0);
            DrawPiecePass(piece, pixel, half, halo, 1);
        }

        private void DrawPiecePass(PieceRec piece, float pixel, int half, float halo, int pass)
        {
            if (!OverlayRules.ShowPiece(Plugin.OnlyPlayerBuilt.Value, piece.HasCreator)) return;
            float x0 = piece.X0;
            float x1 = piece.X1;
            float z0 = piece.Z0;
            float z1 = piece.Z1;
            float min = Plugin.MinPieceSizeMeters.Value;
            if (x1 - x0 < min) { float mid = (x0 + x1) * 0.5f; x0 = mid - min * 0.5f; x1 = mid + min * 0.5f; }
            if (z1 - z0 < min) { float mid = (z0 + z1) * 0.5f; z0 = mid - min * 0.5f; z1 = mid + min * 0.5f; }
            if (pass == 0) Quad(x0 - halo, z0 - halo, x1 + halo, z1 + halo, pixel, half, Drawn(Plugin.OutlineColor.Value));
            else Quad(x0, z0, x1, z1, pixel, half, MaterialColor(piece.Material));
        }

        private bool EnsureColor()
        {
            if (_failed) return false;
            if (_color != null) return _color.passCount > 0;
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return false;
            _color = new Material(shader);
            _color.SetInt("_ZWrite", 0);
            _color.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            _color.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _color.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _color.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (_color.passCount > 0) return true;
            _failed = true;
            Plugin.LogWarning("The map overlay shader has no passes on this machine. The overlay is off for this session.");
            return false;
        }

        private static int FindPiece(List<PieceRec> pieces, PieceRec piece)
        {
            for (int i = pieces.Count - 1; i >= 0; i--)
            {
                PieceRec other = pieces[i];
                if (other.X0 == piece.X0 && other.X1 == piece.X1 && other.Z0 == piece.Z0 && other.Z1 == piece.Z1
                    && other.Y == piece.Y && other.Material == piece.Material && other.HasCreator == piece.HasCreator)
                    return i;
            }
            return -1;
        }

        private static bool Near(PieceRec a, PieceRec b, float margin)
        {
            return a.X1 >= b.X0 - margin && a.X0 <= b.X1 + margin && a.Z1 >= b.Z0 - margin && a.Z0 <= b.Z1 + margin;
        }

        private static void ApplyMain(Minimap map, Texture texture)
        {
            if (map.m_mapImageSmall != null && map.m_mapImageSmall.material != null)
                map.m_mapImageSmall.material.SetTexture("_MainTex", texture);
            if (map.m_mapImageLarge != null && map.m_mapImageLarge.material != null)
                map.m_mapImageLarge.material.SetTexture("_MainTex", texture);
        }

        private static void Poke(Minimap map)
        {
            if (map.m_smallRoot == null || !map.m_smallRoot.activeSelf) return;
            map.m_smallRoot.SetActive(false);
            map.m_smallRoot.SetActive(true);
        }

        private static int OptionBits()
        {
            int bits = 0;
            if (Plugin.ShowBuildings.Value) bits |= 1;
            if (Plugin.OnlyPlayerBuilt.Value) bits |= 2;
            if (Plugin.ShowPaths.Value) bits |= 4;
            if (Plugin.ShowClearedGround.Value) bits |= 8;
            if (Plugin.BuildingOutline.Value) bits |= 16;
            if (Plugin.RespectFog.Value) bits |= 32;
            return bits;
        }

        private static bool Visible(MapSession session, BitArray explored, BitArray others, float worldX, float worldZ, float pixel, int half)
        {
            if (!Plugin.RespectFog.Value) return true;
            int px = Mathf.RoundToInt(worldX / pixel + half);
            int py = Mathf.RoundToInt(worldZ / pixel + half);
            int index = py * session.TextureSize + px;
            if (index < 0) return false;
            if (explored != null && index < explored.Length && explored[index]) return true;
            if (others != null && index < others.Length && others[index]) return true;
            return false;
        }

        private void Quad(float x0, float z0, float x1, float z1, float pixel, int half, Color color)
        {
            GL.Color(color);
            float sx = _scale;
            float ax = (x0 / pixel + half) * sx;
            float bx = (x1 / pixel + half) * sx;
            float ay = (z0 / pixel + half) * sx;
            float by = (z1 / pixel + half) * sx;
            GL.Vertex3(ax, ay, 0f);
            GL.Vertex3(bx, ay, 0f);
            GL.Vertex3(bx, by, 0f);
            GL.Vertex3(ax, by, 0f);
        }

        private static Color ColorFor(byte kind)
        {
            if (kind == OverlayRules.KindPaved) return Drawn(Plugin.PavedColor.Value);
            if (kind == OverlayRules.KindDirt) return Drawn(Plugin.DirtColor.Value);
            if (kind == OverlayRules.KindCultivated) return Drawn(Plugin.CultivatedColor.Value);
            return Drawn(Plugin.ClearedColor.Value);
        }

        private static Color MaterialColor(byte material)
        {
            if (material < Plugin.MaterialColors.Length) return Drawn(Plugin.MaterialColors[material].Value);
            return Drawn(Plugin.UnknownColor.Value);
        }

        private static Color Drawn(Color color)
        {
            if (QualitySettings.activeColorSpace == ColorSpace.Linear) return color.linear;
            return color;
        }

        private static int Scale()
        {
            return 1;
        }
    }
}
