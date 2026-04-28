using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using ImGuiNET;
using SKFramework;

namespace SK.ImguiForms.Example;

sealed class NotepadOverlayWindow : ImguiOverlay
{
    readonly int targetProcessId;
    readonly string sampleAPath;
    readonly string sampleBPath;

    public NotepadOverlayWindow(Process process, string assetsDirectory) : base("SK.ImguiForms Example Overlay", process, true)
    {
        targetProcessId = process.Id;
        sampleAPath = Path.Combine(assetsDirectory, "sample-a.png");
        sampleBPath = Path.Combine(assetsDirectory, "sample-b.png");
        FPSLimit = 60;
    }

    public int TargetProcessId => targetProcessId;

    protected override void RenderOverlay(ImDrawListPtr drawList)
    {
        var bounds = OverlayBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        Graphics.BeginFrame(drawList);

        var width = bounds.Width;
        var height = bounds.Height;
        var margin = S(18f);
        var headerHeight = S(72f);
        var topPanelHeight = MathF.Min(S(150f), MathF.Max(S(120f), height * 0.28f));
        var bottomTop = margin + headerHeight + topPanelHeight + S(16f);
        var bottomHeight = MathF.Max(S(180f), height - bottomTop - margin);
        var leftWidth = MathF.Max(S(220f), (width - margin * 3f) / 2f);
        var rightWidth = MathF.Max(S(220f), width - leftWidth - margin * 3f);

        Graphics.DrawRectFilled(new RectangleF(0, 0, width, height), Color.FromArgb(24, 8, 10, 18));
        Graphics.DrawRectFilled(new RectangleF(margin, margin, width - margin * 2f, height - margin * 2f), Color.FromArgb(130, 10, 14, 26), S(16f));
        Graphics.DrawRect(new RectangleF(margin, margin, width - margin * 2f, height - margin * 2f), Color.FromArgb(180, 76, 105, 180), S(16f), ImDrawFlags.None, S(2f));

        Graphics.DrawText(new Vector2(margin + S(18f), margin + S(14f)), Color.White, "SK.ImguiForms overlay sample", 26f);
        Graphics.DrawText(new Vector2(margin + S(18f), margin + S(42f)), Color.FromArgb(210, 205, 215, 240), "Rendering above Notepad with SKOverlayGraphics primitives", 17f);
        Graphics.DrawIconGlyph(new Vector2(width - S(68f), margin + S(10f)), SKGraphics.ColorRGBA(255, 208, 96), SKGraphics.GetIconGlyph(0xf303), IconSize.Large);

        var topPanel = new RectangleF(margin + S(18f), margin + headerHeight, width - (margin + S(18f)) * 2f, topPanelHeight);
        var bottomLeftPanel = new RectangleF(margin + S(18f), bottomTop, leftWidth - S(18f), bottomHeight);
        var bottomRightPanel = new RectangleF(bottomLeftPanel.Right + margin, bottomTop, rightWidth - S(18f), bottomHeight);

        DrawImagesAndIconsPanel(topPanel);
        DrawBasicPrimitivesPanel(bottomLeftPanel);
        DrawShapePrimitivesPanel(bottomRightPanel);
    }

    float S(float value) {
        return Graphics.Scale(value);
    }

    Vector2 S(Vector2 value) {
        return Graphics.Scale(value);
    }

    void DrawImagesAndIconsPanel(RectangleF panel)
    {
        DrawPanel(panel, "Images + icons");

        const float labelFontSize = 14f;
        const string firstLabel = "DrawImage(sample-a)";
        const string secondLabel = "DrawImage(sample-b)";
        const string iconLabel = "DrawIconGlyph";

        var imageY = panel.Y + S(62f);
        var imageSize = S(new Vector2(120f, 80f));
        var columnGap = S(28f);
        var labelGap = S(6f);
        var firstLabelSize = Graphics.MeasureText(firstLabel, labelFontSize);
        var secondLabelSize = Graphics.MeasureText(secondLabel, labelFontSize);
        var iconLabelSize = Graphics.MeasureText(iconLabel, labelFontSize);
        var firstColumnWidth = MathF.Max(imageSize.X, firstLabelSize.X);
        var secondColumnWidth = MathF.Max(imageSize.X, secondLabelSize.X);

        var firstImagePosition = new Vector2(panel.X + S(16f), imageY);
        var secondImagePosition = new Vector2(firstImagePosition.X + firstColumnWidth + columnGap, imageY);
        var iconX = secondImagePosition.X + secondColumnWidth + columnGap;
        var labelY = imageY - MathF.Max(firstLabelSize.Y, MathF.Max(secondLabelSize.Y, iconLabelSize.Y)) - labelGap;

        Graphics.DrawText(new Vector2(firstImagePosition.X, labelY), Color.White, firstLabel, labelFontSize);
        Graphics.DrawImage(sampleAPath, "sample-a", firstImagePosition, imageSize);

        Graphics.DrawText(new Vector2(secondImagePosition.X, labelY), Color.White, secondLabel, labelFontSize);
        Graphics.DrawImage(sampleBPath, "sample-b", secondImagePosition, imageSize);

        Graphics.DrawText(new Vector2(iconX, labelY), Color.White, iconLabel, labelFontSize);
        Graphics.DrawIconGlyph(new Vector2(iconX + S(8f), imageY + S(4f)), SKGraphics.ColorRGBA(255, 210, 80), SKGraphics.GetIconGlyph(0xf06d), IconSize.Large);
        Graphics.DrawIconGlyph(new Vector2(iconX + S(48f), imageY + S(4f)), SKGraphics.ColorRGBA(124, 211, 255), SKGraphics.GetIconGlyph(0xf121), IconSize.Large);
        Graphics.DrawIconGlyph(new Vector2(iconX + S(88f), imageY + S(4f)), SKGraphics.ColorRGBA(126, 231, 135), SKGraphics.GetIconGlyph(0xf11b), IconSize.Large);

        var textBox = new RectangleF(iconX, imageY + S(58f), S(150f), S(34f));
        Graphics.DrawRectFilled(textBox, Color.FromArgb(180, 42, 62, 116), S(10f));
        Graphics.DrawTextInBox(textBox, Color.White, "DrawTextInBox");
    }

