using System.Windows;

namespace Islet
{
    /// <summary>
    /// Walks round the outline of a rounded rectangle, giving the point at any distance along it and
    /// which way is outward there. The waves that break on the Now Playing takeover's edge are drawn
    /// by pushing each point of this outline out along its normal.
    /// </summary>
    public static class WavePerimeter
    {
        static double Clamp(Rect r, double radius) => Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));

        /// <summary>The length of the whole outline.</summary>
        public static double Length(Rect r, double radius)
        {
            radius = Clamp(r, radius);
            return 2 * (r.Width - 2 * radius) + 2 * (r.Height - 2 * radius) + 2 * Math.PI * radius;
        }

        /// <summary>
        /// The point <paramref name="s"/> along the outline, going clockwise from where the top edge's
        /// straight part begins, and the unit vector pointing out of the shape there.
        /// </summary>
        public static (Point Point, Vector Normal) At(Rect r, double radius, double s)
        {
            radius = Clamp(r, radius);
            double top = r.Width - 2 * radius;
            double side = r.Height - 2 * radius;
            double arc = Math.PI * radius / 2;
            double length = Length(r, radius);

            s %= length;

            if (s < 0)
                s += length;

            if (s < top)
                return (new Point(r.Left + radius + s, r.Top), new Vector(0, -1));

            s -= top;

            if (s < arc)
                return Corner(new Point(r.Right - radius, r.Top + radius), radius, -Math.PI / 2 + s / Math.Max(radius, 1e-9));

            s -= arc;

            if (s < side)
                return (new Point(r.Right, r.Top + radius + s), new Vector(1, 0));

            s -= side;

            if (s < arc)
                return Corner(new Point(r.Right - radius, r.Bottom - radius), radius, s / Math.Max(radius, 1e-9));

            s -= arc;

            if (s < top)
                return (new Point(r.Right - radius - s, r.Bottom), new Vector(0, 1));

            s -= top;

            if (s < arc)
                return Corner(new Point(r.Left + radius, r.Bottom - radius), radius, Math.PI / 2 + s / Math.Max(radius, 1e-9));

            s -= arc;

            if (s < side)
                return (new Point(r.Left, r.Bottom - radius - s), new Vector(-1, 0));

            s -= side;

            return Corner(new Point(r.Left + radius, r.Top + radius), radius, Math.PI + s / Math.Max(radius, 1e-9));
        }

        static (Point, Vector) Corner(Point centre, double radius, double angle)
        {
            var normal = new Vector(Math.Cos(angle), Math.Sin(angle));
            return (centre + normal * radius, normal);
        }
    }
}
