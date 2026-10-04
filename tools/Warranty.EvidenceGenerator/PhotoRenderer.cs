using SkiaSharp;

namespace Warranty.EvidenceGenerator;

/// <summary>
/// Draws a synthetic product photo: a device outline on a table surface, seen from the front (screen or
/// door) or the back (housing with the serial label), or a close-up of the serial label; optional damage
/// overlays and an optional handwritten sticky note.
/// </summary>
internal static class PhotoRenderer
{
    private static readonly SKColor LabelInk = new(0x1A, 0x1A, 0x1A);

    public static void Render(EvidenceItem item, string outputPath)
    {
        var random = Rendering.Seeded(item);
        using var bitmap = new SKBitmap(item.Width, item.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            DrawBackground(canvas, item, random);

            // Hand-held framing: slightly off-centre, rotated and scaled.
            var scale = Math.Min(item.Width / 1024f, item.Height / 768f) * random.Between(0.92f, 1.04f);
            canvas.Save();
            canvas.Translate((item.Width / 2f) + random.Between(-30, 30), (item.Height / 2f) + random.Between(-20, 20));
            canvas.RotateDegrees(random.Between(-4, 4));
            canvas.Scale(scale);

            var surface = item.View == "label"
                ? DrawLabelCloseUp(canvas, item)
                : item.View == "back" ? DrawBack(canvas, item) : DrawFront(canvas, item, random);

            foreach (var damage in item.Damage)
            {
                DrawDamage(canvas, damage, surface, random);
            }

            if (!string.IsNullOrWhiteSpace(item.Sticker))
            {
                DrawSticker(canvas, item.Sticker, surface, random);
            }

            canvas.Restore();
            DrawVignette(canvas, item);
        }

        Rendering.AddNoise(bitmap, random);
        Rendering.Save(bitmap, outputPath, item.Quality);
    }

    private static void DrawBackground(SKCanvas canvas, EvidenceItem item, Random random)
    {
        var tint = (byte)random.Next(0, 40);
        var top = new SKColor((byte)(170 + tint), (byte)(150 + (tint / 2)), 120);
        var bottom = new SKColor((byte)(120 + tint), (byte)(100 + (tint / 2)), 80);
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(item.Width * 0.3f, item.Height), [top, bottom], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(0, 0, item.Width, item.Height, paint);

        // Wood grain.
        using var grain = Rendering.Stroke(new SKColor(90, 60, 30, 28), 2);
        for (var y = random.Between(0, 25); y < item.Height; y += random.Between(18, 34))
        {
            using var path = new SKPathBuilder();
            path.MoveTo(0, y);
            path.CubicTo(item.Width * 0.3f, y + random.Between(-12, 12), item.Width * 0.7f, y + random.Between(-12, 12), item.Width, y + random.Between(-8, 8));
            canvas.DrawPath(path, grain);
        }
    }

    private static void DrawVignette(SKCanvas canvas, EvidenceItem item)
    {
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(item.Width / 2f, item.Height / 2f), Math.Max(item.Width, item.Height) * 0.75f,
                [SKColors.Transparent, new SKColor(0, 0, 0, 90)], [0.55f, 1f], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(0, 0, item.Width, item.Height, paint);
    }

