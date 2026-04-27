using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SketchUpMimic;

public sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        UpdateStyles();
    }
}

public sealed class MainForm : Form
{
    private enum Tool { Draw, Light, Move, Erase }

    private readonly SceneModel _model = new();
    private readonly History _history = new();
    private readonly Renderer3D _renderer = new();

    private Tool _tool = Tool.Draw;
    private float _snapDist = 14f;

    private Vertex? _pendingVertex;
    private PointF? _hoverPos;
    private Lighter? _movingLighter;

    private bool _orbiting;
    private Point _lastMouse;

    // UI
    private readonly ToolStrip _toolStrip = new();
    private readonly SplitContainer _split = new();
    private readonly BufferedPanel _panel2D = new() { BackColor = Color.WhiteSmoke, Dock = DockStyle.Fill };
    private readonly BufferedPanel _panel3D = new() { BackColor = Color.FromArgb(32, 40, 48), Dock = DockStyle.Fill };
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    private ToolStripButton _btnDraw  = null!, _btnLight = null!, _btnMove = null!, _btnErase = null!;
    private ToolStripButton _btnUndo  = null!, _btnRedo  = null!;
    private ToolStripButton _btnRender = null!, _btnSave = null!, _btnClear = null!;
    private ToolStripComboBox _snapCombo = null!;
    private ToolStripComboBox _intensityCombo = null!;

    public MainForm()
    {
        Text = "SketchUp Mimic – WinForms";
        Width = 1280; Height = 800;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        KeyPreview = true;

        BuildToolStrip();

        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Vertical;
        _split.SplitterWidth = 6;
        _split.Panel1.Controls.Add(_panel2D);
        _split.Panel2.Controls.Add(_panel3D);
        _split.Panel1MinSize = 200;
        _split.Panel2MinSize = 200;

        _status.Items.Add(_statusLabel);
        _statusLabel.Text = "Click to start an edge, click again to finish. Snaps to nearest point/edge.";

        Controls.Add(_split);
        Controls.Add(_status);
        Controls.Add(_toolStrip);

        Shown += (_, _) => { _split.SplitterDistance = _split.Width / 2; };

        WireUp2D();
        WireUp3D();

        _history.Reset(_model);
        UpdateButtons();
    }

    /* ------------------------------------------------------------------ */
    /* Toolbar                                                            */
    /* ------------------------------------------------------------------ */
    private void BuildToolStrip()
    {
        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _toolStrip.ImageScalingSize = new Size(20, 20);

        _btnDraw  = NewToolBtn("✏ Draw Edge",   (_,_) => SetTool(Tool.Draw));
        _btnLight = NewToolBtn("💡 Place Lighter", (_,_) => SetTool(Tool.Light));
        _btnMove  = NewToolBtn("✋ Move Lighter",  (_,_) => SetTool(Tool.Move));
        _btnErase = NewToolBtn("🗑 Erase",        (_,_) => SetTool(Tool.Erase));
        _toolStrip.Items.AddRange(new ToolStripItem[] { _btnDraw, _btnLight, _btnMove, _btnErase, new ToolStripSeparator() });

        _btnUndo = NewToolBtn("↶ Undo", (_,_) => DoUndo());
        _btnRedo = NewToolBtn("↷ Redo", (_,_) => DoRedo());
        _toolStrip.Items.AddRange(new ToolStripItem[] { _btnUndo, _btnRedo, new ToolStripSeparator() });

        _btnRender = NewToolBtn("🌞 Render Lights: ON", (_,_) =>
        {
            _renderer.LightingOn = !_renderer.LightingOn;
            _btnRender.Text = "🌞 Render Lights: " + (_renderer.LightingOn ? "ON" : "OFF");
            _btnRender.Checked = _renderer.LightingOn;
            _panel3D.Invalidate();
        });
        _btnRender.Checked = true;
        _btnSave  = NewToolBtn("📷 Save JPG", (_,_) => SaveJpg());
        _btnClear = NewToolBtn("🧹 Clear",     (_,_) =>
        {
            _model.Vertices.Clear(); _model.Edges.Clear(); _model.Lighters.Clear();
            _pendingVertex = null;
            Commit();
        });
        _toolStrip.Items.AddRange(new ToolStripItem[] { _btnRender, _btnSave, _btnClear, new ToolStripSeparator() });

        _toolStrip.Items.Add(new ToolStripLabel("Snap (px):"));
        _snapCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
        _snapCombo.Items.AddRange(new object[] { "8", "14", "22", "32" });
        _snapCombo.SelectedIndex = 1;
        _snapCombo.SelectedIndexChanged += (_, _) =>
        {
            if (float.TryParse(_snapCombo.SelectedItem?.ToString(), out var v)) _snapDist = v;
        };
        _toolStrip.Items.Add(_snapCombo);

        _toolStrip.Items.Add(new ToolStripLabel("New light:"));
        _intensityCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        _intensityCombo.Items.AddRange(new object[] { "Soft", "Normal", "Bright", "Spot" });
        _intensityCombo.SelectedIndex = 1;
        _toolStrip.Items.Add(_intensityCombo);

        SetTool(Tool.Draw);
    }

