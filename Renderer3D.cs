using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;

namespace SketchUpMimic;

/// <summary>
/// Axonometric (orthographic + yaw + pitch) projection. The world is the
/// Y-up XZ plane; faces lie at y=0; lights sit at y=LightHeight above
/// their 2D position.
/// </summary>
public sealed class ScreenSpace
{
    public float Pitch;        // radians, 0=top-down, ~pi/2=side
    public float Yaw;          // radians, rotation around Y
    public float Scale = 1f;   // pixels per world-unit
    public float Cx, Cy;       // screen center

    public ScreenSpace(float pitch, float yaw, float scale, float cx, float cy)
    { Pitch = pitch; Yaw = yaw; Scale = scale; Cx = cx; Cy = cy; }

    public PointF WorldToScreen(float x, float y, float z)
    {
        float cyw = MathF.Cos(Yaw), syw = MathF.Sin(Yaw);
        float xr =  x * cyw + z * syw;
        float zr = -x * syw + z * cyw;
        float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
        float sx = xr * Scale;
        float sy = -(zr * cp - y * sp) * Scale;
        return new PointF(sx + Cx, sy + Cy);
    }

    /// <summary>Inverse of WorldToScreen for points known to lie on y=0.</summary>
    public (float wx, float wz) ScreenToFloor(float sx, float sy)
    {
        float cp = MathF.Cos(Pitch);
        if (cp < 1e-3f) cp = 1e-3f;
        float xr = (sx - Cx) / Scale;
        float zr = -(sy - Cy) / (cp * Scale);
        float cyw = MathF.Cos(Yaw), syw = MathF.Sin(Yaw);
        float x = xr * cyw - zr * syw;
        float z = xr * syw + zr * cyw;
        return (x, z);
    }
}

public sealed class Renderer3D
{
    public float Pitch = 0.85f;            // ~49°
    public float Yaw   = 0.55f;
    public float Scale = 1.0f;
    public float LightHeight = 160f;
    public float Ambient = 0.16f;
    public bool  LightingOn = true;
    public Color FaceColor = Color.FromArgb(178, 200, 226);
    public Color BgColor   = Color.FromArgb(32, 40, 48);
    public Color FloorColor = Color.FromArgb(56, 64, 72);

    public Bitmap Render(int w, int h, SceneModel m, PointF center2D)
    {
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var bg = Graphics.FromImage(bmp))
        {
            bg.Clear(BgColor);
        }

        var ss = new ScreenSpace(Pitch, Yaw, Scale, w / 2f, h / 2f);

        // World mapping: model.x - cx -> world.X ; -(model.y - cy) -> world.Z
        float ToWX(float mx) => mx - center2D.X;
        float ToWZ(float my) => -(my - center2D.Y);

        // Pre-compute lighter world positions
        var lights = m.Lighters.Select(l => new
        {
            X = ToWX(l.Pos.X),
            Z = ToWZ(l.Pos.Y),
            Y = LightHeight,
            R = l.Color.R / 255f,
            G = l.Color.G / 255f,
            B = l.Color.B / 255f,
            I = l.Intensity
        }).ToArray();

        float fcR = FaceColor.R / 255f;
        float fcG = FaceColor.G / 255f;
        float fcB = FaceColor.B / 255f;

        var faces = FaceDetector.Detect(m);

        // Per-pixel rasterize each face directly into bitmap.
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* scan0 = (byte*)data.Scan0;
                int stride = data.Stride;