    /// <summary>Draws the device front and returns the surface damage is drawn on (screen or door glass).</summary>
    private static SKRect DrawFront(SKCanvas canvas, EvidenceItem item, Random random)
    {
        switch (item.Device)
        {
            case "phone":
            {
                var body = new SKRect(-120, -250, 120, 250);
                Shadow(canvas, body, 34);
                canvas.DrawRoundRect(body, 34, 34, Rendering.Fill(new SKColor(0x22, 0x24, 0x28)));
                var screen = new SKRect(-108, -232, 108, 232);
                Screen(canvas, screen, item.Screen, 22);
                canvas.DrawCircle(0, -218, 6, Rendering.Fill(new SKColor(0x0B, 0x0B, 0x0D)));
                return screen;
            }

            case "laptop":
            {
                var lid = new SKRect(-330, -250, 330, 140);
                Shadow(canvas, new SKRect(-380, -250, 380, 250), 16);
                canvas.DrawRoundRect(lid, 16, 16, Rendering.Fill(new SKColor(0x2B, 0x2E, 0x33)));
                var screen = new SKRect(-310, -232, 310, 120);
                Screen(canvas, screen, item.Screen, 6);
                using var deck = new SKPathBuilder();
                deck.MoveTo(-330, 140);
                deck.LineTo(330, 140);
                deck.LineTo(380, 250);
                deck.LineTo(-380, 250);
                deck.Close();
                canvas.DrawPath(deck, Rendering.Fill(new SKColor(0xB8, 0xBC, 0xC2)));
                using var key = Rendering.Fill(new SKColor(0x3A, 0x3D, 0x42));
                for (var row = 0; row < 4; row++)
                {
                    var y = 150 + (row * 18);
                    var inset = 345 + (row * 8);
                    for (var x = -inset + 20; x < inset - 30; x += 30)
                    {
                        canvas.DrawRoundRect(new SKRect(x, y, x + 24, y + 13), 3, 3, key);
                    }
                }

                Brand(canvas, item, new SKPoint(0, 136), 12, new SKColor(0x9A, 0x9E, 0xA4));
                return screen;
            }

            case "hub":
            {
                var body = new SKRect(-170, -230, 170, 230);
                Shadow(canvas, body, 90);
                canvas.DrawRoundRect(body, 90, 90, Rendering.Fill(new SKColor(0x5C, 0x63, 0x6E)));
                using var dot = Rendering.Fill(new SKColor(0x4A, 0x50, 0x59));
                for (var y = -200f; y < 200; y += 14)
                {
                    for (var x = -150f; x < 150; x += 14)
                    {
                        if (body.Contains(x, y))
                        {
                            canvas.DrawCircle(x + (random.Next(2) * 7), y, 3, dot);
                        }
                    }
                }

                var display = new SKRect(-90, -60, 90, 40);
                Screen(canvas, display, item.Screen, 14);
                Brand(canvas, item, new SKPoint(0, 190), 20, SKColors.WhiteSmoke);
                return body;
            }

            case "oven":
            {
                var body = new SKRect(-330, -260, 330, 260);
                Shadow(canvas, body, 10);
                canvas.DrawRoundRect(body, 10, 10, Rendering.Fill(new SKColor(0xC9, 0xCC, 0xD1)));
                var panel = new SKRect(-330, -260, 330, -170);
                canvas.DrawRect(panel, Rendering.Fill(new SKColor(0x2F, 0x33, 0x38)));
                Screen(canvas, new SKRect(-60, -240, 60, -190), item.Screen, 4);
                using var knob = Rendering.Fill(new SKColor(0xA8, 0xAC, 0xB2));
                foreach (var x in new[] { -250f, -170f, 170f, 250f })
                {
                    canvas.DrawCircle(x, -215, 22, knob);
                }

                var door = new SKRect(-300, -150, 300, 240);
                canvas.DrawRoundRect(door, 8, 8, Rendering.Fill(new SKColor(0x1E, 0x21, 0x25)));
                var glass = new SKRect(-250, -110, 250, 200);
                canvas.DrawRoundRect(glass, 6, 6, Rendering.Fill(new SKColor(0x10, 0x12, 0x14)));
                canvas.DrawRect(new SKRect(-200, -140, 200, -126), Rendering.Fill(new SKColor(0xB0, 0xB4, 0xBA)));
                Brand(canvas, item, new SKPoint(0, -176), 13, SKColors.WhiteSmoke);
                return glass;
            }

            default:
            {
                var body = new SKRect(-300, -210, 300, 210);
                Shadow(canvas, body, 30);
                canvas.DrawRoundRect(body, 30, 30, Rendering.Fill(new SKColor(0x26, 0x28, 0x2D)));
                var screen = new SKRect(-276, -186, 276, 186);
                Screen(canvas, screen, item.Screen, 10);
                canvas.DrawCircle(0, -198, 5, Rendering.Fill(new SKColor(0x0B, 0x0B, 0x0D)));
                return screen;
            }
        }
    }