    private ToolStripButton NewToolBtn(string text, EventHandler onClick)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        b.Click += onClick;
        return b;
    }

    private void SetTool(Tool t)
    {
        _tool = t;
        _pendingVertex = null;
        _btnDraw.Checked  = t == Tool.Draw;
        _btnLight.Checked = t == Tool.Light;
        _btnMove.Checked  = t == Tool.Move;
        _btnErase.Checked = t == Tool.Erase;
        _panel2D.Cursor = t switch
        {
            Tool.Draw  => Cursors.Cross,
            Tool.Erase => Cursors.No,
            _          => Cursors.Hand,
        };
        _panel2D.Invalidate();
    }

    private void UpdateButtons()
    {
        _btnUndo.Enabled = _history.CanUndo;
        _btnRedo.Enabled = _history.CanRedo;
    }

    /* ------------------------------------------------------------------ */
    /* History                                                            */
    /* ------------------------------------------------------------------ */
    private void Commit()
    {
        _history.Snapshot(_model);
        UpdateButtons();
        _panel2D.Invalidate();
        _panel3D.Invalidate();
    }
    private void DoUndo()
    {
        var s = _history.Undo(); if (s is null) return;
        _model.CopyFrom(s); _pendingVertex = null;
        UpdateButtons(); _panel2D.Invalidate(); _panel3D.Invalidate();
    }
    private void DoRedo()
    {
        var s = _history.Redo(); if (s is null) return;
        _model.CopyFrom(s); _pendingVertex = null;
        UpdateButtons(); _panel2D.Invalidate(); _panel3D.Invalidate();
    }

    /* ------------------------------------------------------------------ */
    /* 2D drawing panel                                                   */
    /* ------------------------------------------------------------------ */
    private void WireUp2D()
    {
        _panel2D.Paint += Panel2D_Paint;
        _panel2D.MouseDown += Panel2D_MouseDown;
        _panel2D.MouseMove += Panel2D_MouseMove;
        _panel2D.MouseUp   += Panel2D_MouseUp;
        _panel2D.MouseLeave += (_, _) => { _hoverPos = null; _panel2D.Invalidate(); };
        _panel2D.Resize += (_, _) => { _panel2D.Invalidate(); _panel3D.Invalidate(); };
    }

    private Vertex? FindNearestVertex(PointF p, float maxD)
    {
        Vertex? best = null; float bd = maxD;
        foreach (var v in _model.Vertices)
        {
            float d = Dist(v.Pos, p);
            if (d < bd) { bd = d; best = v; }
        }
        return best;
    }
    private static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private (Edge edge, PointF proj, float t)? FindNearestEdge(PointF p, float maxD)
    {
        (Edge e, PointF pr, float t)? best = null; float bd = maxD;
        foreach (var e in _model.Edges)
        {
            var a = _model.FindVertex(e.A); var b = _model.FindVertex(e.B);
            if (a is null || b is null) continue;
            var (proj, t) = ProjectOnSegment(p, a.Pos, b.Pos);
            float d = Dist(proj, p);
            if (d < bd && t > 0.05f && t < 0.95f) { bd = d; best = (e, proj, t); }
        }
        return best;
    }
    private static (PointF Proj, float T) ProjectOnSegment(PointF p, PointF a, PointF b)
    {
        float vx = b.X - a.X, vy = b.Y - a.Y, L2 = vx * vx + vy * vy;
        if (L2 < 1e-6f) return (a, 0f);
        float t = ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / L2;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        return (new PointF(a.X + vx * t, a.Y + vy * t), t);
    }

    private Vertex AddVertex(PointF p)
    {
        var v = new Vertex { Id = _model.NextId++, Pos = p };
        _model.Vertices.Add(v); return v;
    }
    private Edge? AddEdge(Vertex a, Vertex b)
    {
        if (a.Id == b.Id) return null;
        if (_model.Edges.Any(e => (e.A == a.Id && e.B == b.Id) || (e.A == b.Id && e.B == a.Id))) return null;
        var e = new Edge { Id = _model.NextId++, A = a.Id, B = b.Id };
        _model.Edges.Add(e); return e;
    }
    private Vertex SplitEdge(Edge edge, PointF proj)
    {
        var newV = AddVertex(proj);
        int a = edge.A, b = edge.B;
        _model.Edges.Remove(edge);
        AddEdge(_model.FindVertex(a)!, newV);
        AddEdge(newV, _model.FindVertex(b)!);
        return newV;
    }

    private Vertex Resolve(PointF p)
    {
        var nv = FindNearestVertex(p, _snapDist); if (nv != null) return nv;
        var ne = FindNearestEdge(p, _snapDist); if (ne != null) return SplitEdge(ne.Value.edge, ne.Value.proj);
        return AddVertex(p);
    }

    private Lighter? NearestLighter(PointF p, float maxD)
    {
        Lighter? best = null; float bd = maxD;
        foreach (var l in _model.Lighters)
        {
            float d = Dist(l.Pos, p);
            if (d < bd) { bd = d; best = l; }
        }
        return best;
    }

    private void Panel2D_MouseDown(object? sender, MouseEventArgs e)
    {
        var p = (PointF)e.Location;
        if (e.Button == MouseButtons.Right) { _pendingVertex = null; _panel2D.Invalidate(); return; }
        if (e.Button != MouseButtons.Left) return;

        switch (_tool)
        {
            case Tool.Draw:
            {
                var v = Resolve(p);
                if (_pendingVertex is null) { _pendingVertex = v; _panel2D.Invalidate(); }
                else { AddEdge(_pendingVertex, v); _pendingVertex = null; Commit(); }
                break;
            }
            case Tool.Light:
            {
                var (intensity, color) = LighterPreset();
                _model.Lighters.Add(new Lighter
                {
                    Id = _model.NextId++,
                    Pos = p,
                    Intensity = intensity,
                    Color = color
                });
                Commit();
                break;
            }
            case Tool.Move:
            {
                _movingLighter = NearestLighter(p, 22f);
                break;
            }
            case Tool.Erase:
            {
                var l = NearestLighter(p, 16f);
                if (l != null) { _model.Lighters.Remove(l); Commit(); break; }
                var v = FindNearestVertex(p, 12f);
                if (v != null)
                {
                    _model.Vertices.Remove(v);
                    _model.Edges.RemoveAll(ed => ed.A == v.Id || ed.B == v.Id);
                    Commit(); break;
                }
                var ne = FindNearestEdge(p, 8f);
                if (ne != null) { _model.Edges.Remove(ne.Value.edge); Commit(); }
                break;
            }
        }
    }

    private (float intensity, Color color) LighterPreset() => _intensityCombo.SelectedIndex switch
    {
        0 => (0.7f,  Color.FromArgb(255, 240, 210)),   // Soft
        2 => (1.8f,  Color.FromArgb(255, 220, 160)),   // Bright
        3 => (2.6f,  Color.FromArgb(255, 255, 230)),   // Spot
        _ => (1.2f,  Color.FromArgb(255, 230, 179)),   // Normal
    };

    private void Panel2D_MouseMove(object? sender, MouseEventArgs e)
    {
        _hoverPos = e.Location;
        if (_movingLighter != null)
        {
            _movingLighter.Pos = e.Location;
            _panel2D.Invalidate(); _panel3D.Invalidate();
            return;
        }
        _panel2D.Invalidate();
    }
    private void Panel2D_MouseUp(object? sender, MouseEventArgs e)
    {
        if (_movingLighter != null) { _movingLighter = null; Commit(); }
    }

    private void Panel2D_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawGrid(g, _panel2D.ClientSize);

        // faces
        var faces = FaceDetector.Detect(_model);
        using (var faceBrush = new SolidBrush(Color.FromArgb(64, 120, 170, 230)))
        {
            foreach (var f in faces)
            {
                var pts = f.Select(id => _model.FindVertex(id)!.Pos).ToArray();
                if (pts.Length >= 3) g.FillPolygon(faceBrush, pts);
            }
        }

        // edges
        using (var pen = new Pen(Color.FromArgb(34, 34, 34), 1.6f))
        {
            foreach (var ed in _model.Edges)
            {
                var a = _model.FindVertex(ed.A); var b = _model.FindVertex(ed.B);
                if (a != null && b != null) g.DrawLine(pen, a.Pos, b.Pos);
            }
        }

        // vertices
        using (var pb = new SolidBrush(Color.FromArgb(34, 34, 34)))
        {
            foreach (var v in _model.Vertices)
                g.FillEllipse(pb, v.Pos.X - 3, v.Pos.Y - 3, 6, 6);
        }

        // lighters
        foreach (var l in _model.Lighters)
        {
            float r = 6f + l.Intensity * 3f;
            using (var halo = new PathGradientBrush(MakeCircle(l.Pos, r * 2)))
            {
                halo.CenterColor = Color.FromArgb(180, l.Color);
                halo.SurroundColors = new[] { Color.FromArgb(0, l.Color) };
                g.FillEllipse(halo, l.Pos.X - r * 2, l.Pos.Y - r * 2, r * 4, r * 4);
            }
            using (var bb = new SolidBrush(l.Color))
            using (var bp = new Pen(Color.FromArgb(150, 110, 70), 1f))
            {
                g.FillEllipse(bb, l.Pos.X - r, l.Pos.Y - r, r * 2, r * 2);
                g.DrawEllipse(bp, l.Pos.X - r, l.Pos.Y - r, r * 2, r * 2);
            }
        }

        // hover snap indicator + pending edge preview
        if (_hoverPos is PointF hp)
        {
            PointF target = hp; bool snapped = false;
            var nv = FindNearestVertex(hp, _snapDist);
            if (nv != null) { target = nv.Pos; snapped = true; using var sp = new Pen(Color.FromArgb(0, 170, 100), 2f); g.DrawEllipse(sp, target.X - 7, target.Y - 7, 14, 14); }
            else
            {
                var ne = FindNearestEdge(hp, _snapDist);
                if (ne != null) { target = ne.Value.proj; snapped = true; using var sp = new Pen(Color.FromArgb(0, 130, 200), 2f); g.DrawEllipse(sp, target.X - 6, target.Y - 6, 12, 12); }
            }
            if (_tool == Tool.Draw && _pendingVertex != null)
            {
                using var pen = new Pen(Color.FromArgb(10, 115, 193), 1.5f) { DashStyle = DashStyle.Dash };
                g.DrawLine(pen, _pendingVertex.Pos, target);
            }
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath MakeCircle(PointF c, float r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddEllipse(c.X - r, c.Y - r, r * 2, r * 2);
        return path;
    }

    private static void DrawGrid(Graphics g, Size s)
    {
        using var pen = new Pen(Color.FromArgb(230, 230, 230), 1f);
        for (int x = 0; x < s.Width; x += 20) g.DrawLine(pen, x, 0, x, s.Height);
        for (int y = 0; y < s.Height; y += 20) g.DrawLine(pen, 0, y, s.Width, y);
    }

    /* ------------------------------------------------------------------ */
    /* 3D preview panel                                                   */
    /* ------------------------------------------------------------------ */
    private void WireUp3D()
    {
        _panel3D.Paint += Panel3D_Paint;
        _panel3D.MouseDown += (_, e) => { _orbiting = true; _lastMouse = e.Location; _panel3D.Cursor = Cursors.SizeAll; };
        _panel3D.MouseUp   += (_, _) => { _orbiting = false; _panel3D.Cursor = Cursors.Default; };
        _panel3D.MouseMove += (_, e) =>
        {
            if (!_orbiting) return;
            int dx = e.X - _lastMouse.X, dy = e.Y - _lastMouse.Y;
            _lastMouse = e.Location;
            _renderer.Yaw   += dx * 0.01f;
            _renderer.Pitch += dy * 0.01f;
            _renderer.Pitch = Math.Clamp(_renderer.Pitch, 0.1f, 1.45f);
            _panel3D.Invalidate();
        };
        _panel3D.MouseWheel += (_, e) =>
        {
            float f = e.Delta > 0 ? 1.1f : 1f / 1.1f;
            _renderer.Scale = Math.Clamp(_renderer.Scale * f, 0.2f, 6f);
            _panel3D.Invalidate();
        };
        _panel3D.Resize += (_, _) => _panel3D.Invalidate();
        _panel3D.MouseEnter += (_, _) => _panel3D.Focus();
    }

    private void Panel3D_Paint(object? sender, PaintEventArgs e)
    {
        int w = _panel3D.ClientSize.Width;
        int h = _panel3D.ClientSize.Height;
        var center2D = new PointF(_panel2D.ClientSize.Width / 2f, _panel2D.ClientSize.Height / 2f);
        using var bmp = _renderer.Render(w, h, _model, center2D);
        e.Graphics.DrawImageUnscaled(bmp, 0, 0);
    }

    /* ------------------------------------------------------------------ */
    /* Save current 3D view to JPG                                        */
    /* ------------------------------------------------------------------ */
    private void SaveJpg()
    {
        int w = _panel3D.ClientSize.Width;
        int h = _panel3D.ClientSize.Height;
        var center2D = new PointF(_panel2D.ClientSize.Width / 2f, _panel2D.ClientSize.Height / 2f);
        using var bmp = _renderer.Render(w, h, _model, center2D);

        using var sfd = new SaveFileDialog
        {
            Filter = "JPEG Image|*.jpg",
            FileName = $"sketchup_mimic_{DateTime.Now:yyyyMMdd_HHmmss}.jpg"
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/jpeg");
        var ep = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, 92L) } };
        if (encoder != null) bmp.Save(sfd.FileName, encoder, ep);
        else bmp.Save(sfd.FileName, ImageFormat.Jpeg);

        _statusLabel.Text = "Saved " + Path.GetFileName(sfd.FileName);
    }

    /* ------------------------------------------------------------------ */
    /* Keyboard shortcuts                                                 */
    /* ------------------------------------------------------------------ */
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Control && e.KeyCode == Keys.Z) { if (e.Shift) DoRedo(); else DoUndo(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.Y) { DoRedo(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { _pendingVertex = null; _panel2D.Invalidate(); }
    }
}
