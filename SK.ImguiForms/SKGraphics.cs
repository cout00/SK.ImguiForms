using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
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
        const int MinTextFontAtlasSize = 12;
        const int MaxTextFontAtlasSize = 34;
        readonly object iconFontsLock = new();
        readonly Dictionary<float, ImFontPtr> iconFonts = new();
        readonly HashSet<float> pendingIconFontSizes = new();
        readonly object textFontsLock = new();
        readonly Dictionary<int, ImFontPtr> textFonts = new();
        static string iconFontPath;
        static string textFontPath;

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

        static float NormalizeIconFontSize(float fontSize) {
            return MathF.Round(MathF.Max(1f, fontSize), 2);
        }

        static bool TryResolveFontPaths(out string textFontPath, out string fontAwesomePath) {
            textFontPath = Path.Combine(Path.Combine(AppContext.BaseDirectory, "Fonts"), "NotoMono-Regular.ttf");
            fontAwesomePath = Path.Combine(Path.Combine(AppContext.BaseDirectory, "Fonts"), "Font Awesome 7 Free-Solid-900.otf");
            if(!File.Exists(textFontPath) || !File.Exists(fontAwesomePath)) {
                return false;
            }

            SKGraphics.textFontPath = textFontPath;
            iconFontPath = fontAwesomePath;
            return true;
        }

        bool TryGetCachedIconFont(float fontSize, out ImFontPtr font) {
            lock(iconFontsLock) {
                return iconFonts.TryGetValue(fontSize, out font);
            }
        }

        void ResetFontCache() {
            lock(iconFontsLock) {
                iconFonts.Clear();
                pendingIconFontSizes.Clear();
            }

            lock(textFontsLock) {
                textFonts.Clear();
            }
        }

        unsafe ImFontPtr CacheIconFont(float fontSize, ImFontPtr font) {
            lock(iconFontsLock) {
                pendingIconFontSizes.Remove(fontSize);
                if((IntPtr)font.NativePtr != IntPtr.Zero) {
                    iconFonts[fontSize] = font;
                }

                return font;
            }
        }

        void ClearPendingIconFont(float fontSize) {
            lock(iconFontsLock) {
                pendingIconFontSizes.Remove(fontSize);
            }
        }

        bool TryBeginIconFontLoad(float fontSize) {
            lock(iconFontsLock) {
                if(iconFonts.ContainsKey(fontSize) || pendingIconFontSizes.Contains(fontSize)) {
                    return false;
                }

                pendingIconFontSizes.Add(fontSize);
                return true;
            }
        }

        static bool TryEnqueueFontUpdate(ImguiWindow overlay, FontHelper.FontLoadDelegate fontLoadDelegate) {
            return overlay != null && overlay.ReplaceFont(fontLoadDelegate);
        }

        float GetScaledFontSize(float fontSize) {
            return overlay != null
                ? overlay.ScaleFontSize(fontSize)
                : MathF.Max(1f, MathF.Round(fontSize));
        }

        unsafe ImFontPtr CreateIconFont(ImFontConfig* fontConfig, float fontSize) {
            if(string.IsNullOrWhiteSpace(iconFontPath) || !File.Exists(iconFontPath)) {
                return default;
            }

            var prevMergeMode = fontConfig->MergeMode;
            var prevPixelSnapH = fontConfig->PixelSnapH;
            var prevGlyphMinAdvanceX = fontConfig->GlyphMinAdvanceX;

            try {
                fontConfig->MergeMode = 0;
                fontConfig->PixelSnapH = 1;
                fontConfig->GlyphMinAdvanceX = 0f;

                ushort[] iconRanges = [0xe000, 0xf8ff, 0];
                fixed(ushort* glyphRanges = iconRanges) {
                    return ImGui.GetIO().Fonts.AddFontFromFileTTF(iconFontPath, GetScaledFontSize(fontSize), new ImFontConfigPtr(fontConfig), (IntPtr)glyphRanges);
                }
            }
            finally {
                fontConfig->MergeMode = prevMergeMode;
                fontConfig->PixelSnapH = prevPixelSnapH;
                fontConfig->GlyphMinAdvanceX = prevGlyphMinAdvanceX;
            }
        }

        unsafe ImFontPtr CreateTextFont(ImFontConfig* fontConfig, float fontSize) {
            if(string.IsNullOrWhiteSpace(textFontPath) || !File.Exists(textFontPath)) {
                return default;
            }

            return ImGui.GetIO().Fonts.AddFontFromFileTTF(textFontPath, GetScaledFontSize(fontSize), new ImFontConfigPtr(fontConfig), ImGui.GetIO().Fonts.GetGlyphRangesCyrillic());
        }

        unsafe void CacheTextFont(int fontSize, ImFontPtr font) {
            lock(textFontsLock) {
                if((IntPtr)font.NativePtr != IntPtr.Zero) {
                    textFonts[fontSize] = font;
                }
            }
        }

        bool TryGetCachedTextFont(int fontSize, out ImFontPtr font) {
            lock(textFontsLock) {
                return textFonts.TryGetValue(fontSize, out font);
            }
        }

        static int NormalizeTextFontSize(float fontSize) {
            return (int)Math.Clamp(MathF.Round(fontSize), MinTextFontAtlasSize, MaxTextFontAtlasSize);
        }


        public unsafe void LoadFonts(ImFontConfig* fontConfig) {
            if(!TryResolveFontPaths(out var textFontPath, out var fontAwesomePath)) {
                return;
            }

            var io = ImGui.GetIO();
            ResetFontCache();
            float[] pendingIconSizes;
            lock(iconFontsLock) {
                pendingIconSizes = pendingIconFontSizes.ToArray();
            }

            io.Fonts.AddFontFromFileTTF(textFontPath, GetScaledFontSize(20f), new ImFontConfigPtr(fontConfig), io.Fonts.GetGlyphRangesCyrillic());

            for(int fontSize = MinTextFontAtlasSize; fontSize <= MaxTextFontAtlasSize; fontSize++) {
                CacheTextFont(fontSize, CreateTextFont(fontConfig, fontSize));
            }

            var prevMergeMode = fontConfig->MergeMode;
            var prevPixelSnapH = fontConfig->PixelSnapH;
            var prevGlyphMinAdvanceX = fontConfig->GlyphMinAdvanceX;

            fontConfig->MergeMode = 1;
            fontConfig->PixelSnapH = 1;
            fontConfig->GlyphMinAdvanceX = GetScaledFontSize(20f);

            ushort[] iconRanges = [0xe000, 0xf8ff, 0];
            fixed(ushort* glyphRanges = iconRanges) {
                var iconRangePtr = (IntPtr)glyphRanges;
                io.Fonts.AddFontFromFileTTF(fontAwesomePath, GetScaledFontSize(20f), new ImFontConfigPtr(fontConfig), iconRangePtr);
            }

            CacheIconFont(NormalizeIconFontSize(SmallIconFontSize), CreateIconFont(fontConfig, SmallIconFontSize));
            CacheIconFont(NormalizeIconFontSize(MediumIconFontSize), CreateIconFont(fontConfig, MediumIconFontSize));
            CacheIconFont(NormalizeIconFontSize(LargeIconFontSize), CreateIconFont(fontConfig, LargeIconFontSize));
            foreach(var pendingIconSize in pendingIconSizes) {
                var normalizedSize = NormalizeIconFontSize(pendingIconSize);
                if(normalizedSize == NormalizeIconFontSize(SmallIconFontSize) ||
                   normalizedSize == NormalizeIconFontSize(MediumIconFontSize) ||
                   normalizedSize == NormalizeIconFontSize(LargeIconFontSize)) {
                    continue;
                }

                CacheIconFont(normalizedSize, CreateIconFont(fontConfig, normalizedSize));
            }

            fontConfig->MergeMode = prevMergeMode;
            fontConfig->PixelSnapH = prevPixelSnapH;
            fontConfig->GlyphMinAdvanceX = prevGlyphMinAdvanceX;
        }

        protected ImFontPtr GetIconFont(IconSize size) {
            return GetIconFont(GetIconFontSize(size));
        }

        protected ImFontPtr GetIconFont(float fontSize) {
            fontSize = NormalizeIconFontSize(fontSize);
            if(TryGetCachedIconFont(fontSize, out var font)) {
                return font;
            }

            EnsureIconFontLoaded(fontSize);
            return TryGetCachedIconFont(fontSize, out font) ? font : default;
        }

        public static IEnumerable<int> GetAvailableTextFontSizesDescending(float maxFontSize, float minFontSize = MinTextFontAtlasSize) {
            int normalizedMax = NormalizeTextFontSize(maxFontSize);
            int normalizedMin = NormalizeTextFontSize(minFontSize);
            for(int fontSize = normalizedMax; fontSize >= normalizedMin; fontSize--) {
                yield return fontSize;
            }
        }

        public static int GetClosestTextFontSize(float fontSize) {
            return NormalizeTextFontSize(fontSize);
        }

        public ImFontPtr GetTextFont(float fontSize) {
            var normalizedFontSize = NormalizeTextFontSize(fontSize);
            return TryGetCachedTextFont(normalizedFontSize, out var font) ? font : default;
        }

        public unsafe float GetResolvedTextFontSize(float fontSize) {
            var font = GetTextFont(fontSize);
            return (IntPtr)font.NativePtr != IntPtr.Zero ? font.FontSize : ImGui.GetFont().FontSize;
        }

        public unsafe Vector2 MeasureText(string text, float fontSize) {
            if(string.IsNullOrEmpty(text)) {
                return Vector2.Zero;
            }

            var font = GetTextFont(fontSize);
            if((IntPtr)font.NativePtr == IntPtr.Zero) {
                return ImGui.CalcTextSize(text);
            }

            ImGui.PushFont(font);
            try {
                return ImGui.CalcTextSize(text);
            }
            finally {
                ImGui.PopFont();
            }
        }

        unsafe void EnsureIconFontLoaded(float fontSize) {
            if(!TryResolveFontPaths(out _, out _)) {
                return;
            }

            if(!TryBeginIconFontLoad(fontSize)) {
                return;
            }

            if(!TryEnqueueFontUpdate(overlay, LoadFonts)) {
                ClearPendingIconFont(fontSize);
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

        public unsafe Vector2 MeasureIconGlyph(string glyph, IconSize size) {
            if(string.IsNullOrEmpty(glyph)) {
                return Vector2.Zero;
            }

            var font = GetIconFont(size);
            if((IntPtr)font.NativePtr == IntPtr.Zero) {
                return ImGui.CalcTextSize(glyph);
            }

            ImGui.PushFont(font);
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

        public unsafe void DrawText(Vector2 position, Color color, string text, ImFontPtr font) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            position = SnapTextPosition(position);
            if((IntPtr)font.NativePtr != IntPtr.Zero) {
                _drawList.AddText(font, font.FontSize, position, col, text);
            }
            else {
                _drawList.AddText(position, col, text);
            }
        }

        public unsafe void DrawText(Vector2 position, Color color, string text, float fontSize) {
            DrawText(position, color, text, GetTextFont(fontSize));
        }

        protected unsafe void AddTextToDrawList(ImDrawListPtr drawList, Vector2 position, Color color, string text, ImFontPtr font) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            position = SnapTextPosition(position);
            if((IntPtr)font.NativePtr != IntPtr.Zero) {
                drawList.AddText(font, font.FontSize, position, col, text);
            }
            else {
                drawList.AddText(position, col, text);
            }
        }

        public unsafe void DrawIconGlyph(Vector2 position, uint color, string glyph, IconSize size) {
            AddIconGlyphToDrawList(_drawList, position, color, glyph, size);
        }

        protected unsafe void AddIconGlyphToDrawList(ImDrawListPtr drawList, Vector2 position, uint color, string glyph, IconSize size) {
            var font = GetIconFont(size);
            if((IntPtr)font.NativePtr != IntPtr.Zero) {
                drawList.AddText(font, font.FontSize, position, color, glyph);
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

        public void DrawImage(string imagePath, string imageName, Vector2 position, Vector2 size) {
            if(string.IsNullOrWhiteSpace(imagePath) || size.X <= 0f || size.Y <= 0f) {
                return;
            }

            var texture = GetOrCreateTexturePointer(imagePath, imageName);
            if(texture == 0) {
                return;
            }

            _drawList.AddImage(texture, position, position + size);
        }

        public static uint ColorRGBA(byte r, byte g, byte b, byte a = 255) {
            return ImGui.ColorConvertFloat4ToU32(new Vector4(r / 255f, g / 255f, b / 255f, a / 255f));
        }

        public static uint ColorRGBA(int r, int g, int b, int a = 255) {
            return ColorRGBA((byte)r, (byte)g, (byte)b, (byte)a);
        }
    }
}
