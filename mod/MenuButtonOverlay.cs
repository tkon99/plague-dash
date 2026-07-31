using System.Linq;
using UnityEngine;

namespace PlagueDash
{
    /// <summary>
    /// Draws a "Plague Dash" overlay button on the game's main menu that opens the
    /// dashboard in the browser. Uses Unity IMGUI (OnGUI + GUI.Button).
    ///
    /// The button's background is the game's OWN button sprite, extracted at runtime
    /// from the NGUI atlas the native menu buttons use — so it looks pixel-identical
    /// to the in-game buttons (9-slice borders and all). Hover is a color tint, the
    /// same mechanism the native buttons use (UIButtonColor.hover).
    ///
    /// Visibility is the current screen being CMainMenuScreen (polled each frame).
    /// No game objects are cloned or reparented — we draw our own rectangle, so this
    /// is robust against menu-layout changes. If sprite extraction fails for any
    /// reason, it falls back to a styled procedural texture so the button always shows.
    /// </summary>
    public class MenuButtonOverlay : MonoBehaviour
    {
        private static bool _created;          // guard against duplicate instances
        private static GUIStyle _style;        // built once, cached
        private static float _scale = -1f;     // last scale the style was built for
        private static bool _extractedOk;      // did extraction succeed?
        private static int _extractAttempts;   // retries so far (give up after N)
        private const int MaxExtractAttempts = 600; // ~10s of frames on the menu

        private void OnGUI()
        {
            if (!Main.Enabled) return;
            var menu = GetCurrentMainMenu();
            if (menu == null) return;
            try { DrawButton(menu); }
            catch (System.Exception e) { Main.Log("MenuButtonOverlay draw failed: " + e.Message); }
        }

        /// <summary>The current screen if it's the main menu, else null.</summary>
        private static CMainMenuScreen GetCurrentMainMenu()
        {
            try
            {
                object ui = HarmonyLib.AccessTools.Field(typeof(CUIManager), "instance").GetValue(null);
                if (ui == null) return null;
                object screen = HarmonyLib.AccessTools.Method(typeof(CUIManager), "GetCurrentScreen")
                                .Invoke(ui, null);
                return screen as CMainMenuScreen;
            }
            catch { return null; }
        }

        private void DrawButton(CMainMenuScreen menu)
        {
            float scale = Screen.height / 1080f;

            // Keep trying to extract the native button sprite each frame until it
            // succeeds. The start sub-screen is built lazily, so startSubScreen may
            // be null for the first few frames; retrying lets us catch it once it's
            // ready. Give up after MaxExtractAttempts so we don't log forever.
            if (!_extractedOk && _extractAttempts < MaxExtractAttempts)
            {
                _extractAttempts++;
                bool ok = TryExtractNativeSprite(menu);
                if (ok)
                {
                    _extractedOk = true;
                    _style = null; // force a rebuild with the real texture
                }
            }
            // Rebuild the style if scale changed.
            if (_style == null || _scale != scale)
            {
                _scale = scale;
                _style = BuildStyle(scale);
            }

            // Size the button to the native sprite's aspect ratio so the art isn't
            // distorted. The Single Player sprite is 280×55; scale uniformly to the
            // screen. Falls back to 260×52 if we never extracted.
            float aspect = (_extractedOk && _nativeSpriteW > 0 && _nativeSpriteH > 0)
                           ? (float)_nativeSpriteW / _nativeSpriteH : 5f;
            float h = 52f * scale;
            float w = h * aspect;
            float margin = 40f * scale;
            var rect = new Rect(margin, margin, w, h);

            // Hover is handled by the style's hover.background (the native hover
            // sprite), so no GUI.color tint needed when we have the real texture.
            bool clicked = GUI.Button(rect, "☣  Open Plague Dash", _style);
            if (clicked)
            {
                Main.Log("Menu overlay button clicked.");
                if (DashboardServer.WaitUntilListening(2000))
                    DashboardServer.OpenInBrowser(Main.Settings.Port);
                else
                    Main.Log("Button clicked but server not listening on port " + Main.Settings.Port);
            }
        }

