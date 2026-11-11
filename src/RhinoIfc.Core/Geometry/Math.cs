using System;
using System.Globalization;

namespace RhinoIfc.Core.Geometry
{
    /// <summary>A point or vector in 3D.</summary>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public readonly double X, Y, Z;

        public Vec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 XAxis = new Vec3(1, 0, 0);
        public static readonly Vec3 YAxis = new Vec3(0, 1, 0);
        public static readonly Vec3 ZAxis = new Vec3(0, 0, 1);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(double s, Vec3 a) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator /(Vec3 a, double s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;

        public Vec3 Cross(Vec3 b) => new Vec3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public double LengthSquared => X * X + Y * Y + Z * Z;

        public Vec3 Unit()
        {
            double l = Length;
            return l > 1e-300 ? this / l : this;
        }

        public bool IsValid => !(double.IsNaN(X) || double.IsNaN(Y) || double.IsNaN(Z) || double.IsInfinity(X) || double.IsInfinity(Y) || double.IsInfinity(Z));

        public bool Equals(Vec3 other) => X == other.X && Y == other.Y && Z == other.Z;

        public override bool Equals(object obj) => obj is Vec3 v && Equals(v);

        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() * 397) ^ (Z.GetHashCode() * 7919);

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:G6}, {1:G6}, {2:G6})", X, Y, Z);

        /// <summary>Any unit vector perpendicular to this one.</summary>
        public Vec3 Perpendicular()
        {
            var u = Unit();
            var other = Math.Abs(u.X) < 0.9 ? XAxis : YAxis;
            return u.Cross(other).Unit();
        }
    }

    /// <summary>An affine transformation as a 3x4 matrix (rotation/scale part and translation): p' = M·p + T.
    /// Composition: (A * B) applies B first, then A.</summary>
    public readonly struct Mat4
    {
        // Row-major 3x3 linear part and translation column.
        public readonly double M00, M01, M02, M03;
        public readonly double M10, M11, M12, M13;
        public readonly double M20, M21, M22, M23;

        public Mat4(double m00, double m01, double m02, double m03,
                    double m10, double m11, double m12, double m13,
                    double m20, double m21, double m22, double m23)
        {
            M00 = m00; M01 = m01; M02 = m02; M03 = m03;
            M10 = m10; M11 = m11; M12 = m12; M13 = m13;
            M20 = m20; M21 = m21; M22 = m22; M23 = m23;
        }

        public static readonly Mat4 Identity = new Mat4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0);

        /// <summary>The frame with origin and axes as columns (the axes need not be unit or orthogonal).</summary>
        public static Mat4 FromAxes(Vec3 origin, Vec3 x, Vec3 y, Vec3 z) =>
            new Mat4(x.X, y.X, z.X, origin.X,
                     x.Y, y.Y, z.Y, origin.Y,
                     x.Z, y.Z, z.Z, origin.Z);

        public static Mat4 Translation(Vec3 t) => new Mat4(1, 0, 0, t.X, 0, 1, 0, t.Y, 0, 0, 1, t.Z);

        public static Mat4 Scale(double s) => new Mat4(s, 0, 0, 0, 0, s, 0, 0, 0, 0, s, 0);

        public Vec3 Origin => new Vec3(M03, M13, M23);
        public Vec3 XColumn => new Vec3(M00, M10, M20);
        public Vec3 YColumn => new Vec3(M01, M11, M21);
        public Vec3 ZColumn => new Vec3(M02, M12, M22);

        public static Mat4 operator *(Mat4 a, Mat4 b) => new Mat4(
            a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
            a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
            a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            a.M00 * b.M03 + a.M01 * b.M13 + a.M02 * b.M23 + a.M03,
            a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
            a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
            a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            a.M10 * b.M03 + a.M11 * b.M13 + a.M12 * b.M23 + a.M13,
            a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
            a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
            a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22,
            a.M20 * b.M03 + a.M21 * b.M13 + a.M22 * b.M23 + a.M23);

        public Vec3 Point(Vec3 p) => new Vec3(
            M00 * p.X + M01 * p.Y + M02 * p.Z + M03,
            M10 * p.X + M11 * p.Y + M12 * p.Z + M13,
            M20 * p.X + M21 * p.Y + M22 * p.Z + M23);

        public Vec3 Vector(Vec3 v) => new Vec3(
            M00 * v.X + M01 * v.Y + M02 * v.Z,
            M10 * v.X + M11 * v.Y + M12 * v.Z,
            M20 * v.X + M21 * v.Y + M22 * v.Z);

        public double Determinant =>
            M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);

        public bool IsIdentity =>
            M00 == 1 && M11 == 1 && M22 == 1 && M01 == 0 && M02 == 0 && M10 == 0 && M12 == 0 && M20 == 0 && M21 == 0
            && M03 == 0 && M13 == 0 && M23 == 0;

        public Mat4 Inverse()
        {
            double det = Determinant;
            if (Math.Abs(det) < 1e-300)
                return Identity;
            double inv = 1.0 / det;
            double i00 = (M11 * M22 - M12 * M21) * inv;
            double i01 = (M02 * M21 - M01 * M22) * inv;
            double i02 = (M01 * M12 - M02 * M11) * inv;
            double i10 = (M12 * M20 - M10 * M22) * inv;
            double i11 = (M00 * M22 - M02 * M20) * inv;
            double i12 = (M02 * M10 - M00 * M12) * inv;
            double i20 = (M10 * M21 - M11 * M20) * inv;
            double i21 = (M01 * M20 - M00 * M21) * inv;
            double i22 = (M00 * M11 - M01 * M10) * inv;
            return new Mat4(
                i00, i01, i02, -(i00 * M03 + i01 * M13 + i02 * M23),
                i10, i11, i12, -(i10 * M03 + i11 * M13 + i12 * M23),
                i20, i21, i22, -(i20 * M03 + i21 * M13 + i22 * M23));
        }

        /// <summary>The 16 values row by row (last row 0 0 0 1), e.g. for Rhino's Transform.</summary>
        public double[] ToArray() => new[]
        {
            M00, M01, M02, M03, M10, M11, M12, M13, M20, M21, M22, M23, 0, 0, 0, 1,
        };

        /// <summary>True if the linear part is a rotation times a uniform positive scale (no mirror, no shear).</summary>
        public bool IsSimilarity(double tolerance = 1e-6)
        {
            var x = XColumn;
            var y = YColumn;
            var z = ZColumn;
            double s = x.Length;
            if (s < 1e-12)
                return false;
            return Math.Abs(y.Length - s) < tolerance * s && Math.Abs(z.Length - s) < tolerance * s
                && Math.Abs(x.Dot(y)) < tolerance * s * s && Math.Abs(x.Dot(z)) < tolerance * s * s
                && Math.Abs(y.Dot(z)) < tolerance * s * s && Determinant > 0;
        }

        public override string ToString() => $"[{XColumn} {YColumn} {ZColumn} | {Origin}]";
    }

    /// <summary>An axis-aligned bounding box.</summary>
    public struct BBox
    {
        public Vec3 Min;
        public Vec3 Max;

        public static BBox Empty => new BBox
        {
            Min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue),
            Max = new Vec3(double.MinValue, double.MinValue, double.MinValue),
        };

        public bool IsEmpty => Min.X > Max.X;

        public void Add(Vec3 p)
        {
            Min = new Vec3(Math.Min(Min.X, p.X), Math.Min(Min.Y, p.Y), Math.Min(Min.Z, p.Z));
            Max = new Vec3(Math.Max(Max.X, p.X), Math.Max(Max.Y, p.Y), Math.Max(Max.Z, p.Z));
        }

        public void Add(BBox b)
        {
            if (b.IsEmpty)
                return;
            Add(b.Min);
            Add(b.Max);
        }

        public Vec3 Size => IsEmpty ? Vec3.Zero : Max - Min;

        public double Diagonal => Size.Length;

        public override string ToString() => IsEmpty ? "(empty)" : $"{Min} – {Max}";
    }
}
