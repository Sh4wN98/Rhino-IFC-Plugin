using System;
using System.Collections.Generic;

namespace RhinoIfc.Core.Geometry
{
    /// <summary>Triangulates a planar polygon with holes (ear clipping, holes joined to the outer loop by bridges).
    /// Works on 3D points by projecting onto the polygon's best-fit plane.</summary>
    public static class Triangulator
    {
        /// <summary>Newell's normal of a polygon (not normalised; its length is twice the area).</summary>
        public static Vec3 Normal(IList<Vec3> polygon)
        {
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                x += (a.Y - b.Y) * (a.Z + b.Z);
                y += (a.Z - b.Z) * (a.X + b.X);
                z += (a.X - b.X) * (a.Y + b.Y);
            }
            return new Vec3(x, y, z);
        }

        /// <summary>Triangles as index triples into the concatenation outer + holes[0] + holes[1] …, oriented like the
        /// outer loop. Returns false (and a fan of the outer loop) if the polygon is degenerate.</summary>
        public static bool Triangulate(IList<Vec3> outer, IList<IList<Vec3>> holes, List<int> triangles)
        {
            int n = outer.Count;
            if (n < 3)
                return false;
            var normal = Normal(outer);
            if (normal.LengthSquared < 1e-30)
            {
                Fan(n, triangles);
                return false;
            }
            normal = normal.Unit();
            var u = normal.Perpendicular();
            var v = normal.Cross(u);

            // 2D coordinates of all points; outer counter-clockwise in (u, v) by construction of the normal.
            var points = new List<P2>();
            foreach (var p in outer)
                points.Add(new P2(p.Dot(u), p.Dot(v)));
            var ring = new List<int>(n);
            for (int i = 0; i < n; i++)
                ring.Add(i);

            if (holes != null && holes.Count > 0)
            {
                var holeRings = new List<List<int>>();
                foreach (var hole in holes)
                {
                    if (hole == null || hole.Count < 3)
                        continue;
                    int start = points.Count;
                    foreach (var p in hole)
                        points.Add(new P2(p.Dot(u), p.Dot(v)));
                    var h = new List<int>(hole.Count);
                    for (int i = 0; i < hole.Count; i++)
                        h.Add(start + i);
                    // Holes must run clockwise.
                    if (SignedArea(points, h) > 0)
                        h.Reverse();
                    holeRings.Add(h);
                }
                // Join holes from the one reaching furthest right (Eberly).
                holeRings.Sort((a, b) => MaxX(points, b).CompareTo(MaxX(points, a)));
                foreach (var h in holeRings)
                    ring = Bridge(points, ring, h);
            }

            if (!EarClip(points, ring, triangles))
            {
                // Should not happen for valid input; keep what was produced and fan the rest.
                return false;
            }
            return true;
        }

        private static void Fan(int n, List<int> triangles)
        {
            for (int i = 1; i + 1 < n; i++)
            {
                triangles.Add(0);
                triangles.Add(i);
                triangles.Add(i + 1);
            }
        }

        private readonly struct P2
        {
            public readonly double X, Y;

            public P2(double x, double y)
            {
                X = x;
                Y = y;
            }
        }

        private static double SignedArea(List<P2> p, List<int> ring)
        {
            double area = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = p[ring[i]];
                var b = p[ring[(i + 1) % ring.Count]];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area / 2;
        }

        private static double MaxX(List<P2> p, List<int> ring)
        {
            double max = double.MinValue;
            foreach (var i in ring)
                max = Math.Max(max, p[i].X);
            return max;
        }

