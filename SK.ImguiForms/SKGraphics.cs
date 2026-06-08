using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Numerics;
using Hexa.NET.ImGui;
using SK.ImguiForms;
using SixLabors.ImageSharp.PixelFormats;

namespace SKFramework {

    public static class SKOverlayHelpers {
        public static Vector4 ToImguiVec4(this Color c) {
            return new Vector4((float)(int)c.R / 255f, (float)(int)c.G / 255f, (float)(int)c.B / 255f, (float)(int)c.A / 255f);
        }

        public static Vector4 ToImguiVec4(this Color c, byte alpha) {
            return new Vector4((int)c.R, (int)c.G, (int)c.B, (int)alpha);
        }
    }

    public enum IconSize {
        Small,
        Medium,
        Large,
    }

    public abstract class SKGraphics {
        private ImDrawListPtr _drawList;
        protected readonly ImguiWindow overlay;
        const float SmallIconFontSize = 14f;
        const float MediumIconFontSize = 20f;
        const float LargeIconFontSize = 28f;
        // Nominal pixel size the fonts are registered at. With Dear ImGui 1.92 fonts are scalable, so this is
        // only a baseline — actual render size is chosen per draw via PushFont(font, size) / AddText(font, size).
        const float FontLoadSize = 16f;
        // Kept only for GetAvailableTextFontSizesDescending/GetClosestTextFontSize size enumeration helpers.
        const int MinTextFontSize = 12;
        const int MaxTextFontSize = 34;
        static string iconFontPath;
        static string textFontPath;
        static string cjkFontPath;
        // Single merged font: NotoMono (Latin/Cyrillic) + Microsoft YaHei (CJK, on-demand) + Font Awesome (icons).
        ImFontPtr font;

        protected SKGraphics(ImguiWindow overlay) {
            this.overlay = overlay;
        }

        public void BeginFrame(ImDrawListPtr drawList) {
            _drawList = drawList;
        }

        public float Scale(float value) {
            return overlay != null ? value * overlay.EffectiveUiScale : value;
        }

        public Vector2 Scale(Vector2 value) {
            return value * Scale(1f);
        }

        public RectangleF Scale(RectangleF value) {
            var scale = Scale(1f);
            return new RectangleF(value.X * scale, value.Y * scale, value.Width * scale, value.Height * scale);
        }

        static float GetIconFontSize(IconSize size) {
            return size switch {
                IconSize.Small => SmallIconFontSize,
                IconSize.Large => LargeIconFontSize,
                _ => MediumIconFontSize,
            };
        }

        static bool TryResolveFontPaths() {
            var fontsDir = Path.Combine(AppContext.BaseDirectory, "Fonts");
            textFontPath = Path.Combine(fontsDir, "NotoMono-Regular.ttf");
            iconFontPath = Path.Combine(fontsDir, "Font Awesome 7 Free-Solid-900.otf");
            // CJK glyphs come from the Windows system font (Microsoft YaHei); not bundled (licensing).
            var cjk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc");
            cjkFontPath = File.Exists(cjk) ? cjk : null;
            return File.Exists(textFontPath) && File.Exists(iconFontPath);
        }

        float GetScaledFontSize(float fontSize) {
            return overlay != null
                ? overlay.ScaleFontSize(fontSize)
                : MathF.Max(1f, MathF.Round(fontSize));
        }

        // Builds this context's font atlas. Dear ImGui 1.92 rasterizes glyphs on demand at any size, so we add
        // each face exactly once (no per-size caches). CJK/Latin merge by glyph coverage; Font Awesome is
        // restricted to the icon PUA range so it doesn't shadow text glyphs.
        public unsafe void LoadFonts(ImFontConfigPtr fontConfig) {
            if(!TryResolveFontPaths()) {
                return;
            }

            var io = ImGui.GetIO();

            font = io.Fonts.AddFontFromFileTTF(textFontPath, FontLoadSize, fontConfig);

            if(cjkFontPath != null) {
                fontConfig.MergeMode = true;
                io.Fonts.AddFontFromFileTTF(cjkFontPath, FontLoadSize, fontConfig);
                fontConfig.MergeMode = false;
            }

            uint[] iconRanges = [0xe000, 0xf8ff, 0];
            fixed(uint* glyphRanges = iconRanges) {
                fontConfig.MergeMode = true;
                io.Fonts.AddFontFromFileTTF(iconFontPath, FontLoadSize, fontConfig, glyphRanges);
                fontConfig.MergeMode = false;
            }
        }

