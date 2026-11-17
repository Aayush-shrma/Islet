using System.Windows;
using System.Windows.Media;

namespace Islet
{
    /// <summary>
    /// The shape of the Now Playing takeover and the sea round it: the black body, and waves that roll
    /// in from outside and break on its edge, each lighting the edge as it lands. How high they run
    /// follows the music's loudness.
    /// </summary>
    public sealed class WaveEdge : FrameworkElement
    {
        /// <summary>Where the body is on screen, and how rounded its corners are.</summary>
        public Rect Shape { get; set; }
        public double Radius { get; set; } = 19;

        /// <summary>The colour the waves take from the cover, and the deeper one the body is tinted with.</summary>
        public Color Accent { get; set; } = Color.FromRgb(150, 130, 255);
        public Color Tint { get; set; } = Color.FromRgb(24, 18, 48);

        /// <summary>0 to 1: how loud the music is.</summary>
        public double Level { get; set; }

        /// <summary>Seconds, driving the waves.</summary>
        public double Time { get; set; }

        /// <summary>0 to 1: waves fade in as the body finishes opening.</summary>
        public double Presence { get; set; }

        /// <summary>How many waves are on their way in at once; fewer is lighter.</summary>
        public int Waves { get; set; } = 4;

        static double Frac(double x) => x - Math.Floor(x);

        static Color Mix(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

        protected override void OnRender(DrawingContext dc)
        {
            var body = Shape;

            if (body.Width < 2 || body.Height < 2)
                return;

            // The body: black, warmed towards the cover's colour from the top left
            var fill = new LinearGradientBrush(Mix(Colors.Black, Tint, 1), Colors.Black, new Point(0, 0), new Point(0.9, 1));
            dc.DrawRoundedRectangle(fill, null, body, Radius, Radius);

            double presence = Math.Clamp(Presence, 0, 1);
            double loud = Math.Clamp(Level, 0, 1);
            double perimeter = WavePerimeter.Length(body, Radius);
            int samples = (int)Math.Clamp(perimeter / 12, 80, 240);
            double reach = 70 + 120 * loud;
            double edgeLight = 0;

            // Each wave starts out in the open and rolls in; it brightens as it nears and is gone as it lands
            for (int i = 0; i < Waves; i++)
            {
                double p = Frac(Time * 0.15 + (double)i / Waves);
                double distance = (1 - p) * reach;
                double alpha = Math.Pow(Math.Sin(Math.PI * Math.Pow(p, 0.75)), 1.3) * 0.62 * presence;

                // How hard this one is striking the edge, for the edge to light up
                edgeLight += Math.Exp(-Math.Pow(distance / 16, 2)) * presence;

                if (alpha < 0.01)
                    continue;

                double height = (3 + 15 * loud) * (0.45 + 0.55 * Math.Sin(Math.PI * p));
                double k1 = 2 * Math.PI / (150 + i * 23);
                double k2 = 2 * Math.PI / (83 + i * 11);

                var geometry = new StreamGeometry();

                var points = new Point[samples];

                for (int n = 0; n < samples; n++)
                {
                    double s = perimeter * n / samples;
                    double swell = Math.Sin(s * k1 - Time * 2.3 + i * 1.7) * 0.62 + Math.Sin(s * k2 + Time * 1.5 + i * 0.9) * 0.38;
                    var (point, normal) = WavePerimeter.At(body, Radius, s);
                    points[n] = point + normal * Math.Max(1.5, distance + height * swell);
                }

                // Curves through the points' midpoints, so the wave is smooth rather than a string of straight pieces
                using (var ctx = geometry.Open())
                {
                    static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

                    ctx.BeginFigure(Mid(points[samples - 1], points[0]), false, true);

                    for (int n = 0; n < samples; n++)
                        ctx.QuadraticBezierTo(points[n], Mid(points[n], points[(n + 1) % samples]), true, true);
                }


                var pen = new Pen(new SolidColorBrush(WithAlpha(Mix(Accent, Colors.White, 0.25 * p), alpha)), 1 + 2.4 * p) { LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, pen, geometry);
            }

            // The edge itself, lit by whatever has just landed
            double glow = Math.Clamp(edgeLight, 0, 1);

            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(WithAlpha(Accent, (0.10 + 0.34 * glow) * presence)), 9 + 12 * glow), body, Radius, Radius);
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(WithAlpha(Mix(Accent, Colors.White, 0.55 * glow), 0.55 + 0.45 * glow)), 1.3 + 2.2 * glow), body, Radius, Radius);
        }
    }
}