        // ---- native sprite extraction (one-time) ----
        private static Texture2D _nativeTex;     // cropped normal button sprite
        private static RectOffset _nativeBorder; // 9-slice borders from the normal sprite
        private static Texture2D _hoverTex;      // cropped hover button sprite
        private static RectOffset _hoverBorder;  // 9-slice borders from the hover sprite
        private static Color? _labelColor;       // native button label color
        private static int _nativeSpriteW;       // native sprite pixel width (for aspect)
        private static int _nativeSpriteH;       // native sprite pixel height

        private static bool TryExtractNativeSprite(CMainMenuScreen menu)
        {
            try
            {
                // Reach the active sub-screen via the base-class GetActiveSubScreen().
                // The start sub-screen holds the standard menu buttons (Single Player…).
                object activeSub = HarmonyLib.AccessTools.Method(typeof(IGameScreen), "GetActiveSubScreen")
                                   .Invoke(menu, null);
                if (activeSub == null) return false;

                // The Single Player button is a UIImageButton whose normal/hover/pressed
                // sprite NAMES are in its fields (normalSprite/hoverSprite/pressedSprite).
                // The art lives on a child 'Background' UISprite.
                var btn = HarmonyLib.AccessTools.Field(activeSub.GetType(), "buttonSinglePlayer")
                          .GetValue(activeSub) as UIImageButton;
                if (btn == null) { Main.Log("Sprite extract: buttonSinglePlayer not found / not UIImageButton"); return false; }

                string normalName = HarmonyLib.AccessTools.Field(typeof(UIImageButton), "normalSprite").GetValue(btn) as string;
                string hoverName = HarmonyLib.AccessTools.Field(typeof(UIImageButton), "hoverSprite").GetValue(btn) as string;
                string pressedName = HarmonyLib.AccessTools.Field(typeof(UIImageButton), "pressedSprite").GetValue(btn) as string;
                Main.Log("Sprite extract: UIImageButton normal='" + normalName + "' hover='" + hoverName + "' pressed='" + pressedName + "'");

                // Find a UISprite whose atlas has a POPULATED sprite list to resolve names
                // against. (The button's own Background sprite's atlas read empty on the
                // first frame; fall back to any UISprite in the menu with a real atlas.)
                var bgSprite = btn.GetComponentInChildren<UISprite>(true);
                UIAtlas atlas = bgSprite != null ? bgSprite.atlas : null;
                if (!AtlasHasSprites(atlas))
                {
                    atlas = null;
                    foreach (var s in menu.GetComponentsInChildren<UISprite>(true))
                    {
                        if (AtlasHasSprites(s.atlas)) { atlas = s.atlas; break; }
                    }
                    if (atlas == null && activeSub is MonoBehaviour mb)
                        foreach (var s in mb.GetComponentsInChildren<UISprite>(true))
                        {
                            if (AtlasHasSprites(s.atlas)) { atlas = s.atlas; break; }
                        }
                }
                if (atlas == null) { Main.Log("Sprite extract: no atlas with populated sprite list found"); return false; }

                var atlasTex = atlas.texture as Texture2D;
                if (atlasTex == null) { Main.Log("Sprite extract: atlas texture not Texture2D"); return false; }
                Main.Log("Sprite extract: atlas " + atlasTex.width + "x" + atlasTex.height + " fmt=" + atlasTex.format);

                // Resolve + crop the normal sprite (required) and hover sprite (optional).
                if (!CropNamed(atlas, atlasTex, normalName, out var normalTex, out var normalBorder, out int nw, out int nh))
                { Main.Log("Sprite extract: could not resolve/crop normal sprite '" + normalName + "'"); return false; }

                _nativeTex = normalTex;
                _nativeBorder = normalBorder;
                _nativeSpriteW = nw;
                _nativeSpriteH = nh;

                if (!string.IsNullOrEmpty(hoverName))
                    CropNamed(atlas, atlasTex, hoverName, out _hoverTex, out _hoverBorder, out _, out _);
                if (_hoverTex == null) _hoverTex = normalTex; // fall back to normal for hover
                if (_hoverBorder == null) _hoverBorder = normalBorder;

                // Label color from the button's colour component (hover is handled by
                // the native hover sprite, so no tint is needed).
                var colorField = HarmonyLib.AccessTools.Field(typeof(UIButtonColor), "mColor");
                if (colorField != null) _labelColor = (Color)colorField.GetValue(btn);

                Main.Log("Sprite extract OK: normal='" + normalName + "' hover='" + hoverName + "'");
                return true;
            }
            catch (System.Exception e)
            {
                Main.Log("Sprite extract failed: " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);
                return false;
            }
        }