        protected ImFontPtr GetIconFont(IconSize size) {
            return font;
        }

        protected ImFontPtr GetIconFont(float fontSize) {
            return font;
        }

        public static IEnumerable<int> GetAvailableTextFontSizesDescending(float maxFontSize, float minFontSize = MinTextFontSize) {
            int normalizedMax = NormalizeTextFontSize(maxFontSize);
            int normalizedMin = NormalizeTextFontSize(minFontSize);
            for(int fontSize = normalizedMax; fontSize >= normalizedMin; fontSize--) {
                yield return fontSize;
            }
        }

        static int NormalizeTextFontSize(float fontSize) {
            return (int)Math.Clamp(MathF.Round(fontSize), MinTextFontSize, MaxTextFontSize);
        }

        public static int GetClosestTextFontSize(float fontSize) {
            return NormalizeTextFontSize(fontSize);
        }

        public ImFontPtr GetTextFont(float fontSize) {
            return font;
        }

        public float GetResolvedTextFontSize(float fontSize) {
            return GetScaledFontSize(fontSize);
        }

        public Vector2 MeasureText(string text, float fontSize) {
            if(string.IsNullOrEmpty(text)) {
                return Vector2.Zero;
            }

            if(font.IsNull) {
                return ImGui.CalcTextSize(text);
            }

            ImGui.PushFont(font, GetScaledFontSize(fontSize));
            try {
                return ImGui.CalcTextSize(text);
            }
            finally {
                ImGui.PopFont();
            }
        }

        static string ToIconGlyph(int iconNumber) {
            int codePoint;
            if(iconNumber >= 0xE000 && iconNumber <= 0x10FFFF) {
                codePoint = iconNumber;
            }
            else {
                codePoint = 0xF000 + iconNumber;
            }

            return char.ConvertFromUtf32(codePoint);
        }

        public static string GetIconGlyph(int iconNumber) {
            return ToIconGlyph(iconNumber);
        }

        public Vector2 MeasureIconGlyph(string glyph, IconSize size) {
            if(string.IsNullOrEmpty(glyph)) {
                return Vector2.Zero;
            }

            if(font.IsNull) {
                return ImGui.CalcTextSize(glyph);
            }

            ImGui.PushFont(font, GetScaledFontSize(GetIconFontSize(size)));
            try {
                return ImGui.CalcTextSize(glyph);
            }
            finally {
                ImGui.PopFont();
            }
        }

        static Vector2 SnapTextPosition(Vector2 position) {
            return new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
        }

        public void DrawLine(Vector2 start, Vector2 end, Color color, float thickness = 1.0f) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            _drawList.AddLine(start, end, col, thickness);
        }

