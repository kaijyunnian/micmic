using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace SketchUpMimic;

public sealed class Vertex
{
    public int Id;
    public PointF Pos;
}

public sealed class Edge
{
    public int Id;
    public int A;
    public int B;
}

public sealed class Lighter
{
    public int Id;
    public PointF Pos;
    public float Intensity = 1.2f;
    public Color Color = Color.FromArgb(255, 230, 179);
}

public sealed class SceneModel
{
    public List<Vertex> Vertices { get; } = new();
    public List<Edge> Edges { get; } = new();
    public List<Lighter> Lighters { get; } = new();
    public int NextId = 1;

    public Vertex? FindVertex(int id) => Vertices.Find(v => v.Id == id);

    public SceneModel Clone()
    {
        var c = new SceneModel { NextId = NextId };
        foreach (var v in Vertices) c.Vertices.Add(new Vertex { Id = v.Id, Pos = v.Pos });
        foreach (var e in Edges) c.Edges.Add(new Edge { Id = e.Id, A = e.A, B = e.B });
        foreach (var l in Lighters) c.Lighters.Add(new Lighter { Id = l.Id, Pos = l.Pos, Intensity = l.Intensity, Color = l.Color });
        return c;
    }

    public void CopyFrom(SceneModel other)
    {
        Vertices.Clear(); Edges.Clear(); Lighters.Clear();
        foreach (var v in other.Vertices) Vertices.Add(new Vertex { Id = v.Id, Pos = v.Pos });
        foreach (var e in other.Edges) Edges.Add(new Edge { Id = e.Id, A = e.A, B = e.B });
        foreach (var l in other.Lighters) Lighters.Add(new Lighter { Id = l.Id, Pos = l.Pos, Intensity = l.Intensity, Color = l.Color });
        NextId = other.NextId;
    }
}

public sealed class History
{
    private readonly Stack<SceneModel> _undo = new();
    private readonly Stack<SceneModel> _redo = new();

    public void Reset(SceneModel m) { _undo.Clear(); _redo.Clear(); _undo.Push(m.Clone()); }

    public void Snapshot(SceneModel m) { _undo.Push(m.Clone()); _redo.Clear(); }

    public bool CanUndo => _undo.Count >= 2;
    public bool CanRedo => _redo.Count > 0;

    public SceneModel? Undo()
    {
        if (_undo.Count < 2) return null;
        _redo.Push(_undo.Pop());
        return _undo.Peek().Clone();
    }

    public SceneModel? Redo()
    {
        if (_redo.Count == 0) return null;
        var s = _redo.Pop();
        _undo.Push(s);
        return s.Clone();
    }
}

/// <summary>
/// Planar-graph face finder. Sorts edges around each vertex by angle and
/// walks "next CW edge before reverse" to enumerate faces, dropping the
/// outer one by orientation.
/// </summary>
public static class FaceDetector
{
    public static List<List<int>> Detect(SceneModel m)
    {
        var adj = new Dictionary<int, List<(int To, double Angle)>>();
        foreach (var v in m.Vertices) adj[v.Id] = new();
        foreach (var e in m.Edges)
        {
            var a = m.FindVertex(e.A); var b = m.FindVertex(e.B);
            if (a is null || b is null) continue;
            adj[a.Id].Add((b.Id, Math.Atan2(b.Pos.Y - a.Pos.Y, b.Pos.X - a.Pos.X)));
            adj[b.Id].Add((a.Id, Math.Atan2(a.Pos.Y - b.Pos.Y, a.Pos.X - b.Pos.X)));
        }
        foreach (var key in adj.Keys.ToList())
            adj[key].Sort((x, y) => x.Angle.CompareTo(y.Angle));

        (int From, int To)? NextEdge(int u, int v)
        {
            if (!adj.TryGetValue(v, out var list)) return null;
            int idx = list.FindIndex(x => x.To == u);
            if (idx < 0) return null;
            var prev = list[(idx - 1 + list.Count) % list.Count];
            return (v, prev.To);
        }

        var visited = new HashSet<(int, int)>();
        var faces = new List<List<int>>();
        foreach (var e in m.Edges)
        {
            foreach (var (u, v) in new[] { (e.A, e.B), (e.B, e.A) })
            {
                if (visited.Contains((u, v))) continue;
                var cycle = new List<int>();
                int cu = u, cv = v, guard = 0;
                bool aborted = false;
                while (guard++ < 10000)
                {
                    if (visited.Contains((cu, cv))) break;
                    visited.Add((cu, cv));
                    cycle.Add(cu);
                    var nx = NextEdge(cu, cv);
                    if (nx is null) { aborted = true; break; }
                    cu = nx.Value.From; cv = nx.Value.To;
                    if (cu == u && cv == v) break;
                }
                if (!aborted && cycle.Count >= 3 && SignedArea(m, cycle) > 0)
                    faces.Add(cycle);
            }
        }
        return faces;
    }

    public static double SignedArea(SceneModel m, List<int> cycle)
    {
        double s = 0;
        for (int i = 0; i < cycle.Count; i++)
        {
            var a = m.FindVertex(cycle[i])!.Pos;
            var b = m.FindVertex(cycle[(i + 1) % cycle.Count])!.Pos;
            s += a.X * b.Y - b.X * a.Y;
        }
        return s * 0.5;
    }
}