    /// <summary>Draws the device back (housing, brand, camera, serial label) and returns the housing.</summary>
    private static SKRect DrawBack(SKCanvas canvas, EvidenceItem item)
    {
        var (body, radius, color) = item.Device switch
        {
            "phone" => (new SKRect(-120, -250, 120, 250), 34f, new SKColor(0x8E, 0x95, 0xA0)),
            "laptop" => (new SKRect(-360, -240, 360, 240), 18f, new SKColor(0xA9, 0xAE, 0xB5)),
            "hub" => (new SKRect(-170, -230, 170, 230), 90f, new SKColor(0x5C, 0x63, 0x6E)),
            "oven" => (new SKRect(-330, -260, 330, 260), 8f, new SKColor(0x8D, 0x92, 0x99)),
            _ => (new SKRect(-300, -210, 300, 210), 30f, new SKColor(0xB4, 0xB9, 0xC0)),
        };

        Shadow(canvas, body, radius);
        using (var housing = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(body.Left, body.Top), new SKPoint(body.Right, body.Bottom),
                [color.WithAlpha(255), new SKColor((byte)(color.Red - 25), (byte)(color.Green - 25), (byte)(color.Blue - 25))], SKShaderTileMode.Clamp),
        })
        {
            canvas.DrawRoundRect(body, radius, radius, housing);
        }

        if (item.Device is "tablet" or "phone")
        {
            var camera = new SKRect(body.Left + 22, body.Top + 22, body.Left + 92, body.Top + 92);
            canvas.DrawRoundRect(camera, 18, 18, Rendering.Fill(new SKColor(0x30, 0x33, 0x38)));
            canvas.DrawCircle(camera.MidX, camera.MidY, 18, Rendering.Fill(new SKColor(0x0E, 0x10, 0x13)));
        }

        if (item.Device == "oven")
        {
            using var vent = Rendering.Stroke(new SKColor(0x5A, 0x5E, 0x64), 6);
            for (var y = body.Top + 40; y < body.Top + 160; y += 20)
            {
                canvas.DrawLine(body.Left + 80, y, body.Right - 80, y, vent);
            }
        }

        Brand(canvas, item, new SKPoint(body.MidX, body.MidY - (body.Height * 0.12f)), item.Device == "phone" ? 24 : 34, new SKColor(0xF4, 0xF5, 0xF7, 210));

        if (item.Serial is not null)
        {
            var width = Math.Min(260f, body.Width - 40);
            var label = new SKRect(body.MidX - (width / 2), body.Bottom - 150, body.MidX + (width / 2), body.Bottom - 40);
            DrawLabel(canvas, item, label, 1f);
        }

        return body;
    }

    /// <summary>A close-up of the rating/serial label filling most of the frame.</summary>
    private static SKRect DrawLabelCloseUp(SKCanvas canvas, EvidenceItem item)
    {
        var housing = new SKRect(-560, -420, 560, 420);
        canvas.DrawRect(housing, Rendering.Fill(new SKColor(0xA4, 0xA9, 0xB0)));
        var label = new SKRect(-330, -180, 330, 180);
        Shadow(canvas, label, 6);
        DrawLabel(canvas, item, label, 2.4f);
        return label;
    }

    private static void DrawLabel(SKCanvas canvas, EvidenceItem item, SKRect label, float zoom)
    {
        canvas.DrawRoundRect(label, 4 * zoom, 4 * zoom, Rendering.Fill(new SKColor(0xF7, 0xF7, 0xF2)));
        canvas.DrawRoundRect(label, 4 * zoom, 4 * zoom, Rendering.Stroke(new SKColor(0x80, 0x80, 0x80), zoom));

        using var ink = Rendering.Fill(LabelInk);
        using var small = Rendering.Font(Rendering.SansBold, 11 * zoom);
        using var mono = Rendering.Font(Rendering.Mono, 15 * zoom);
        var x = label.Left + (10 * zoom);
        var y = label.Top + (18 * zoom);
        canvas.DrawText($"{(item.Brand ?? BrandFromModel(item.ModelCode)).ToUpperInvariant()}  MODEL {item.ModelCode ?? "UNKNOWN"}", x, y, SKTextAlign.Left, small, ink);
        y += 22 * zoom;
        canvas.DrawText($"S/N: {item.Serial}", x, y, SKTextAlign.Left, mono, ink);
        y += 8 * zoom;

        // A barcode-like strip derived from the serial so it differs per unit.
        var seed = (item.Serial ?? string.Empty).Aggregate(17, (h, c) => (h * 31) + c);
        var random = new Random(seed);
        var barX = x;
        while (barX < label.Right - (10 * zoom))
        {
            var bar = random.Next(1, 4) * zoom;
            canvas.DrawRect(barX, y, bar, 26 * zoom, ink);
            barX += bar + (random.Next(1, 3) * zoom);
        }

        using var tiny = Rendering.Font(Rendering.Sans, 8 * zoom);
        canvas.DrawText("Rated 5V 3A  ·  Designed for indoor use", x, label.Bottom - (8 * zoom), SKTextAlign.Left, tiny, ink);
    }

    private static void Screen(SKCanvas canvas, SKRect screen, string state, float radius)
    {
        if (state == "on")
        {
            using var lit = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(screen.Left, screen.Top), new SKPoint(screen.Right, screen.Bottom),
                    [new SKColor(0x2E, 0x6F, 0xD8), new SKColor(0x7A, 0x3C, 0xC8)], SKShaderTileMode.Clamp),
            };
            canvas.DrawRoundRect(screen, radius, radius, lit);
            using var icon = Rendering.Fill(new SKColor(255, 255, 255, 170));
            var size = Math.Min(screen.Width, screen.Height) / 8;
            for (var y = screen.Top + size; y < screen.Bottom - size; y += size * 1.6f)
            {
                for (var x = screen.Left + size; x < screen.Right - size; x += size * 1.6f)
                {
                    canvas.DrawRoundRect(new SKRect(x, y, x + size, y + size), size / 4, size / 4, icon);
                }
            }

            return;
        }

        canvas.DrawRoundRect(screen, radius, radius, Rendering.Fill(new SKColor(0x07, 0x08, 0x0A)));
        using var glare = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(screen.Left, screen.Top), new SKPoint(screen.MidX, screen.MidY),
                [new SKColor(255, 255, 255, 38), SKColors.Transparent], SKShaderTileMode.Clamp),
        };
        canvas.DrawRoundRect(screen, radius, radius, glare);
    }

    private static void Shadow(SKCanvas canvas, SKRect body, float radius)
    {
        using var shadow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 110),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14),
        };
        var offset = body;
        offset.Offset(14, 18);
        canvas.DrawRoundRect(offset, radius, radius, shadow);
    }

    private static void Brand(SKCanvas canvas, EvidenceItem item, SKPoint at, float size, SKColor color)
    {
        using var font = Rendering.Font(Rendering.SansBold, size);
        using var paint = Rendering.Fill(color);
        canvas.DrawText((item.Brand ?? BrandFromModel(item.ModelCode)).ToUpperInvariant(), at.X, at.Y, SKTextAlign.Center, font, paint);
    }

    private static string BrandFromModel(string? modelCode) => modelCode?.Split('-')[0] switch
    {
        "AUR" => "Aurora",
        "BOR" => "Borealis",
        _ => "Generic",
    };

    private static void DrawDamage(SKCanvas canvas, string damage, SKRect surface, Random random)
    {
        canvas.Save();
        canvas.ClipRect(surface);
        var impact = new SKPoint(
            random.Between(surface.Left + (surface.Width * 0.25f), surface.Right - (surface.Width * 0.25f)),
            random.Between(surface.Top + (surface.Height * 0.25f), surface.Bottom - (surface.Height * 0.25f)));
        var reach = Math.Max(surface.Width, surface.Height);

        switch (damage)
        {
            case "crack":
            {
                using var crack = Rendering.Stroke(new SKColor(235, 238, 242, 220), 2.2f);
                using var fine = Rendering.Stroke(new SKColor(235, 238, 242, 150), 1.1f);
                var arms = random.Next(9, 14);
                for (var i = 0; i < arms; i++)
                {
                    var angle = (i * Math.PI * 2 / arms) + random.Between(-0.25f, 0.25f);
                    using var path = new SKPathBuilder();
                    path.MoveTo(impact);
                    var point = impact;
                    var length = random.Between(0.35f, 0.9f) * reach;
                    for (var travelled = 0f; travelled < length;)
                    {
                        var step = random.Between(18, 46);
                        travelled += step;
                        angle += random.Between(-0.35f, 0.35f);
                        point = new SKPoint(point.X + (float)(Math.Cos(angle) * step), point.Y + (float)(Math.Sin(angle) * step));
                        path.LineTo(point);
                    }

                    canvas.DrawPath(path, i % 3 == 0 ? fine : crack);
                }

                for (var ring = 1; ring <= 2; ring++)
                {
                    using var path = new SKPathBuilder();
                    var r = ring * random.Between(22, 36);
                    for (var a = 0; a <= 12; a++)
                    {
                        var angle = a * Math.PI / 6;
                        var p = new SKPoint(impact.X + (float)(Math.Cos(angle) * r * random.Between(0.8f, 1.2f)), impact.Y + (float)(Math.Sin(angle) * r * random.Between(0.8f, 1.2f)));
                        if (a == 0)
                        {
                            path.MoveTo(p);
                        }
                        else
                        {
                            path.LineTo(p);
                        }
                    }

                    canvas.DrawPath(path, fine);
                }

                canvas.DrawCircle(impact, 7, Rendering.Fill(new SKColor(250, 250, 250, 200)));
                break;
            }

            case "scorch":
            {
                for (var i = 0; i < 6; i++)
                {
                    var center = new SKPoint(impact.X + random.Between(-40, 40), impact.Y + random.Between(-30, 30));
                    var radius = random.Between(50, 110);
                    using var burn = new SKPaint
                    {
                        IsAntialias = true,
                        Shader = SKShader.CreateRadialGradient(
                            center, radius,
                            [new SKColor(20, 12, 6, 235), new SKColor(80, 45, 20, 150), SKColors.Transparent], [0f, 0.55f, 1f], SKShaderTileMode.Clamp),
                    };
                    canvas.DrawCircle(center, radius, burn);
                }

                break;
            }

            case "dent":
            {
                var rect = new SKRect(impact.X - 70, impact.Y - 45, impact.X + 70, impact.Y + 45);
                using var dark = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateRadialGradient(
                        new SKPoint(impact.X + 15, impact.Y + 10), 80, [new SKColor(0, 0, 0, 120), SKColors.Transparent], SKShaderTileMode.Clamp),
                };
                canvas.DrawOval(rect, dark);
                using var light = Rendering.Stroke(new SKColor(255, 255, 255, 120), 3);
                canvas.DrawArc(rect, 190, 120, false, light);
                break;
            }

            case "liquid":
            {
                for (var i = 0; i < 5; i++)
                {
                    var center = new SKPoint(impact.X + random.Between(-120, 120), impact.Y + random.Between(-80, 80));
                    var r = random.Between(25, 70);
                    using var stain = Rendering.Fill(new SKColor(150, 120, 70, 70));
                    using var rim = Rendering.Stroke(new SKColor(110, 85, 45, 120), 2.5f);
                    var oval = new SKRect(center.X - r, center.Y - (r * 0.7f), center.X + r, center.Y + (r * 0.7f));
                    canvas.DrawOval(oval, stain);
                    canvas.DrawOval(oval, rim);
                }

                break;
            }

            case "corrosion":
            {
                var corner = new SKPoint(surface.Right - (surface.Width * 0.15f), surface.Bottom - (surface.Height * 0.15f));
                for (var i = 0; i < 160; i++)
                {
                    var p = new SKPoint(corner.X + random.Between(-70, 70), corner.Y + random.Between(-50, 50));
                    var color = random.Next(2) == 0 ? new SKColor(70, 120, 80, 200) : new SKColor(130, 90, 40, 210);
                    canvas.DrawCircle(p, random.Between(1.5f, 5f), Rendering.Fill(color));
                }

                break;
            }

            case "scratches":
            {
                using var scratch = Rendering.Stroke(new SKColor(255, 255, 255, 90), 1.2f);
                for (var i = 0; i < 14; i++)
                {
                    var start = new SKPoint(random.Between(surface.Left, surface.Right), random.Between(surface.Top, surface.Bottom));
                    var angle = random.Between(-0.6f, 0.6f);
                    var length = random.Between(60, 220);
                    canvas.DrawLine(start, new SKPoint(start.X + (float)(Math.Cos(angle) * length), start.Y + (float)(Math.Sin(angle) * length)), scratch);
                }

                break;
            }
        }

        canvas.Restore();
    }

    private static void DrawSticker(SKCanvas canvas, string text, SKRect surface, Random random)
    {
        const float size = 230;
        canvas.Save();
        canvas.Translate(surface.MidX + random.Between(-surface.Width * 0.2f, surface.Width * 0.2f), surface.MidY + random.Between(-surface.Height * 0.15f, surface.Height * 0.15f));
        canvas.RotateDegrees(random.Between(-8, 8));
        var note = new SKRect(-size / 2, -size / 2, size / 2, size / 2);
        Shadow(canvas, note, 2);
        canvas.DrawRect(note, Rendering.Fill(new SKColor(0xFF, 0xE8, 0x6B)));

        using var ink = Rendering.Fill(new SKColor(0x1F, 0x2A, 0x80));
        var fontSize = 22f;
        List<string> lines;
        SKFont font;
        while (true)
        {
            font = Rendering.Font(Rendering.Hand, fontSize);
            lines = Rendering.Wrap(text, font, size - 24);
            if (lines.Count * fontSize * 1.25f <= size - 24 || fontSize <= 11)
            {
                break;
            }

            font.Dispose();
            fontSize -= 1;
        }

        using (font)
        {
            var y = note.Top + 12 + fontSize;
            foreach (var line in lines)
            {
                canvas.DrawText(line, note.Left + 12, y, SKTextAlign.Left, font, ink);
                y += fontSize * 1.25f;
            }
        }

        canvas.Restore();
    }
}
