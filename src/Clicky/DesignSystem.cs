//
//  DesignSystem.cs
//  Clicky for Windows
//
//  Centralized design system ported 1:1 from the original DesignSystem.swift —
//  same blue accent palette on dark surfaces, same spacing and radii, so the
//  Windows panel and overlay look like the Mac original.
//

using System.Windows.Media;

namespace Clicky;

public static class DS
{
    public static class Colors
    {
        // ── Backgrounds ──────────────────────────────────────────────
        public static readonly Color Background = FromHex("#101211");
        public static readonly Color Surface1 = FromHex("#171918");
        public static readonly Color Surface2 = FromHex("#202221");
        public static readonly Color Surface3 = FromHex("#272A29");
        public static readonly Color Surface4 = FromHex("#2E3130");

        // ── Borders ──────────────────────────────────────────────────
        public static readonly Color BorderSubtle = FromHex("#373B39");
        public static readonly Color BorderStrong = FromHex("#444947");

        // ── Text ─────────────────────────────────────────────────────
        public static readonly Color TextPrimary = FromHex("#ECEEED");
        public static readonly Color TextSecondary = FromHex("#ADB5B2");
        public static readonly Color TextTertiary = FromHex("#6B736F");
        public static readonly Color TextOnAccent = Color.FromRgb(255, 255, 255);

        // ── Blue Scale (Tailwind v4) ─────────────────────────────────
        public static readonly Color Blue400 = FromHex("#60a5fa");
        public static readonly Color Blue600 = FromHex("#2563eb");
        public static readonly Color Blue700 = FromHex("#1d4ed8");

        // ── Accent ───────────────────────────────────────────────────
        public static readonly Color Accent = Blue600;
        public static readonly Color AccentHover = Blue700;
        public static readonly Color AccentText = Blue400;

        // ── Semantic ─────────────────────────────────────────────────
        public static readonly Color Destructive = FromHex("#E5484D");
        public static readonly Color Success = FromHex("#34D399");
        public static readonly Color Warning = FromHex("#FFB224");

        /// The blue cursor/bubble color used in the overlay. Kept distinct
        /// from the accent since it serves a different purpose (screen
        /// overlay vs in-app UI).
        public static readonly Color OverlayCursorBlue = FromHex("#3380FF");

        private static Color FromHex(string hex)
        {
            string sanitized = hex.TrimStart('#');
            byte red = Convert.ToByte(sanitized.Substring(0, 2), 16);
            byte green = Convert.ToByte(sanitized.Substring(2, 2), 16);
            byte blue = Convert.ToByte(sanitized.Substring(4, 2), 16);
            return Color.FromRgb(red, green, blue);
        }
    }

    public static class Brushes
    {
        public static readonly SolidColorBrush Background = Frozen(Colors.Background);
        public static readonly SolidColorBrush Surface2 = Frozen(Colors.Surface2);
        public static readonly SolidColorBrush Surface3 = Frozen(Colors.Surface3);
        public static readonly SolidColorBrush BorderSubtle = Frozen(Colors.BorderSubtle);
        public static readonly SolidColorBrush TextPrimary = Frozen(Colors.TextPrimary);
        public static readonly SolidColorBrush TextSecondary = Frozen(Colors.TextSecondary);
        public static readonly SolidColorBrush TextTertiary = Frozen(Colors.TextTertiary);
        public static readonly SolidColorBrush TextOnAccent = Frozen(Colors.TextOnAccent);
        public static readonly SolidColorBrush Accent = Frozen(Colors.Accent);
        public static readonly SolidColorBrush Success = Frozen(Colors.Success);
        public static readonly SolidColorBrush Warning = Frozen(Colors.Warning);
        public static readonly SolidColorBrush OverlayCursorBlue = Frozen(Colors.OverlayCursorBlue);
        public static readonly SolidColorBrush White = Frozen(Color.FromRgb(255, 255, 255));

        public static SolidColorBrush WhiteWithOpacity(double opacity)
        {
            var brush = new SolidColorBrush(Color.FromRgb(255, 255, 255)) { Opacity = opacity };
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    public static class CornerRadius
    {
        public const double Small = 6;
        public const double Medium = 8;
        public const double Large = 10;
        public const double ExtraLarge = 12;
    }

    public static class AnimationDurations
    {
        public const double Fast = 0.15;
        public const double Normal = 0.25;
        public const double Slow = 0.4;
    }
}