        private static bool AtlasHasSprites(UIAtlas atlas)
        {
            if (atlas == null) return false;
            var list = HarmonyLib.AccessTools.Field(typeof(UIAtlas), "mSprites")?.GetValue(atlas) as System.Collections.IList;
            return list != null && list.Count > 0;
        }

        /// <summary>Resolve a named sprite in the atlas, crop it, and report its borders + dims.</summary>
        private static bool CropNamed(UIAtlas atlas, Texture2D atlasTex, string name,
                                      out Texture2D tex, out RectOffset border, out int sw, out int sh)
        {
            tex = null; border = null; sw = 0; sh = 0;
            if (string.IsNullOrEmpty(name)) return false;
            var sd = atlas.GetSprite(name);
            if (sd == null) return false;
            sw = sd.width; sh = sd.height;
            // NGUI atlas coordinates are TOP-left origin; Unity Texture2D.GetPixels is
            // BOTTOM-left origin. Flip y so we read the correct region of the atlas.
            int flippedY = atlasTex.height - sd.y - sd.height;
            tex = CropReadable(atlasTex, sd.x, flippedY, sd.width, sd.height);
            border = new RectOffset(sd.borderLeft, sd.borderRight, sd.borderTop, sd.borderBottom);
            Main.Log("  cropped '" + name + "' rect x=" + sd.x + " y=" + sd.y + " (flippedY=" + flippedY + ") "
                + sd.width + "x" + sd.height
                + " border L/R/T/B=" + sd.borderLeft + "/" + sd.borderRight + "/" + sd.borderTop + "/" + sd.borderBottom
                + " tex=" + (tex != null));
            return tex != null;
        }

        /// <summary>Return a vertically-flipped copy of a Texture2D (row 0 ↔ last row).</summary>
        private static Texture2D FlipVertical(Texture2D src)
        {
            if (src == null) return null;
            int w = src.width, h = src.height;
            var pixels = src.GetPixels();
            var flipped = new Texture2D(w, h, src.format, false);
            var outPixels = new Color[pixels.Length];
            for (int y = 0; y < h; y++)
            {
                int srcRow = y * w;
                int dstRow = (h - 1 - y) * w;
                for (int x = 0; x < w; x++) outPixels[dstRow + x] = pixels[srcRow + x];
            }
            flipped.SetPixels(outPixels);
            flipped.Apply();
            return flipped;
        }

        /// <summary>Crop a region of an atlas texture into a readable Texture2D.
        /// Handles non-readable source textures via a RenderTexture copy. The result
        /// is vertically flipped so NGUI (top-origin) sprite content reads upright
        /// when used as an IMGUI GUIStyle background (which draws top-down).</summary>
        private static Texture2D CropReadable(Texture2D src, int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) return null;
            // Fast path: source is readable.
            try
            {
                var crop = new Texture2D(w, h, TextureFormat.ARGB32, false);
                crop.SetPixels(0, 0, w, h, src.GetPixels(x, y, w, h));
                crop.Apply();
                return FlipVertical(crop);
            }
            catch
            {
                // Fallback: GPU-side copy via RenderTexture (for compressed/non-readable).
                try
                {
                    var rt = RenderTexture.GetTemporary(w, h, 0);
                    Graphics.SetRenderTarget(rt);
                    // Draw the atlas region into the RT. UV rect in atlas space:
                    Rect uv = new Rect((float)x / src.width, (float)y / src.height,
                                       (float)w / src.width, (float)h / src.height);
                    GL.Clear(false, true, Color.clear);
                    GL.PushMatrix();
                    GL.LoadPixelMatrix(0, w, 0, h);
                    Graphics.DrawTexture(new Rect(0, 0, w, h), src, uv, 0, 0, 0, 0);
                    GL.PopMatrix();
                    var crop = new Texture2D(w, h, TextureFormat.ARGB32, false);
                    crop.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    crop.Apply();
                    RenderTexture.ReleaseTemporary(rt);
                    Graphics.SetRenderTarget(null);
                    return FlipVertical(crop);
                }
                catch (System.Exception e) { Main.Log("CropReadable fallback failed: " + e.Message); return null; }
            }
        }

