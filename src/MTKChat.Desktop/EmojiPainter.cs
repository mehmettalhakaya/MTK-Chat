using System.Drawing;
using System.Drawing.Drawing2D;

namespace MTKChat.Desktop;

/// <summary>
/// Original resolution-independent MTK artwork. No installed emoji font, external asset,
/// bitmap cache or network lookup is involved; callers own animation scheduling.
/// </summary>
internal static class EmojiPainter
{
    private static readonly Color Ink = Color.FromArgb(73, 43, 38);
    private static readonly Color Gold = Color.FromArgb(255, 194, 61);
    private static readonly Color LightGold = Color.FromArgb(255, 226, 107);
    private static readonly Color Purple = Color.FromArgb(115, 85, 255);
    private static readonly Color Blue = Color.FromArgb(74, 145, 255);
    private static readonly Color Pink = Color.FromArgb(255, 96, 143);
    private static readonly Color Red = Color.FromArgb(244, 65, 91);

    internal static void Draw(Graphics graphics, RectangleF bounds, EmojiDefinition emoji,
        double seconds, bool animate = true)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(emoji);
        if (bounds.Width <= 0 || bounds.Height <= 0 || !float.IsFinite(bounds.X) ||
            !float.IsFinite(bounds.Y) || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height)) return;
        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var side = Math.Min(bounds.Width, bounds.Height);
            graphics.TranslateTransform(bounds.X + (bounds.Width - side) / 2, bounds.Y + (bounds.Height - side) / 2);
            graphics.ScaleTransform(side / 100f, side / 100f);
            graphics.SetClip(new RectangleF(0, 0, 100, 100), CombineMode.Intersect);
            // A bounded phase avoids precision loss when a long-lived process uses a stopwatch.
            var moving = animate && emoji.Animated;
            var time = moving && double.IsFinite(seconds) ? (seconds % 3.6 + 3.6) % 3.6 : 0;
            var motion = moving ? (float)Math.Sin(time * Math.PI * 2 / 3.6) : 0;
            var symbol = emoji.Symbol.Replace("\uFE0F", string.Empty, StringComparison.Ordinal);
            if (emoji.Category == EmojiCategory.Faces) Face(graphics, symbol, time, motion, moving);
            else if (emoji.Category == EmojiCategory.Love) Love(graphics, symbol, time, motion, moving);
            else if (emoji.Category == EmojiCategory.Gestures) Gesture(graphics, symbol, time, motion, moving);
            else if (emoji.Category == EmojiCategory.Celebration) Celebration(graphics, symbol, time, motion, moving);
            else if (emoji.Category == EmojiCategory.Nature) Nature(graphics, symbol, time, motion, moving);
            else Object(graphics, symbol, time, motion, moving);
        }
        finally { graphics.Restore(state); }
    }

    private static void Face(Graphics g, string symbol, double time, float motion, bool moving)
    {
        var upside = symbol == "🙃";
        if (upside) { g.TranslateTransform(50, 50); g.RotateTransform(180); g.TranslateTransform(-50, -50); }
        if (symbol == "🤣") { g.TranslateTransform(50, 50); g.RotateTransform(-15 + motion * 4); g.TranslateTransform(-50, -50); }
        var body = new RectangleF(11, 11, 78, 78);
        var angry = symbol == "😡";
        GradientEllipse(g, body, angry ? Color.FromArgb(255, 134, 89) : LightGold,
            angry ? Color.FromArgb(236, 70, 52) : Gold);
        Ellipse(g, Color.FromArgb(52, Color.White), 25, 18, 25, 9);
        var blushAlpha = 82 + (int)(motion * 12);
        if (symbol is "😊" or "🥰" or "🤗" or "😘" or "😅")
        {
            Ellipse(g, Color.FromArgb(blushAlpha, 244, 107, 99), 18, 48, 16, 9);
            Ellipse(g, Color.FromArgb(blushAlpha, 244, 107, 99), 66, 48, 16, 9);
        }
        var blink = moving ? (float)Bump(time, 2.55, .23) : 0;
        var openEyes = Math.Max(.08f, 1 - blink);
        switch (symbol)
        {
            case "😍":
                Heart(g, new RectangleF(23, 29, 25 + motion, 24 + motion), Red);
                Heart(g, new RectangleF(53, 29, 25 + motion, 24 + motion), Red);
                OpenSmile(g, 34, 56, 32, 20 + motion * 2, false);
                break;
            case "🤩":
                Star(g, 35, 40, 13 + motion, Color.FromArgb(242, 136, 37));
                Star(g, 65, 40, 13 + motion, Color.FromArgb(242, 136, 37));
                OpenSmile(g, 34, 57, 32, 19 + motion, true);
                break;
            case "😎":
                Round(g, Color.FromArgb(39, 42, 70), 19, 30, 28, 22, 7);
                Round(g, Color.FromArgb(39, 42, 70), 53, 30, 28, 22, 7);
                Line(g, Color.FromArgb(39, 42, 70), 5, 43, 34, 57, 34);
                Line(g, Color.FromArgb(39, 42, 70), 4, 12, 32, 23, 35);
                Line(g, Color.FromArgb(39, 42, 70), 4, 77, 35, 88, 32);
                Line(g, Color.FromArgb(95, 174, 223), 2.5f, 25, 36, 34, 43);
                Line(g, Color.FromArgb(95, 174, 223), 2.5f, 59, 36, 68, 43);
                Smile(g, 35, 61, 30, 10 + motion, 4);
                break;
            case "😉":
            case "😜":
                Eye(g, 35, 40, openEyes);
                Curve(g, Ink, 4, new PointF(57, 42 - motion), new PointF(64, 35 - motion), new PointF(72, 42 - motion));
                if (symbol == "😜") { OpenSmile(g, 33, 57, 34, 19, false); Tongue(g, 49, 65, 14, 20 + motion * 2); }
                else Smile(g, 34, 57, 33, 15 + motion, 4);
                break;
            case "😴":
                ClosedEye(g, 35, 42, false); ClosedEye(g, 65, 42, false);
                Ellipse(g, Ink, 44, 61, 12, 10 + motion * 2);
                // Floating quiet bubbles communicate sleep without font-dependent Z glyphs.
                Ellipse(g, Color.FromArgb(150, 140, 181, 255), 75, 19 - motion * 2, 9, 9);
                Ellipse(g, Color.FromArgb(190, 163, 193, 255), 85, 8 - motion * 2, 7, 7);
                break;
            case "😮":
            case "😲":
                Eye(g, 35, 39, openEyes, 5.5f); Eye(g, 65, 39, openEyes, 5.5f);
                Brows(g, 29, 27 - motion * 2, 15, -2); Brows(g, 57, 25 - motion * 2, 15, 2);
                Ellipse(g, Ink, symbol == "😲" ? 39 : 43, 55,
                    symbol == "😲" ? 22 : 14, 23 + motion * 3);
                if (symbol == "😲") Round(g, Color.White, 43, 57, 14, 5, 2);
                break;
            case "😢":
            case "😭":
                if (symbol == "😭") { ClosedEye(g, 35, 41, true); ClosedEye(g, 65, 41, true); }
                else { Eye(g, 35, 40, openEyes); Eye(g, 65, 40, openEyes); }
                Brows(g, 25, 30, 17, -5); Brows(g, 58, 25, 17, 5);
                Curve(g, Ink, 4, new PointF(36, 70), new PointF(50, 58 - motion), new PointF(64, 70));
                if (symbol == "😭")
                {
                    Round(g, Color.FromArgb(102, 194, 255), 27, 46, 9, 29 + motion * 3, 4);
                    Round(g, Color.FromArgb(102, 194, 255), 64, 46, 9, 29 - motion * 3, 4);
                }
                else Drop(g, 66, 52 + motion * 4, 9, 16, Color.FromArgb(96, 186, 255));
                break;
            case "😡":
                Eye(g, 35, 43, openEyes); Eye(g, 65, 43, openEyes);
                Line(g, Ink, 5, 25, 31 - motion, 43, 37 - motion);
                Line(g, Ink, 5, 57, 37 - motion, 75, 31 - motion);
                Curve(g, Ink, 4, new PointF(36, 70), new PointF(50, 58), new PointF(64, 70));
                break;
            case "🤔":
                Eye(g, 34, 43, openEyes); Eye(g, 65, 39, openEyes);
                Brows(g, 26, 28 - motion * 2, 17, -3); Brows(g, 57, 27 + motion, 17, 2);
                Line(g, Ink, 3.5f, 45, 63, 60, 61);
                Round(g, Color.FromArgb(244, 172, 58), 56, 62, 16, 23, 7);
                Line(g, LightGold, 8, 59, 65, 40, 70 - motion);
                break;
            case "🤫":
                Eye(g, 35, 39, openEyes); Eye(g, 65, 39, openEyes);
                Ellipse(g, Ink, 45, 56, 10, 11);
                Round(g, Color.FromArgb(240, 169, 56), 44, 49, 12, 39, 6);
                Line(g, LightGold, 4, 48, 55, 48, 79);
                break;
            case "😘":
                Eye(g, 35, 40, openEyes); ClosedEye(g, 65, 40, true);
                Curve(g, Ink, 3.5f, new PointF(44, 57), new PointF(54, 61), new PointF(45, 65), new PointF(53, 69));
                Heart(g, new RectangleF(69 + motion * 3, 53 - motion * 2, 22, 20), Red);
                break;
            case "😬":
                Eye(g, 35, 40, openEyes); Eye(g, 65, 40, openEyes);
                Round(g, Ink, 27, 56, 46, 21 + motion, 7);
                Round(g, Color.FromArgb(255, 251, 228), 30, 59, 40, 15 + motion, 4);
                for (var x = 39; x < 68; x += 10) Line(g, Color.FromArgb(197, 177, 144), 1.5f, x, 59, x, 73 + motion);
                Line(g, Color.FromArgb(197, 177, 144), 1.5f, 31, 66, 69, 66);
                break;
            case "😋":
                ClosedEye(g, 35, 40, true); ClosedEye(g, 65, 40, true);
                Smile(g, 30, 55, 39, 17, 4); Tongue(g, 54, 62, 14, 16 + motion * 2);
                break;
            default:
                if (symbol is "😄" or "😆" or "😂" or "🤣" or "😊" or "🥰" or "🤗")
                { ClosedEye(g, 35, 40 + motion, true); ClosedEye(g, 65, 40 + motion, true); }
                else { Eye(g, 35, 40, openEyes); Eye(g, 65, 40, openEyes); }
                if (symbol is "😀" or "😃" or "😄" or "😆" or "😂" or "🤣" or "😁" or "🥳" or "😅")
                    OpenSmile(g, 29, 54, 42, symbol is "😆" or "😂" or "🤣" ? 26 + motion * 2 : 22 + motion, true);
                else Smile(g, 33, 57, 34, symbol == "🙂" ? 10 + motion : 15 + motion, 4);
                if (symbol is "😂" or "🤣")
                {
                    Drop(g, 13, 43 + motion * 2, 13, 21, Color.FromArgb(88, 187, 255));
                    Drop(g, 75, 43 - motion * 2, 13, 21, Color.FromArgb(88, 187, 255));
                }
                if (symbol == "😅") Drop(g, 75, 18 + motion * 3, 13, 24, Color.FromArgb(88, 187, 255));
                if (symbol == "🥰")
                {
                    Heart(g, new RectangleF(2, 21 + motion, 22, 20), Red);
                    Heart(g, new RectangleF(75, 13 - motion, 22, 20), Red);
                    Heart(g, new RectangleF(75, 70 + motion, 20, 18), Red);
                }
                if (symbol == "🤗")
                {
                    MiniHand(g, 12, 60 + motion, -24); MiniHand(g, 65, 60 + motion, 24);
                }
                if (symbol == "🥳")
                {
                    Polygon(g, Purple, new PointF(12, 25), new PointF(24, 2), new PointF(44, 22));
                    Line(g, Pink, 3, 18, 16, 34, 21);
                    Polygon(g, Color.FromArgb(255, 142, 115), new PointF(64, 63), new PointF(91, 62 + motion), new PointF(89, 70 + motion));
                    Spark(g, 86, 18, 6 + motion, Purple);
                }
                break;
        }
    }

    private static void Love(Graphics g, string symbol, double time, float motion, bool moving)
    {
        var color = symbol switch
        {
            "💜" => Color.FromArgb(158, 104, 245), "💙" => Color.FromArgb(73, 153, 255),
            "💚" => Color.FromArgb(76, 202, 128), "💛" => Color.FromArgb(255, 204, 66),
            "🧡" => Color.FromArgb(255, 144, 60), "🩷" => Color.FromArgb(255, 139, 183), _ => Red
        };
        var beat = moving ? (float)(Bump(time, .6, .22) + .6 * Bump(time, 1.0, .18)) : 0;
        var scale = 1 + beat * .065f;
        if (symbol == "💕")
        {
            Heart(g, new RectangleF(9 - motion, 33 + motion, 53, 49), Pink);
            Heart(g, new RectangleF(49 + motion, 13 - motion, 41, 39), Red);
        }
        else
        {
            var heartBounds = new RectangleF(50 - 39 * scale, 50 - 36 * scale, 78 * scale, 72 * scale);
            if (symbol == "💔")
            {
                using var heart = HeartPath(heartBounds);
                var clip = g.Save();
                try
                {
                    g.SetClip(heart);
                    GradientPath(g, heart, heartBounds, Lighten(color, 35), color);
                    using var gap = new GraphicsPath();
                    gap.AddLines(new[] { new PointF(53, 13), new PointF(44, 41), new PointF(57, 47), new PointF(44, 68), new PointF(50, 87) });
                    using var pen = new Pen(Color.FromArgb(36, 30, 57), 6) { LineJoin = LineJoin.Bevel };
                    g.DrawPath(pen, gap);
                }
                finally { g.Restore(clip); }
            }
            else Heart(g, heartBounds, color);
            if (symbol == "💖")
            { Spark(g, 82, 23, 11 + motion * 2, LightGold); Spark(g, 20, 67, 8 - motion, LightGold); }
        }
    }

    private static void Gesture(Graphics g, string symbol, double time, float motion, bool moving)
    {
        var skin = Color.FromArgb(255, 199, 112);
        var shade = Color.FromArgb(232, 153, 69);
        var state = g.Save();
        try
        {
            if (symbol == "👎") { g.TranslateTransform(50, 50); g.RotateTransform(180); g.TranslateTransform(-50, -50); }
            if (symbol is "👍" or "👎")
            {
                if (symbol == "👍") { g.TranslateTransform(50, 72); g.RotateTransform(motion * -4); g.TranslateTransform(-50, -72); }
                using var p = new GraphicsPath();
                p.AddBezier(34, 48, 40, 42, 43, 29, 45, 17);
                p.AddBezier(45, 17, 47, 8, 57, 10, 60, 20);
                p.AddBezier(60, 20, 64, 28, 58, 39, 58, 44);
                p.AddLine(58, 44, 79, 44);
                p.AddBezier(79, 44, 92, 45, 87, 59, 84, 63);
                p.AddBezier(84, 63, 85, 70, 80, 78, 74, 80);
                p.AddLine(74, 80, 34, 80); p.CloseFigure();
                GradientPath(g, p, new RectangleF(30, 10, 60, 73), skin, shade);
                Round(g, Blue, 17, 47, 18, 36, 5);
                Ellipse(g, Color.FromArgb(175, Color.White), 23, 73, 5, 5);
                Line(g, shade, 2, 69, 53, 84, 53);
                Line(g, shade, 2, 67, 64, 81, 64);
                return;
            }
            switch (symbol)
            {
                case "👋":
                    g.TranslateTransform(50, 58); g.RotateTransform(-14 + motion * 10); g.TranslateTransform(-50, -58);
                    Palm(g, skin, shade);
                    Curve(g, Blue, 3, new PointF(10, 25), new PointF(4, 42), new PointF(9, 57));
                    Curve(g, Blue, 3, new PointF(88, 18), new PointF(97, 36), new PointF(92, 51));
                    break;
                case "✌":
                    Round(g, skin, 29, 47, 44, 39, 15);
                    Line(g, skin, 15, 42, 50, 29, 16);
                    Line(g, skin, 15, 54, 50, 67, 16);
                    Line(g, shade, 2, 46, 54, 36, 27);
                    Round(g, shade, 57, 48, 16, 21, 7);
                    Round(g, LightGold, 25, 55, 22, 15, 7);
                    Round(g, Purple, 34, 80, 35, 11, 4);
                    break;
                case "👌":
                    Round(g, skin, 32, 48, 44, 38, 13);
                    Line(g, skin, 11, 48, 49, 52, 18);
                    Line(g, skin, 11, 61, 49, 68, 22);
                    Line(g, skin, 10, 71, 53, 82, 35);
                    using (var ring = new GraphicsPath(FillMode.Alternate))
                    {
                        ring.AddEllipse(16, 35, 39, 40); ring.AddEllipse(27, 46, 17, 18);
                        using var brush = new SolidBrush(skin); g.FillPath(brush, ring);
                    }
                    Round(g, Purple, 37, 80, 33, 11, 4);
                    break;
                case "👏":
                    MiniHand(g, 15 + motion * 2, 32, -25);
                    MiniHand(g, 47 - motion * 2, 25, -25);
                    Spark(g, 35, 12, 6 + Math.Max(0, motion) * 2, Gold);
                    Line(g, Gold, 3, 11, 28, 4, 23); Line(g, Gold, 3, 65, 9, 69, 3);
                    break;
                case "🙏":
                    Polygon(g, skin, new PointF(48, 14 + motion), new PointF(48, 66), new PointF(22, 83), new PointF(20, 67), new PointF(37, 54));
                    Polygon(g, LightGold, new PointF(52, 14 + motion), new PointF(52, 66), new PointF(78, 83), new PointF(80, 67), new PointF(63, 54));
                    Line(g, shade, 2, 50, 21, 50, 64);
                    Polygon(g, Blue, new PointF(16, 66), new PointF(32, 79), new PointF(23, 91), new PointF(7, 77));
                    Polygon(g, Purple, new PointF(84, 66), new PointF(68, 79), new PointF(77, 91), new PointF(93, 77));
                    Spark(g, 22, 25, 6 + motion, Gold); Spark(g, 78, 25, 6 - motion, Gold);
                    break;
                case "🤝":
                    Polygon(g, Blue, new PointF(4, 36), new PointF(24, 27), new PointF(38, 58), new PointF(18, 69));
                    Polygon(g, Purple, new PointF(96, 36), new PointF(76, 27), new PointF(62, 58), new PointF(82, 69));
                    Round(g, skin, 22, 34 + motion, 56, 31, 12);
                    Line(g, shade, 8, 35, 55 + motion, 57, 70 + motion);
                    Line(g, shade, 7, 45, 51 + motion, 67, 64 + motion);
                    Line(g, LightGold, 7, 30, 40 + motion, 47, 44 + motion);
                    Line(g, skin, 8, 69, 37 + motion, 55, 47 + motion);
                    break;
            }
        }
        finally { g.Restore(state); }
    }

    private static void Celebration(Graphics g, string symbol, double time, float motion, bool moving)
    {
        switch (symbol)
        {
            case "🎉":
                Polygon(g, Purple, new PointF(13, 88), new PointF(30, 40), new PointF(63, 72));
                Line(g, Pink, 7, 24, 62, 42, 80);
                Line(g, Blue, 7, 29, 46, 58, 74);
                Confetti(g, time, moving);
                break;
            case "🎊":
                GradientEllipse(g, new RectangleF(18, 10, 64, 38), Pink, Purple);
                Polygon(g, Blue, new PointF(50, 29), new PointF(18, 48), new PointF(23, 18));
                Polygon(g, Gold, new PointF(50, 29), new PointF(82, 48), new PointF(77, 18));
                Line(g, Purple, 2, 50, 3, 50, 13);
                Confetti(g, time, moving, 26);
                break;
            case "🎂":
                Round(g, Pink, 17, 51, 66, 32, 7);
                Round(g, Color.FromArgb(255, 236, 209), 17, 45, 66, 17, 7);
                Line(g, Color.FromArgb(252, 180, 188), 4, 22, 70, 78, 70);
                Line(g, Color.FromArgb(213, 202, 255), 5, 11, 85, 89, 85);
                for (var i = 0; i < 3; i++)
                {
                    var x = 30 + i * 20;
                    Round(g, i % 2 == 0 ? Purple : Blue, x, 28, 6, 21, 2);
                    Flame(g, new RectangleF(x - 3 + motion, 11 - motion, 12, 18 + motion), Gold, Color.FromArgb(255, 124, 61));
                }
                break;
            case "🎁":
                Round(g, Blue, 18, 40, 64, 43, 5);
                Round(g, Purple, 14, 31, 72, 17, 5);
                Rectangle(g, Pink, 44, 31, 12, 52);
                using (var bow = new GraphicsPath())
                {
                    bow.AddBezier(50, 31, 16, 34, 25, 3, 42, 17);
                    bow.AddLine(42, 17, 50, 31); bow.CloseFigure();
                    bow.StartFigure(); bow.AddBezier(50, 31, 84, 34, 75, 3, 58, 17);
                    bow.AddLine(58, 17, 50, 31); bow.CloseFigure();
                    using var brush = new SolidBrush(Pink); g.FillPath(brush, bow);
                }
                break;
            case "🎈":
                GradientEllipse(g, new RectangleF(23 + motion * 2, 9 + motion, 55, 62), Lighten(Red, 45), Red);
                Ellipse(g, Color.FromArgb(130, Color.White), 33 + motion * 2, 18 + motion, 10, 19);
                Polygon(g, Red, new PointF(47 + motion * 2, 67 + motion), new PointF(53 + motion * 2, 67 + motion), new PointF(56 + motion * 2, 75 + motion), new PointF(44 + motion * 2, 75 + motion));
                Curve(g, Color.FromArgb(198, 190, 226), 2, new PointF(50 + motion * 2, 74 + motion), new PointF(45, 82), new PointF(55, 90), new PointF(49, 97));
                break;
            case "🏆":
                using (var cup = new GraphicsPath())
                {
                    cup.AddLine(27, 18, 73, 18); cup.AddBezier(73, 18, 75, 66, 25, 66, 27, 18); cup.CloseFigure();
                    GradientPath(g, cup, new RectangleF(25, 18, 50, 47), LightGold, Color.FromArgb(236, 156, 44));
                }
                Curve(g, Gold, 7, new PointF(27, 27), new PointF(8, 25), new PointF(14, 50), new PointF(36, 52));
                Curve(g, Gold, 7, new PointF(73, 27), new PointF(92, 25), new PointF(86, 50), new PointF(64, 52));
                Round(g, Gold, 45, 58, 10, 19, 3); Round(g, Purple, 30, 76, 40, 12, 3);
                Star(g, 50, 36, 10, Color.FromArgb(255, 246, 185));
                break;
        }
    }

    private static void Nature(Graphics g, string symbol, double time, float motion, bool moving)
    {
        switch (symbol)
        {
            case "✨":
                Spark(g, 56, 45, 29 + motion * 3, LightGold);
                Spark(g, 24, 23, 12 - motion * 2, Gold);
                Spark(g, 23, 76, 15 - motion * 2, Gold);
                break;
            case "⭐":
                Star(g, 50, 50, 40 + motion * 1.3f, Gold);
                Star(g, 43, 40, 16, Color.FromArgb(75, Color.White));
                break;
            case "🔥":
                Flame(g, new RectangleF(19, 7 - motion, 62, 82 + motion), Color.FromArgb(255, 177, 45), Color.FromArgb(247, 91, 51));
                Flame(g, new RectangleF(36, 43 + motion, 29, 44 - motion), LightGold, Color.FromArgb(255, 186, 62));
                break;
            case "🌈":
                var colors = new[] { Red, Color.FromArgb(255, 163, 64), Gold, Color.FromArgb(88, 208, 140), Blue, Purple };
                for (var i = 0; i < colors.Length; i++)
                {
                    using var pen = new Pen(colors[i], 6.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawArc(pen, 8 + i * 6, 14 + i * 6, 84 - i * 12, 84 - i * 12, 180, 180);
                }
                Cloud(g, 5, 63); Cloud(g, 70, 63);
                break;
            case "☀":
                for (var i = 0; i < 8; i++)
                {
                    var angle = Math.PI * i / 4 + motion * .025;
                    Line(g, Gold, 5, 50 + (float)Math.Cos(angle) * 32, 50 + (float)Math.Sin(angle) * 32,
                        50 + (float)Math.Cos(angle) * (40 + motion), 50 + (float)Math.Sin(angle) * (40 + motion));
                }
                GradientEllipse(g, new RectangleF(25, 25, 50, 50), LightGold, Gold);
                Eye(g, 40, 46, 1, 2.5f); Eye(g, 60, 46, 1, 2.5f); Smile(g, 40, 56, 20, 8, 3);
                break;
            case "🌙":
                using (var crescent = new GraphicsPath())
                {
                    crescent.AddBezier(66, 10, 3, 3, 3, 89, 66, 90);
                    crescent.AddBezier(66, 90, 30, 71, 30, 29, 66, 10); crescent.CloseFigure();
                    GradientPath(g, crescent, new RectangleF(11, 8, 57, 83), LightGold, Gold);
                }
                Spark(g, 75, 29, 10, Color.FromArgb(211, 199, 255)); Spark(g, 79, 65, 6, Color.FromArgb(211, 199, 255));
                break;
            case "🌸":
                for (var i = 0; i < 5; i++)
                {
                    var saved = g.Save(); g.TranslateTransform(50, 50); g.RotateTransform(i * 72);
                    GradientEllipse(g, new RectangleF(-15, -39, 30, 43), Color.FromArgb(255, 187, 215), Pink); g.Restore(saved);
                }
                Ellipse(g, LightGold, 38, 38, 24, 24);
                for (var i = 0; i < 5; i++) Ellipse(g, Gold, 47 + (float)Math.Cos(i * Math.PI * 2 / 5) * 6, 47 + (float)Math.Sin(i * Math.PI * 2 / 5) * 6, 5, 5);
                break;
            case "🌿":
                Curve(g, Color.FromArgb(76, 163, 95), 4, new PointF(29, 89), new PointF(43, 57), new PointF(61, 19));
                Leaf(g, new PointF(44, 59), new PointF(16, 29), Color.FromArgb(95, 203, 132));
                Leaf(g, new PointF(51, 45), new PointF(81, 22), Color.FromArgb(71, 184, 116));
                Leaf(g, new PointF(37, 74), new PointF(15, 55), Color.FromArgb(68, 171, 105));
                Leaf(g, new PointF(45, 65), new PointF(77, 45), Color.FromArgb(100, 209, 134));
                Leaf(g, new PointF(58, 28), new PointF(62, 5), Color.FromArgb(113, 216, 144));
                break;
        }
    }

    private static void Object(Graphics g, string symbol, double time, float motion, bool moving)
    {
        switch (symbol)
        {
            case "☕":
                Curve(g, Color.FromArgb(210, 220, 242), 7, new PointF(73, 42), new PointF(96, 38), new PointF(96, 65), new PointF(72, 65));
                Round(g, Color.FromArgb(232, 237, 255), 18, 36, 59, 42, 12);
                Ellipse(g, Color.FromArgb(97, 58, 46), 20, 35, 55, 13);
                Ellipse(g, Color.FromArgb(152, 106, 78), 24, 36, 47, 8);
                Line(g, Color.FromArgb(175, 185, 221), 5, 12, 83, 85, 83);
                for (var i = 0; i < 3; i++)
                    Curve(g, Color.FromArgb(150, 218, 224, 255), 3,
                        new PointF(32 + i * 15, 27), new PointF(28 + i * 15 + motion * 2, 18), new PointF(34 + i * 15 - motion, 9));
                break;
            case "💡":
                GradientEllipse(g, new RectangleF(25, 13, 50, 54), LightGold, Gold);
                Polygon(g, Gold, new PointF(35, 51), new PointF(65, 51), new PointF(59, 76), new PointF(41, 76));
                Round(g, Color.FromArgb(157, 167, 203), 39, 70, 22, 15, 5);
                Line(g, Color.FromArgb(222, 228, 245), 2.5f, 41, 75, 59, 75);
                Line(g, Color.FromArgb(222, 228, 245), 2.5f, 41, 80, 59, 80);
                Curve(g, Color.FromArgb(213, 142, 45), 2, new PointF(44, 63), new PointF(38, 39), new PointF(50, 46), new PointF(62, 39), new PointF(56, 63));
                for (var i = 0; i < 5; i++)
                {
                    var a = Math.PI + Math.PI * i / 4;
                    Line(g, Color.FromArgb(170 + (int)(motion * 45), Gold), 3,
                        50 + (float)Math.Cos(a) * 33, 39 + (float)Math.Sin(a) * 33,
                        50 + (float)Math.Cos(a) * (41 + motion), 39 + (float)Math.Sin(a) * (41 + motion));
                }
                break;
            case "🚀":
                var rocket = g.Save();
                g.TranslateTransform(50, 50); g.RotateTransform(35); g.TranslateTransform(-50, -50);
                Flame(g, new RectangleF(38, 65, 24, 29 + motion * 4), LightGold, Color.FromArgb(255, 130, 69));
                Polygon(g, Purple, new PointF(35, 48), new PointF(20, 72), new PointF(40, 68));
                Polygon(g, Purple, new PointF(65, 48), new PointF(80, 72), new PointF(60, 68));
                using (var shell = new GraphicsPath())
                {
                    shell.AddBezier(50, 7, 77, 29, 67, 57, 63, 71);
                    shell.AddLine(63, 71, 37, 71); shell.AddBezier(37, 71, 33, 57, 23, 29, 50, 7); shell.CloseFigure();
                    GradientPath(g, shell, new RectangleF(31, 7, 38, 64), Color.FromArgb(246, 244, 255), Color.FromArgb(186, 199, 236));
                }
                Ellipse(g, Purple, 38, 29, 24, 24); Ellipse(g, Blue, 42, 33, 16, 16);
                Line(g, Color.FromArgb(180, Color.White), 3, 44, 36, 49, 41);
                g.Restore(rocket);
                break;
            case "💬":
                RoundGradient(g, new RectangleF(10, 17, 80, 58), 18, Blue, Purple);
                Polygon(g, Purple, new PointF(28, 69), new PointF(24, 89), new PointF(48, 73));
                for (var i = 0; i < 3; i++)
                {
                    var bump = moving ? (float)Bump(time, .5 + i * .28, .26) * 4 : 0;
                    Ellipse(g, Color.FromArgb(246, 243, 255), 27 + i * 19, 41 - bump, 9, 9);
                }
                break;
            case "🎵":
                using (var note = new GraphicsPath())
                {
                    note.AddLines(new[] { new PointF(36, 26), new PointF(80, 14), new PointF(80, 68), new PointF(72, 68), new PointF(72, 35), new PointF(44, 43), new PointF(44, 78), new PointF(36, 78) }); note.CloseFigure();
                    GradientPath(g, note, new RectangleF(36, 14, 44, 64), Blue, Purple);
                }
                Ellipse(g, Purple, 19, 67, 26, 18); Ellipse(g, Purple, 56, 58, 25, 18);
                break;
            case "✅":
                RoundGradient(g, new RectangleF(14, 14, 72, 72), 17, Color.FromArgb(109, 225, 166), Color.FromArgb(42, 169, 116));
                Line(g, Color.White, 9, 31, 50, 45, 64); Line(g, Color.White, 9, 45, 64, 71, 35);
                break;
            case "📎":
                using (var clip = new GraphicsPath())
                {
                    clip.AddBezier(37, 67, 17, 49, 45, 28, 59, 15);
                    clip.AddBezier(59, 15, 79, -3, 100, 20, 80, 39);
                    clip.AddLine(80, 39, 38, 80); clip.AddBezier(38, 80, 21, 95, 2, 75, 18, 59);
                    clip.AddLine(18, 59, 61, 18); clip.AddBezier(61, 18, 69, 10, 81, 22, 73, 30);
                    clip.AddLine(73, 30, 37, 65);
                    using var pen = new Pen(Color.FromArgb(198, 213, 248), 7) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    g.DrawPath(pen, clip);
                }
                break;
            case "💻":
                Round(g, Color.FromArgb(176, 190, 229), 15, 17, 70, 54, 5);
                RoundGradient(g, new RectangleF(19, 21, 62, 45), 3, Color.FromArgb(70, 82, 147), Color.FromArgb(36, 37, 77));
                Line(g, Blue, 3, 27, 32, 45, 32); Line(g, Pink, 3, 32, 41, 60, 41);
                Line(g, Color.FromArgb(118, 229, 192), 3, 38, 50, 70, 50);
                Polygon(g, Color.FromArgb(199, 210, 241), new PointF(15, 71), new PointF(85, 71), new PointF(95, 84), new PointF(5, 84));
                Round(g, Color.FromArgb(142, 160, 205), 5, 82, 90, 6, 2);
                Round(g, Color.FromArgb(142, 160, 205), 39, 74, 22, 5, 2);
                break;
        }
    }

    private static void Eye(Graphics g, float x, float y, float openness, float width = 4.7f)
    {
        if (openness < .22f) { Line(g, Ink, 3, x - width, y, x + width, y); return; }
        Ellipse(g, Ink, x - width, y - 6 * openness, width * 2, 12 * openness);
        if (openness > .6f) Ellipse(g, Color.FromArgb(205, Color.White), x - 1.8f, y - 3.8f * openness, 2.5f, 2.5f);
    }

    private static void ClosedEye(Graphics g, float x, float y, bool happy) =>
        Curve(g, Ink, 3.8f, new PointF(x - 7, y + (happy ? 2 : -2)), new PointF(x, y + (happy ? -5 : 5)), new PointF(x + 7, y + (happy ? 2 : -2)));

    private static void Brows(Graphics g, float x, float y, float width, float rise) =>
        Curve(g, Ink, 3, new PointF(x, y), new PointF(x + width / 2, y + rise), new PointF(x + width, y + rise));

    private static void Smile(Graphics g, float x, float y, float width, float depth, float stroke) =>
        Curve(g, Ink, stroke, new PointF(x, y), new PointF(x + width / 2, y + depth), new PointF(x + width, y));

    private static void OpenSmile(Graphics g, float x, float y, float width, float height, bool teeth)
    {
        using var mouth = new GraphicsPath();
        mouth.AddBezier(x, y, x + width * .3f, y + 3, x + width * .7f, y + 3, x + width, y);
        mouth.AddBezier(x + width, y, x + width * .95f, y + height * 1.25f, x + width * .05f, y + height * 1.25f, x, y); mouth.CloseFigure();
        using (var brush = new SolidBrush(Ink)) g.FillPath(brush, mouth);
        var clip = g.Save();
        try
        {
            g.SetClip(mouth);
            if (teeth) Round(g, Color.FromArgb(255, 251, 232), x + 2, y - 1, width - 4, 9, 3);
            Ellipse(g, Pink, x + width * .2f, y + height * .65f, width * .65f, height * .7f);
        }
        finally { g.Restore(clip); }
    }

    private static void Tongue(Graphics g, float x, float y, float width, float height)
    {
        Round(g, Pink, x, y, width, height, width / 2);
        Line(g, Color.FromArgb(210, 70, 100), 1.7f, x + width / 2, y + 3, x + width / 2, y + height - 5);
    }

    private static void Palm(Graphics g, Color skin, Color shade)
    {
        Round(g, skin, 27, 44, 47, 42, 16);
        Line(g, skin, 11, 34, 51, 28, 23); Line(g, skin, 11, 46, 48, 43, 13);
        Line(g, skin, 11, 58, 49, 58, 14); Line(g, skin, 10, 69, 53, 72, 25);
        Line(g, shade, 2, 39, 47, 37, 26); Line(g, shade, 2, 51, 44, 51, 22);
        Line(g, shade, 2, 63, 47, 64, 25);
        Line(g, skin, 14, 31, 65, 17, 47);
        Curve(g, shade, 2, new PointF(37, 67), new PointF(46, 60), new PointF(59, 62));
        Round(g, Purple, 34, 81, 34, 11, 4);
    }

    private static void MiniHand(Graphics g, float x, float y, float angle)
    {
        var state = g.Save();
        try
        {
            g.TranslateTransform(x + 16, y + 20); g.RotateTransform(angle); g.TranslateTransform(-16, -20);
            var skin = Color.FromArgb(255, 197, 108);
            Round(g, skin, 5, 16, 24, 25, 10);
            for (var i = 0; i < 4; i++) Line(g, skin, 5, 8 + i * 6, 22, 7 + i * 6, i is 1 or 2 ? 3 : 7);
            Line(g, skin, 7, 8, 30, 1, 19);
        }
        finally { g.Restore(state); }
    }

    private static void Confetti(Graphics g, double time, bool moving, float yOffset = 0)
    {
        var colors = new[] { Purple, Pink, Blue, Gold, Color.FromArgb(100, 216, 164) };
        for (var i = 0; i < 10; i++)
        {
            var x = 20 + (i * 31 % 71);
            var initial = 8 + i * 17 % 47;
            var travel = moving ? (float)((time / 3.6 * 19 + i * .7) % 19) : 0;
            var y = initial + travel + yOffset;
            if (i % 3 == 0) Ellipse(g, colors[i % colors.Length], x, y, 5, 5);
            else Line(g, colors[i % colors.Length], 3.5f, x, y, x + (i % 2 == 0 ? 5 : -4), y + 7);
        }
        Curve(g, Pink, 3, new PointF(45, 45 + yOffset), new PointF(53, 20 + yOffset), new PointF(64, 33 + yOffset), new PointF(72, 10 + yOffset));
        Curve(g, Blue, 3, new PointF(54, 56 + yOffset), new PointF(77, 41 + yOffset), new PointF(68, 65 + yOffset), new PointF(91, 54 + yOffset));
    }

    private static void Cloud(Graphics g, float x, float y)
    {
        var color = Color.FromArgb(226, 234, 255);
        Ellipse(g, color, x, y + 4, 17, 15); Ellipse(g, color, x + 9, y - 3, 18, 23);
        Ellipse(g, color, x + 19, y + 6, 14, 13); Round(g, color, x + 4, y + 10, 25, 11, 4);
    }

    private static void Leaf(Graphics g, PointF start, PointF tip, Color color)
    {
        using var p = new GraphicsPath();
        var dx = tip.X - start.X; var dy = tip.Y - start.Y;
        p.AddBezier(start, new PointF(start.X + dx * .25f - dy * .3f, start.Y + dy * .25f + dx * .3f),
            new PointF(tip.X - dx * .2f - dy * .15f, tip.Y - dy * .2f + dx * .15f), tip);
        p.AddBezier(tip, new PointF(tip.X - dx * .2f + dy * .2f, tip.Y - dy * .2f - dx * .2f),
            new PointF(start.X + dx * .25f + dy * .3f, start.Y + dy * .25f - dx * .3f), start); p.CloseFigure();
        using var brush = new SolidBrush(color); g.FillPath(brush, p);
    }

    private static void Drop(Graphics g, float x, float y, float width, float height, Color color)
    {
        using var p = new GraphicsPath();
        p.AddBezier(x + width / 2, y, x + width * .4f, y + height * .4f, x, y + height * .5f, x, y + height * .7f);
        p.AddBezier(x, y + height * .7f, x, y + height * 1.1f, x + width, y + height * 1.1f, x + width, y + height * .7f);
        p.AddBezier(x + width, y + height * .7f, x + width, y + height * .5f, x + width * .6f, y + height * .4f, x + width / 2, y); p.CloseFigure();
        GradientPath(g, p, new RectangleF(x, y, width, height), Lighten(color, 45), color);
    }

    private static void Flame(Graphics g, RectangleF r, Color top, Color bottom)
    {
        using var p = new GraphicsPath();
        PointF At(float x, float y) => new(r.X + r.Width * x, r.Y + r.Height * y);
        p.AddBezier(At(.51f, 0), At(.56f, .31f), At(.94f, .33f), At(.95f, .65f));
        p.AddBezier(At(.95f, .65f), At(1.03f, 1.13f), At(-.03f, 1.13f), At(.05f, .65f));
        p.AddBezier(At(.05f, .65f), At(.07f, .40f), At(.33f, .37f), At(.30f, .18f));
        p.AddBezier(At(.30f, .18f), At(.43f, .28f), At(.47f, .31f), At(.51f, 0)); p.CloseFigure();
        GradientPath(g, p, r, top, bottom);
    }

    private static void Heart(Graphics g, RectangleF bounds, Color color)
    {
        using var path = HeartPath(bounds);
        GradientPath(g, path, bounds, Lighten(color, 30), color);
        // A short contour highlight keeps small popup hearts legible on the dark theme.
        Curve(g, Color.FromArgb(95, Color.White), Math.Max(1.3f, bounds.Width * .035f),
            new PointF(bounds.X + bounds.Width * .17f, bounds.Y + bounds.Height * .32f),
            new PointF(bounds.X + bounds.Width * .22f, bounds.Y + bounds.Height * .13f),
            new PointF(bounds.X + bounds.Width * .35f, bounds.Y + bounds.Height * .18f));
    }

    private static GraphicsPath HeartPath(RectangleF r)
    {
        var p = new GraphicsPath();
        PointF At(float x, float y) => new(r.X + r.Width * x, r.Y + r.Height * y);
        p.AddBezier(At(.5f, .23f), At(.30f, -.10f), At(-.04f, .08f), At(.02f, .39f));
        p.AddBezier(At(.02f, .39f), At(.07f, .62f), At(.29f, .80f), At(.5f, 1));
        p.AddBezier(At(.5f, 1), At(.71f, .80f), At(.93f, .62f), At(.98f, .39f));
        p.AddBezier(At(.98f, .39f), At(1.04f, .08f), At(.70f, -.10f), At(.5f, .23f)); p.CloseFigure();
        return p;
    }

    private static void Star(Graphics g, float x, float y, float radius, Color color)
    {
        var points = new PointF[10];
        for (var i = 0; i < 10; i++)
        {
            var angle = -Math.PI / 2 + i * Math.PI / 5;
            var r = i % 2 == 0 ? radius : radius * .47f;
            points[i] = new PointF(x + (float)Math.Cos(angle) * r, y + (float)Math.Sin(angle) * r);
        }
        Polygon(g, color, points);
    }

    private static void Spark(Graphics g, float x, float y, float radius, Color color) =>
        Polygon(g, color, new PointF(x, y - radius), new PointF(x + radius * .28f, y - radius * .28f),
            new PointF(x + radius, y), new PointF(x + radius * .28f, y + radius * .28f), new PointF(x, y + radius),
            new PointF(x - radius * .28f, y + radius * .28f), new PointF(x - radius, y), new PointF(x - radius * .28f, y - radius * .28f));

    private static void Curve(Graphics g, Color color, float width, params PointF[] points)
    {
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawCurve(pen, points, .5f);
    }

    private static void Line(Graphics g, Color color, float width, float x1, float y1, float x2, float y2)
    {
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLine(pen, x1, y1, x2, y2);
    }

    private static void Ellipse(Graphics g, Color color, float x, float y, float width, float height)
    { using var brush = new SolidBrush(color); g.FillEllipse(brush, x, y, width, height); }

    private static void Rectangle(Graphics g, Color color, float x, float y, float width, float height)
    { using var brush = new SolidBrush(color); g.FillRectangle(brush, x, y, width, height); }

    private static void Polygon(Graphics g, Color color, params PointF[] points)
    { using var brush = new SolidBrush(color); g.FillPolygon(brush, points); }

    private static void GradientEllipse(Graphics g, RectangleF bounds, Color top, Color bottom)
    { using var brush = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical); g.FillEllipse(brush, bounds); }

    private static void GradientPath(Graphics g, GraphicsPath path, RectangleF bounds, Color top, Color bottom)
    { using var brush = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical); g.FillPath(brush, path); }

    private static void Round(Graphics g, Color color, float x, float y, float width, float height, float radius)
    {
        using var path = RoundPath(new RectangleF(x, y, width, height), radius);
        using var brush = new SolidBrush(color); g.FillPath(brush, path);
    }

    private static void RoundGradient(Graphics g, RectangleF bounds, float radius, Color top, Color bottom)
    { using var path = RoundPath(bounds, radius); GradientPath(g, path, bounds, top, bottom); }

    private static GraphicsPath RoundPath(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    private static Color Lighten(Color color, int amount) => Color.FromArgb(color.A,
        Math.Min(255, color.R + amount), Math.Min(255, color.G + amount), Math.Min(255, color.B + amount));

    private static double Bump(double time, double center, double halfWidth)
    {
        var distance = Math.Abs(time - center);
        return distance >= halfWidth ? 0 : .5 + .5 * Math.Cos(distance / halfWidth * Math.PI);
    }
}