                foreach (var face in faces)
                {
                    // Project face vertices to screen
                    var pts = new PointF[face.Count];
                    for (int i = 0; i < face.Count; i++)
                    {
                        var v = m.FindVertex(face[i])!;
                        pts[i] = ss.WorldToScreen(ToWX(v.Pos.X), 0, ToWZ(v.Pos.Y));
                    }

                    float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
                    float minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
                    int x0 = Math.Max(0, (int)Math.Floor(minX));
                    int x1 = Math.Min(w - 1, (int)Math.Ceiling(maxX));
                    int y0 = Math.Max(0, (int)Math.Floor(minY));
                    int y1 = Math.Min(h - 1, (int)Math.Ceiling(maxY));

                    for (int py = y0; py <= y1; py++)
                    {
                        byte* row = scan0 + py * stride;
                        for (int px = x0; px <= x1; px++)
                        {
                            float sx = px + 0.5f, sy = py + 0.5f;
                            if (!PointInPolygon(pts, sx, sy)) continue;

                            float r, g, b;
                            if (LightingOn)
                            {
                                var (wx, wz) = ss.ScreenToFloor(sx, sy);
                                r = g = b = Ambient;
                                for (int i = 0; i < lights.Length; i++)
                                {
                                    var L = lights[i];
                                    float dx = L.X - wx, dy = L.Y, dz = L.Z - wz;
                                    float d2 = dx * dx + dy * dy + dz * dz;
                                    float d = MathF.Sqrt(d2);
                                    float ndotL = dy / d;          // N=(0,1,0)
                                    if (ndotL <= 0f) continue;
                                    float att = 1f / (1f + d2 * 5e-5f);
                                    float k = ndotL * att * L.I;
                                    r += k * L.R; g += k * L.G; b += k * L.B;
                                }
                                r *= fcR; g *= fcG; b *= fcB;
                            }
                            else
                            {
                                r = fcR; g = fcG; b = fcB;
                            }

                            byte* p = row + px * 4;
                            p[0] = ToByte(b);
                            p[1] = ToByte(g);
                            p[2] = ToByte(r);
                            p[3] = 255;
                        }
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        // Overlay edges + lighter bulbs/stands with anti-aliased GDI+
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // axes helper at origin
            using (var axisX = new Pen(Color.FromArgb(180, 80, 80), 1f))
            using (var axisZ = new Pen(Color.FromArgb(80, 160, 80), 1f))
            {
                var o  = ss.WorldToScreen(0, 0, 0);
                var px = ss.WorldToScreen(60, 0, 0);
                var pz = ss.WorldToScreen(0, 0, 60);
                g.DrawLine(axisX, o, px);
                g.DrawLine(axisZ, o, pz);
            }

            using var edgePen = new Pen(Color.FromArgb(20, 20, 20), 1.4f);
            foreach (var e in m.Edges)
            {
                var a = m.FindVertex(e.A); var b = m.FindVertex(e.B);
                if (a is null || b is null) continue;
                var pa = ss.WorldToScreen(ToWX(a.Pos.X), 0.1f, ToWZ(a.Pos.Y));
                var pb = ss.WorldToScreen(ToWX(b.Pos.X), 0.1f, ToWZ(b.Pos.Y));
                g.DrawLine(edgePen, pa, pb);
            }

            using var standPen = new Pen(Color.FromArgb(150, 110, 70), 1f);
            foreach (var l in m.Lighters)
            {
                var foot = ss.WorldToScreen(ToWX(l.Pos.X), 0, ToWZ(l.Pos.Y));
                var head = ss.WorldToScreen(ToWX(l.Pos.X), LightHeight, ToWZ(l.Pos.Y));
                g.DrawLine(standPen, foot, head);
                float r = 4f + l.Intensity * 2.2f;
                using var halo = new SolidBrush(Color.FromArgb(70, l.Color));
                g.FillEllipse(halo, head.X - r * 2, head.Y - r * 2, r * 4, r * 4);
                using var bulb = new SolidBrush(l.Color);
                g.FillEllipse(bulb, head.X - r, head.Y - r, r * 2, r * 2);
            }
        }

        return bmp;
    }

    private static byte ToByte(float v)
    {
        if (v <= 0f) return 0;
        if (v >= 1f) return 255;
        return (byte)(v * 255f);
    }

    private static bool PointInPolygon(PointF[] poly, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            float xi = poly[i].X, yi = poly[i].Y;
            float xj = poly[j].X, yj = poly[j].Y;
            bool intersect = ((yi > y) != (yj > y)) &&
                             (x < (xj - xi) * (y - yi) / (yj - yi + 1e-9f) + xi);
            if (intersect) inside = !inside;
        }
        return inside;
    }
}