        private static double Cross(P2 a, P2 b, P2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        /// <summary>Joins a clockwise hole into the counter-clockwise ring with a two-way bridge.</summary>
        private static List<int> Bridge(List<P2> p, List<int> ring, List<int> hole)
        {
            // Rightmost hole vertex.
            int hi = 0;
            for (int i = 1; i < hole.Count; i++)
                if (p[hole[i]].X > p[hole[hi]].X)
                    hi = i;
            var m = p[hole[hi]];

            // Find the closest ring edge hit by a ray to +X.
            int best = -1;
            double bestX = double.MaxValue;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = p[ring[i]];
                var b = p[ring[(i + 1) % ring.Count]];
                if ((a.Y > m.Y) == (b.Y > m.Y) && !(a.Y == m.Y || b.Y == m.Y))
                    continue;
                if (a.Y == b.Y)
                {
                    // Horizontal edge on the ray: take its nearer end.
                    if (a.Y != m.Y)
                        continue;
                    foreach (var k in new[] { i, (i + 1) % ring.Count })
                    {
                        var c = p[ring[k]];
                        if (c.X >= m.X && c.X < bestX)
                        {
                            bestX = c.X;
                            best = k;
                        }
                    }
                    continue;
                }
                double t = (m.Y - a.Y) / (b.Y - a.Y);
                if (t < 0 || t > 1)
                    continue;
                double x = a.X + t * (b.X - a.X);
                if (x >= m.X && x < bestX)
                {
                    bestX = x;
                    // Candidate: the edge endpoint with larger X.
                    best = a.X > b.X ? i : (i + 1) % ring.Count;
                }
            }
            if (best < 0)
            {
                // No hit (hole outside?): nearest vertex.
                double bestDistance = double.MaxValue;
                for (int i = 0; i < ring.Count; i++)
                {
                    var c = p[ring[i]];
                    double d = (c.X - m.X) * (c.X - m.X) + (c.Y - m.Y) * (c.Y - m.Y);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = i;
                    }
                }
            }
            else
            {
                // A reflex vertex inside the triangle (m, hit, candidate) is a better (visible) bridge end.
                var candidate = p[ring[best]];
                var hit = new P2(bestX, m.Y);
                double bestAngle = double.MaxValue;
                int visible = best;
                for (int i = 0; i < ring.Count; i++)
                {
                    if (i == best)
                        continue;
                    var c = p[ring[i]];
                    if (!InTriangle(m, hit, candidate, c))
                        continue;
                    var prev = p[ring[(i + ring.Count - 1) % ring.Count]];
                    var next = p[ring[(i + 1) % ring.Count]];
                    if (Cross(prev, c, next) > 0)
                        continue; // convex
                    double angle = Math.Abs(Math.Atan2(c.Y - m.Y, c.X - m.X));
                    if (angle < bestAngle)
                    {
                        bestAngle = angle;
                        visible = i;
                    }
                }
                best = visible;
            }

            var result = new List<int>(ring.Count + hole.Count + 2);
            for (int i = 0; i <= best; i++)
                result.Add(ring[i]);
            for (int k = 0; k <= hole.Count; k++)
                result.Add(hole[(hi + k) % hole.Count]);
            for (int i = best; i < ring.Count; i++)
                result.Add(ring[i]);
            return result;
        }

        private static bool InTriangle(P2 a, P2 b, P2 c, P2 p)
        {
            double d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
            bool negative = d1 < 0 || d2 < 0 || d3 < 0;
            bool positive = d1 > 0 || d2 > 0 || d3 > 0;
            return !(negative && positive);
        }

        private static bool EarClip(List<P2> p, List<int> ring, List<int> triangles)
        {
            var indices = new List<int>(ring);
            // Drop consecutive duplicates.
            for (int i = indices.Count - 1; i >= 0 && indices.Count > 3; i--)
            {
                var a = p[indices[i]];
                var b = p[indices[(i + 1) % indices.Count]];
                if (a.X == b.X && a.Y == b.Y)
                    indices.RemoveAt(i);
            }
            int guard = indices.Count * indices.Count + 10;
            int current = 0;
            int sinceLastEar = 0;
            while (indices.Count > 3 && guard-- > 0)
            {
                int count = indices.Count;
                int i0 = indices[(current + count - 1) % count];
                int i1 = indices[current % count];
                int i2 = indices[(current + 1) % count];
                var a = p[i0];
                var b = p[i1];
                var c = p[i2];
                double cross = Cross(a, b, c);
                bool ear = cross > 0;
                if (ear)
                {
                    for (int k = 0; k < count; k++)
                    {
                        int idx = indices[k];
                        if (idx == i0 || idx == i1 || idx == i2)
                            continue;
                        var q = p[idx];
                        // Points equal to a corner (bridge duplicates) do not block.
                        if ((q.X == a.X && q.Y == a.Y) || (q.X == b.X && q.Y == b.Y) || (q.X == c.X && q.Y == c.Y))
                            continue;
                        if (InTriangle(a, b, c, q))
                        {
                            ear = false;
                            break;
                        }
                    }
                }
                if (ear || sinceLastEar > count)
                {
                    // When no ear is found for a full round (degenerate input), cut anyway to finish.
                    if (Math.Abs(cross) > 0 || ear)
                    {
                        triangles.Add(i0);
                        triangles.Add(i1);
                        triangles.Add(i2);
                    }
                    indices.RemoveAt(current % count);
                    sinceLastEar = 0;
                    if (current >= indices.Count)
                        current = 0;
                    continue;
                }
                current = (current + 1) % count;
                sinceLastEar++;
            }
            if (indices.Count == 3)
            {
                var a = p[indices[0]];
                var b = p[indices[1]];
                var c = p[indices[2]];
                if (Math.Abs(Cross(a, b, c)) > 0)
                {
                    triangles.Add(indices[0]);
                    triangles.Add(indices[1]);
                    triangles.Add(indices[2]);
                }
            }
            return guard > 0;
        }
    }
}