        // ---- style ----
        private static GUIStyle BuildStyle(float scale)
        {
            var s = new GUIStyle(GUI.skin.button);
            s.fontSize = Mathf.RoundToInt(15f * scale);
            s.fontStyle = FontStyle.Bold;
            s.alignment = TextAnchor.MiddleCenter;
            s.wordWrap = false;

            // The label color from UIButtonColor.mColor is the button's TINT (often
            // transparent black = "no tint"), not the label color — using it directly
            // would make the text invisible. Only use it if it's actually a visible
            // color; otherwise fall back to a solid light cream like the native labels.
            Color label = (_extractedOk && _labelColor.HasValue
                           && _labelColor.Value.a > 0.05f
                           && (_labelColor.Value.r + _labelColor.Value.g + _labelColor.Value.b) > 0.1f)
                          ? _labelColor.Value
                          : HexColor("#f5ecd2");
            s.normal.textColor = label;
            s.hover.textColor = label;
            s.active.textColor = label;

            if (_extractedOk && _nativeTex != null)
            {
                // Use the REAL native sprites: normal for resting/active, hover for hover.
                s.normal.background = _nativeTex;
                s.hover.background = _hoverTex != null ? _hoverTex : _nativeTex;
                s.active.background = _nativeTex;
                s.onNormal.background = _nativeTex;
                RectOffset b = _nativeBorder ?? new RectOffset(2, 2, 2, 2);
                s.border = new RectOffset(b.left, b.right, b.top, b.bottom);
                int pad = Mathf.RoundToInt(16f * scale);
                s.padding = new RectOffset(pad, pad, Mathf.RoundToInt(8f * scale), Mathf.RoundToInt(8f * scale));
            }
            else
            {
                // Fallback procedural angular style.
                Color fillNormal = HexColor32("#3a1414", 0.96f);
                Color fillHover = HexColor32("#4a1c1c", 0.98f);
                Color border = HexColor32("#e8dcc0", 0.95f);
                s.normal.background = MakeAngularButtonTex(fillNormal, border);
                s.hover.background = MakeAngularButtonTex(fillHover, border);
                s.active.background = MakeAngularButtonTex(fillHover, border);
                int pad = Mathf.RoundToInt(16f * scale);
                s.padding = new RectOffset(pad, pad, Mathf.RoundToInt(8f * scale), Mathf.RoundToInt(8f * scale));
                int cut = Mathf.RoundToInt(8f * scale);
                s.border = new RectOffset(cut, cut, cut, cut);
            }
            return s;
        }

        // Octagon fallback texture (used only if native extraction fails).
        private static Texture2D MakeAngularButtonTex(Color fill, Color border)
        {
            const int S = 32, CUT = 8, B = 2;
            var tex = new Texture2D(S, S);
            var px = new Color[S * S];
            bool Inside(int x, int y)
            {
                int rx = S - 1 - x, ry = S - 1 - y;
                return (x + y >= CUT) && (rx + y >= CUT) && (x + ry >= CUT) && (rx + ry >= CUT);
            }
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    if (!Inside(x, y)) { px[y * S + x] = new Color(0, 0, 0, 0); continue; }
                    bool onEdge =
                        (x + y < CUT + B) || ((S - 1 - x) + y < CUT + B) ||
                        (x + (S - 1 - y) < CUT + B) || ((S - 1 - x) + (S - 1 - y) < CUT + B) ||
                        x < B || x >= S - B || y < B || y >= S - B;
                    px[y * S + x] = onEdge ? border : fill;
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        private static Color HexColor(string hex) { Color c; ColorUtility.TryParseHtmlString(hex, out c); return c; }
        private static Color32 HexColor32(string hex, float alpha) { Color c = HexColor(hex); c.a = alpha; return c; }

        // ---- instantiation (called once from Main.OnToggle) ----
        public static void EnsureExists()
        {
            if (_created) return;
            var go = new GameObject("PlagueDash.MenuButtonOverlay");
            go.AddComponent<MenuButtonOverlay>();
            Object.DontDestroyOnLoad(go);
            _created = true;
            Main.Log("Menu button overlay created.");
        }

        public static void DestroyIfExists()
        {
            _created = false;
            _extractAttempts = 0;
            _extractedOk = false;
            _nativeTex = null;
            _hoverTex = null;
            _hoverBorder = null;
            _nativeSpriteW = 0;
            _nativeSpriteH = 0;
            _style = null;
        }
    }
}
