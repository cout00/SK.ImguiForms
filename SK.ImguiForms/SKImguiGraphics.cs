using System;
using System.Drawing;
using System.IO;
using System.Numerics;
using ImGuiNET;
using SK.ImguiForms;
using SixLabors.ImageSharp.PixelFormats;

namespace SKFramework {
    public sealed class SKImguiGraphics : SKGraphics {
        public SKImguiGraphics(ImguiWindow overlay) : base(overlay) {
        }

        public unsafe void ImguiUIDrawText(ImDrawListPtr drawList, Vector2 position, Color color, string text, float fontSize) {
            ImguiUIDrawText(drawList, position, color, text, GetTextFont(fontSize));
        }

        public unsafe void ImguiUIDrawText(ImDrawListPtr drawList, Vector2 position, Color color, string text, ImFontPtr font) {
            AddTextToDrawList(drawList, position, color, text, font);
        }

        public unsafe void ImguiUIDrawIcon(ImDrawListPtr drawList, Vector2 position, uint color, string glyph, IconSize size) {
            AddIconGlyphToDrawList(drawList, position, color, glyph, size);
        }

        public unsafe void DrawIcon(int iconNumber, IconSize size) {
            DrawIcon(iconNumber, GetResolvedIconFontSize(size));
        }

        public unsafe void DrawIcon(int iconNumber, float fontSize) {
            var glyph = GetIconGlyph(iconNumber);
            var font = GetIconFont(fontSize);
            if((IntPtr)font.NativePtr != IntPtr.Zero) {
                ImGui.PushFont(font);
                ImGui.TextUnformatted(glyph);
                ImGui.PopFont();
                return;
            }

            ImGui.TextUnformatted(glyph);
        }

        public unsafe bool IconButton(string id, int iconNumber, IconSize size, Vector2 buttonSize = default, string? tooltip = null) {
            return IconButton(id, iconNumber, GetResolvedIconFontSize(size), buttonSize, tooltip);
        }

        public unsafe bool IconButton(string id, int iconNumber, float fontSize, Vector2 buttonSize = default, string? tooltip = null) {
            var glyph = GetIconGlyph(iconNumber);
            var font = GetIconFont(fontSize);
            bool pressed;
            if((IntPtr)font.NativePtr != IntPtr.Zero) {
                ImGui.PushFont(font);
                pressed = ImGui.Button($"{glyph}##{id}", buttonSize);
                ImGui.PopFont();
            }
            else {
                pressed = ImGui.Button($"{glyph}##{id}", buttonSize);
            }

            if(!string.IsNullOrWhiteSpace(tooltip) && ImGui.IsItemHovered()) {
                ImGui.SetTooltip(tooltip);
            }

            return pressed;
        }

        public void DrawIcon(int iconNumber, IconSize size, Vector4 color) {
            DrawIcon(iconNumber, GetResolvedIconFontSize(size), color);
        }

        public void DrawIcon(int iconNumber, float fontSize, Vector4 color) {
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            DrawIcon(iconNumber, fontSize);
            ImGui.PopStyleColor();
        }

        public void ImguiUIDrawRect(ImDrawListPtr drawList, RectangleF rect, Color color, float rounding = 0f, ImDrawFlags flags = ImDrawFlags.None, float thickness = 1f) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            Vector2 p_min = new Vector2(rect.X, rect.Y);
            Vector2 p_max = new Vector2(rect.X + rect.Width, rect.Y + rect.Height);
            drawList.AddRect(p_min, p_max, col, rounding, flags, thickness);
        }

        public void ImguiUIDrawRectFilled(ImDrawListPtr drawList, RectangleF rect, Color color, float rounding = 0f, ImDrawFlags flags = ImDrawFlags.None) {
            uint col = ColorRGBA(color.R, color.G, color.B, color.A);
            Vector2 p_min = new Vector2(rect.X, rect.Y);
            Vector2 p_max = new Vector2(rect.X + rect.Width, rect.Y + rect.Height);
            drawList.AddRectFilled(p_min, p_max, col, rounding, flags);
        }

        public void DrawImguiImage(string key, SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> image, Size size) {
            overlay.AddOrGetImagePointer(key, image, false, out var imagePointer);
            if(imagePointer != 0) {
                ImGui.Image(imagePointer, new Vector2(size.Width, size.Height));
            }
        }

        public void DrawImguiImage(string key, SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> image) {
            DrawImguiImage(key, image, new Size(image.Width, image.Height));
        }

        public Vector2 GetImguiImageSize(string imagePath) {
            if(string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) {
                return Vector2.Zero;
            }
            return GetImageSize(imagePath);
        }

        public new nint GetOrCreateTexturePointer(string imagePath, string imageName) {
            return base.GetOrCreateTexturePointer(imagePath, imageName);
        }

        public void ImguiDrawImage(string imagePath, string imageName, Vector2 size) {
            Vector2 displaySize = size;
            if(size.X == 0 || size.Y == 0) {
                var originalSize = GetImageSize(imagePath);
                if(size.X == 0 && size.Y == 0) {
                    displaySize = originalSize;
                }
                else if(size.X == 0) {
                    displaySize.X = (size.Y / originalSize.Y) * originalSize.X;
                }
                else if(size.Y == 0) {
                    displaySize.Y = (size.X / originalSize.X) * originalSize.Y;
                }
            }

            if(HaveTexture(imageName)) {
                var imageId = GetTexturePointer(imageName);
                ImGui.Image(imageId, displaySize);
                return;
            }

            using(var imageStream = File.OpenRead(imagePath)) {
                var image = SixLabors.ImageSharp.Image.Load<Rgba32>(imageStream);
                overlay.AddOrGetImagePointer(imageName, image, false, out var imagePointer);
                ImGui.Image(imagePointer, displaySize);
            }
        }

        public void ImguiUIDrawImage(ImDrawListPtr drawList, string imagePath, string imageName, Vector2 position, Vector2 size) {
            if(string.IsNullOrWhiteSpace(imagePath) || size.X <= 0f || size.Y <= 0f) {
                return;
            }

            var texture = GetOrCreateTexturePointer(imagePath, imageName);
            if(texture == 0) {
                return;
            }

            drawList.AddImage(texture, position, position + size);
        }

        static float GetResolvedIconFontSize(IconSize size) {
            return size switch {
                IconSize.Small => 14f,
                IconSize.Large => 28f,
                _ => 20f,
            };
        }
    }
}
