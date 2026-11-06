using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hdlss.App.Views;

/// <summary>
/// The user's app color: the accent and everything derived from it (hover, tinted badges, text on
/// accent buttons, the four tones of the logo mark, the window icon). Resources are replaced in
/// <see cref="Application.Resources"/>, so every <c>DynamicResource</c> follows immediately.
/// </summary>
public static class AccentTheme
{
    /// <summary>Logo cyan (brand sheet).</summary>
    public static readonly Color Default = Color.FromRgb(0x3D, 0xDB, 0xFE);
    public static Color Current { get; private set; } = Default;

    private static readonly Color Ink = Color.FromRgb(0x09, 0x0D, 0x14);          // logo tile
    private static readonly Color DarkText = Color.FromRgb(0x0B, 0x0D, 0x10);
    private static readonly Color LightText = Color.FromRgb(0xF5, 0xF7, 0xFA);

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryParse(string? text, out Color color)
    {
        color = Default;
        var hex = text?.Trim().TrimStart('#') ?? "";
        if (hex.Length == 3) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>Applies the saved color (null or invalid = logo cyan).</summary>
    public static void Apply(string? saved) => Apply(TryParse(saved, out var c) ? c : Default);

    public static void Apply(Color c)
    {
        var r = Application.Current.Resources;
        r["AccentColor"] = c;
        r["Accent"] = Brush(c);
        r["AccentHover"] = Brush(Mix(c, Colors.White, 0.33));
        r["AccentTint"] = Brush(Scale(c, 0.2));               // dark badge background (#0C2C33 for the logo cyan)
        r["OnAccent"] = Brush(Luminance(c) > 0.3 ? DarkText : LightText);
        r["LogoImage"] = LogoImage(c);
        Current = c;
        var icon = c == Default ? null : Icon(c, 256);
        foreach (Window w in Application.Current.Windows)
            if (w is MainWindow) w.Icon = icon ?? new BitmapImage(new Uri("pack://application:,,,/HDLSS5;component/app.ico"));
    }

    /// <summary>Text on the accent color: dark on light accents, light on dark ones.</summary>
    public static Color OnAccent(Color c) => Luminance(c) > 0.3 ? DarkText : LightText;

    // ── Logo mark: 260 x 264 units, as on the brand sheet ──

    /// <summary>The mark in four tones of <paramref name="c"/> (brand: #247489, #31AFCB, #3CD1F3, #3DDBFE).</summary>
    public static DrawingGroup Mark(Color c)
    {
        var g = new DrawingGroup();
        Brush a = Brush(Scale(c, 0.54)), b = Brush(Scale(c, 0.80)), cc = Brush(Scale(c, 0.95)), main = Brush(c);
        void Rect(Brush brush, double x, double y, double w, double h, double radius) =>
            g.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(x, y, w, h), radius, radius)));
        for (int k = 0; k < 6; k++)
        {
            double y = 4 + 44 * k;
            Rect(a, 0, y, 36, 36, 6);
            Rect(b, 44, y, 36, 36, 6);
            if (k is 2 or 3) Rect(cc, 88, y, 36, 36, 6);
        }
        Rect(main, 128, 88, 52, 88, 5);      // joins the bar (its right corners hide under it)
        Rect(main, 172, 0, 88, 264, 20);
        g.Freeze();
        return g;
    }

    public static DrawingImage LogoImage(Color c)
    {
        var image = new DrawingImage(Mark(c));
        image.Freeze();
        return image;
    }

    /// <summary>App icon: dark rounded tile with the mark, like app.ico.</summary>
    public static BitmapSource Icon(Color c, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(Brush(Ink), null, new Rect(size * 0.03, size * 0.03, size * 0.94, size * 0.94), size * 0.2, size * 0.2);
            double h = size * 0.94 * 0.52, w = h * 260 / 264;
            dc.PushTransform(new TranslateTransform((size - w) / 2, (size - h) / 2));
            dc.PushTransform(new ScaleTransform(h / 264, h / 264));
            dc.DrawDrawing(Mark(c));
            dc.Pop(); dc.Pop();
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    // ── Color helpers ──

    public static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static Color Scale(Color c, double f) =>
        Color.FromRgb((byte)Math.Round(c.R * f), (byte)Math.Round(c.G * f), (byte)Math.Round(c.B * f));

    public static Color Mix(Color c, Color with, double t) =>
        Color.FromRgb((byte)Math.Round(c.R + (with.R - c.R) * t), (byte)Math.Round(c.G + (with.G - c.G) * t), (byte)Math.Round(c.B + (with.B - c.B) * t));

    public static double Luminance(Color c)
    {
        static double L(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    /// <summary>Hue 0-360, saturation and value 0-1.</summary>
    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h < 0 ? h + 360 : h, max == 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