    void DrawBasicPrimitivesPanel(RectangleF panel)
    {
        DrawPanel(panel, "Basic primitives");

        var origin = new Vector2(panel.X + S(18f), panel.Y + S(52f));

        Graphics.DrawText(origin + S(new Vector2(0f, -18f)), Color.White, "DrawLine");
        Graphics.DrawLine(origin, origin + S(new Vector2(180f, 0f)), Color.FromArgb(255, 255, 99, 132), S(3f));

        var rectPosition = origin + S(new Vector2(0f, 36f));
        Graphics.DrawText(rectPosition + S(new Vector2(0f, -18f)), Color.White, "DrawRect");
        Graphics.DrawRect(rectPosition, S(new Vector2(110f, 56f)), Color.FromArgb(255, 96, 165, 250), S(3f), S(10f));

        var filledRectPosition = rectPosition + S(new Vector2(138f, 0f));
        Graphics.DrawText(filledRectPosition + S(new Vector2(0f, -18f)), Color.White, "DrawRectFilled");
        Graphics.DrawRectFilled(filledRectPosition, S(new Vector2(110f, 56f)), Color.FromArgb(180, 16, 185, 129), S(10f));

        var roundedBox = new RectangleF(panel.X + S(18f), panel.Bottom - S(82f), S(230f), S(52f));
        Graphics.DrawRectFilled(roundedBox, Color.FromArgb(180, 74, 39, 126), S(12f));
        Graphics.DrawRect(roundedBox, Color.FromArgb(255, 180, 144, 255), S(12f), ImDrawFlags.None, S(2f));
        Graphics.DrawTextInBox(roundedBox, Color.White, "Rounded RectangleF overloads");
    }

    void DrawShapePrimitivesPanel(RectangleF panel)
    {
        DrawPanel(panel, "Shape primitives");

        var left = panel.X + S(30f);
        var top = panel.Y + S(58f);

        Graphics.DrawText(new Vector2(left - S(6f), top - S(22f)), Color.White, "DrawCircle / DrawCircleFilled");
        Graphics.DrawCircle(new Vector2(left + S(34f), top + S(26f)), S(24f), Color.FromArgb(255, 251, 191, 36), 0, S(3f));
        Graphics.DrawCircleFilled(new Vector2(left + S(110f), top + S(26f)), S(24f), Color.FromArgb(180, 56, 189, 248));

        var triangleTop = top + S(88f);
        Graphics.DrawText(new Vector2(left - S(6f), triangleTop - S(22f)), Color.White, "DrawTriangle / DrawTriangleFilled");
        Graphics.DrawTriangle(
            new Vector2(left + S(10f), triangleTop + S(46f)),
            new Vector2(left + S(50f), triangleTop),
            new Vector2(left + S(90f), triangleTop + S(46f)),
            Color.FromArgb(255, 248, 113, 113),
            S(3f));
        Graphics.DrawTriangleFilled(
            new Vector2(left + S(124f), triangleTop + S(46f)),
            new Vector2(left + S(164f), triangleTop),
            new Vector2(left + S(204f), triangleTop + S(46f)),
            Color.FromArgb(180, 34, 197, 94));

        var quadTop = triangleTop + S(92f);
        Graphics.DrawText(new Vector2(left - S(6f), quadTop - S(22f)), Color.White, "DrawQuad / DrawQuadFilled");
        Graphics.DrawQuad(
            new Vector2(left + S(6f), quadTop + S(20f)),
            new Vector2(left + S(78f), quadTop),
            new Vector2(left + S(96f), quadTop + S(54f)),
            new Vector2(left + S(20f), quadTop + S(68f)),
            Color.FromArgb(255, 96, 165, 250),
            S(3f));
        Graphics.DrawQuadFilled(
            new Vector2(left + S(128f), quadTop + S(20f)),
            new Vector2(left + S(200f), quadTop),
            new Vector2(left + S(218f), quadTop + S(54f)),
            new Vector2(left + S(142f), quadTop + S(68f)),
            Color.FromArgb(180, 217, 70, 239));
    }

    void DrawPanel(RectangleF panel, string title)
    {
        Graphics.DrawRectFilled(panel, Color.FromArgb(155, 17, 24, 39), S(14f));
        Graphics.DrawRect(panel, Color.FromArgb(220, 104, 126, 210), S(14f), ImDrawFlags.None, S(1.5f));
        Graphics.DrawText(new Vector2(panel.X + S(14f), panel.Y + S(12f)), Color.White, title, 18f);
    }
}