        public void DrawRect(Vector2 position, Vector2 size, Color color, float thickness = 1.0f, float rounding = 0.0f) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            _drawList.AddRect(position, position + size, col, rounding, ImDrawFlags.None, thickness);
        }

        public void DrawRectFilled(Vector2 position, Vector2 size, Color color, float rounding = 0.0f) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            _drawList.AddRectFilled(position, position + size, col, rounding);
        }

        public void DrawCircle(Vector2 center, float radius, Color color, int segments = 0, float thickness = 1.0f) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            _drawList.AddCircle(center, radius, col, segments, thickness);
        }

        public void DrawCircleFilled(Vector2 center, float radius, Color color, int segments = 0) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            _drawList.AddCircleFilled(center, radius, col, segments);
        }

        public void DrawText(Vector2 position, Color color, string text) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            position = SnapTextPosition(position);
            _drawList.AddText(position, col, text);
        }

        public void DrawText(Vector2 position, Color color, string text, ImFontPtr font) {
            AddTextToDrawList(_drawList, position, color, text, font, ImGui.GetFontSize());
        }

        public void DrawText(Vector2 position, Color color, string text, float fontSize) {
            AddTextToDrawList(_drawList, position, color, text, font, GetScaledFontSize(fontSize));
        }

        protected void AddTextToDrawList(ImDrawListPtr drawList, Vector2 position, Color color, string text, ImFontPtr font) {
            AddTextToDrawList(drawList, position, color, text, font, ImGui.GetFontSize());
        }

        protected unsafe void AddTextToDrawList(ImDrawListPtr drawList, Vector2 position, Color color, string text, ImFontPtr font, float fontSize) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            position = SnapTextPosition(position);
            if(!font.IsNull) {
                drawList.AddText(font, fontSize, position, col, text);
            }
            else {
                drawList.AddText(position, col, text);
            }
        }

        public void DrawIconGlyph(Vector2 position, uint color, string glyph, IconSize size) {
            AddIconGlyphToDrawList(_drawList, position, color, glyph, size);
        }

        protected unsafe void AddIconGlyphToDrawList(ImDrawListPtr drawList, Vector2 position, uint color, string glyph, IconSize size) {
            if(!font.IsNull) {
                drawList.AddText(font, GetScaledFontSize(GetIconFontSize(size)), position, color, glyph);
            }
            else {
                drawList.AddText(position, color, glyph);
            }
        }

        public void DrawTriangle(Vector2 p1, Vector2 p2, Vector2 p3, Color color, float thickness = 1.0f) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            _drawList.AddTriangle(p1, p2, p3, col, thickness);
        }

        public void DrawTriangleFilled(Vector2 p1, Vector2 p2, Vector2 p3, Color color) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            _drawList.AddTriangleFilled(p1, p2, p3, col);
        }

        public void DrawQuad(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4, Color color, float thickness = 1.0f) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            _drawList.AddQuad(p1, p2, p3, p4, col, thickness);
        }

        public void DrawQuadFilled(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4, Color color) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            _drawList.AddQuadFilled(p1, p2, p3, p4, col);
        }

        public void DrawRect(RectangleF rect, Color color) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            Vector2 p_min = new Vector2(rect.X, rect.Y);
            Vector2 p_max = new Vector2(rect.X + rect.Width, rect.Y + rect.Height);
            _drawList.AddRect(p_min, p_max, col);
        }

        public void DrawRect(RectangleF rect, Color color, float rounding) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            Vector2 p_min = new Vector2(rect.X, rect.Y);
            Vector2 p_max = new Vector2(rect.X + rect.Width, rect.Y + rect.Height);
            _drawList.AddRect(p_min, p_max, col, rounding);
        }

        public void DrawRect(RectangleF rect, Color color, float rounding, ImDrawFlags flags) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            Vector2 p_min = new Vector2(rect.X, rect.Y);
            Vector2 p_max = new Vector2(rect.X + rect.Width, rect.Y + rect.Height);
            _drawList.AddRect(p_min, p_max, col, rounding, flags);
        }

        public void DrawRect(RectangleF rect, Color color, float rounding, ImDrawFlags flags, float thickness) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            Vector2 p_min = new Vector2(rect.X, rect.Y);
            Vector2 p_max = new Vector2(rect.X + rect.Width, rect.Y + rect.Height);
            _drawList.AddRect(p_min, p_max, col, rounding, flags, thickness);
        }

        public void DrawTextInBox(RectangleF rect, Color color, string text) {
            var textSize = ImGui.CalcTextSize(text);
            var center = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
            DrawText(new Vector2(center.X - textSize.X / 2, center.Y - textSize.Y / 2), color, text);
        }

        public void DrawRectFilled(RectangleF rect, Color color) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            Vector2 p_min = new Vector2(rect.X, rect.Y);
            Vector2 p_max = new Vector2(rect.X + rect.Width, rect.Y + rect.Height);
            _drawList.AddRectFilled(p_min, p_max, col);
        }

        public void DrawRectFilled(RectangleF rect, Color color, float rounding) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            Vector2 p_min = new Vector2(rect.X, rect.Y);
            Vector2 p_max = new Vector2(rect.X + rect.Width, rect.Y + rect.Height);
            _drawList.AddRectFilled(p_min, p_max, col, rounding);
        }

        public void DrawRectFilled(RectangleF rect, Color color, float rounding, ImDrawFlags flags) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            Vector2 p_min = new Vector2(rect.X, rect.Y);
            Vector2 p_max = new Vector2(rect.X + rect.Width, rect.Y + rect.Height);
            _drawList.AddRectFilled(p_min, p_max, col, rounding, flags);
        }

        public void DisposeImage(string key) {
            overlay.RemoveImage(key);
        }

        private static readonly Dictionary<string, Vector2> _cachedImageSizes = new();

        protected static Vector2 GetImageSize(string imagePath) {
            if(_cachedImageSizes.TryGetValue(imagePath, out var size)) {
                return size;
            }
            using(var imageStream = File.OpenRead(imagePath)) {
                var image = SixLabors.ImageSharp.Image.Load<Rgba32>(imageStream);
                size = new Vector2(image.Width, image.Height);
                _cachedImageSizes[imagePath] = size;
                return size;
            }
        }

        protected bool HaveTexture(string key) {
            return overlay.TryGetImagePointer(key, out _, out _, out _);
        }

        protected nint GetTexturePointer(string key) {
            if(overlay.TryGetImagePointer(key, out var handle, out _, out _)) {
                return handle;
            }
            return -1;
        }

        protected nint GetOrCreateTexturePointer(string imagePath, string imageName) {
            if(string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(imageName) || !File.Exists(imagePath)) {
                return 0;
            }

            if(HaveTexture(imageName)) {
                return GetTexturePointer(imageName);
            }

            using(var imageStream = File.OpenRead(imagePath)) {
                var image = SixLabors.ImageSharp.Image.Load<Rgba32>(imageStream);
                overlay.AddOrGetImagePointer(imageName, image, false, out var imagePointer);
                return imagePointer;
            }
        }

        // Draws text with an explicit font + size onto a draw list. Wraps the ImFontPtr->ImFont* conversion so
        // consumer projects don't need an unsafe context (Hexa's AddText only exposes the ImFont* overload).
        public static unsafe void DrawListAddText(ImDrawListPtr drawList, ImFontPtr font, float fontSize, Vector2 position, uint color, string text) {
            drawList.AddText(font, fontSize, position, color, text);
        }

        // Wraps a backend texture handle (ID3D11ShaderResourceView pointer) as a 1.92 ImTextureRef.
        public static unsafe ImTextureRef ToTextureRef(nint handle) {
            return new ImTextureRef(null, new ImTextureID(handle));
        }

        public void DrawImage(string imagePath, string imageName, Vector2 position, Vector2 size) {
            if(string.IsNullOrWhiteSpace(imagePath) || size.X <= 0f || size.Y <= 0f) {
                return;
            }

            var texture = GetOrCreateTexturePointer(imagePath, imageName);
            if(texture == 0) {
                return;
            }

            _drawList.AddImage(ToTextureRef(texture), position, position + size);
        }

        public static uint ColorRGBA(byte r, byte g, byte b, byte a = 255) {
            return ImGui.ColorConvertFloat4ToU32(new Vector4(r / 255f, g / 255f, b / 255f, a / 255f));
        }

        public static uint ColorRGBA(int r, int g, int b, int a = 255) {
            return ColorRGBA((byte)r, (byte)g, (byte)b, (byte)a);
        }
    }
}
