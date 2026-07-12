#nullable enable

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.AI;

/// <summary>
/// Debug visualization toggle (G): draws the parkour graph's edges, every bot's currently
/// planned path, and tagger intercept predictions via runtime <see cref="LineRenderer"/>s.
/// Hidden by default so it doesn't clutter a normal playtest.
/// </summary>
public sealed class ParkourDebugVisualizer : MonoBehaviour
{
    private ParkourGraph? _graph;
    private readonly List<ParkourBotInput> _bots = new();
    private readonly List<LineRenderer> _graphLines = new();
    private readonly Dictionary<ParkourBotInput, LineRenderer> _pathLines = new();
    private readonly Dictionary<ParkourBotInput, LineRenderer> _predictionLines = new();
    private bool _visible;

    public void Configure(ParkourGraph graph, IEnumerable<ParkourBotInput> bots)
    {
        _graph = graph;
        _bots.AddRange(bots);
        BuildGraphLines();
        SetVisible(false);
    }

    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current.gKey.wasPressedThisFrame)
            SetVisible(!_visible);

        if (!_visible) return;

        foreach (ParkourBotInput bot in _bots)
        {
            UpdatePathLine(bot);
            UpdatePredictionLine(bot);
        }
    }

    private void SetVisible(bool visible)
    {
        _visible = visible;
        foreach (LineRenderer line in _graphLines) line.enabled = visible;
        foreach (LineRenderer line in _pathLines.Values) line.enabled = visible;
        foreach (LineRenderer line in _predictionLines.Values) line.enabled = visible;
    }

    private void BuildGraphLines()
    {
        if (_graph == null) return;

        foreach (ParkourEdge edge in _graph.Edges)
        {
            LineRenderer line = CreateLine($"GraphEdge_{edge.Type}", EdgeColor(edge.Type), 0.06f);
            line.positionCount = 2;
            line.SetPosition(0, _graph.Nodes[edge.FromNode].Position);
            line.SetPosition(1, _graph.Nodes[edge.ToNode].Position);
            _graphLines.Add(line);
        }
    }

    private void UpdatePathLine(ParkourBotInput bot)
    {
        if (!_pathLines.TryGetValue(bot, out LineRenderer? line))
        {
            line = CreateLine($"BotPath_{bot.name}", Color.yellow, 0.1f);
            _pathLines[bot] = line;
        }

        IReadOnlyList<ParkourEdge>? path = bot.CurrentPath;
        if (path == null || path.Count == 0 || _graph == null)
        {
            line.positionCount = 0;
            return;
        }

        line.positionCount = path.Count + 1;
        line.SetPosition(0, bot.transform.position);
        for (int i = 0; i < path.Count; i++)
            line.SetPosition(i + 1, _graph.Nodes[path[i].ToNode].Position);
    }

    private void UpdatePredictionLine(ParkourBotInput bot)
    {
        if (!_predictionLines.TryGetValue(bot, out LineRenderer? line))
        {
            line = CreateLine($"BotPrediction_{bot.name}", new Color(1f, 0.1f, 0.1f), 0.08f);
            _predictionLines[bot] = line;
        }

        if (bot.LastPredictedPoint is not { } predicted)
        {
            line.positionCount = 0;
            return;
        }

        line.positionCount = 2;
        line.SetPosition(0, bot.transform.position);
        line.SetPosition(1, predicted);
    }

    private LineRenderer CreateLine(string name, Color color, float width)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.widthMultiplier = width;
        line.material = new Material(Shader.Find("Sprites/Default"));
        line.startColor = color;
        line.endColor = color;
        return line;
    }

    private static Color EdgeColor(ParkourEdgeType type) => type switch
    {
        ParkourEdgeType.Run => new Color(0.6f, 0.6f, 0.6f),
        ParkourEdgeType.Jump => Color.cyan,
        ParkourEdgeType.SlideHop => Color.blue,
        ParkourEdgeType.Mantle => Color.green,
        ParkourEdgeType.Vault => new Color(0f, 0.6f, 0.2f),
        ParkourEdgeType.Climb => new Color(1f, 0.5f, 0f),
        ParkourEdgeType.Ladder => new Color(0.6f, 0.3f, 0.1f),
        ParkourEdgeType.Swing => new Color(0.8f, 0.8f, 0f),
        ParkourEdgeType.Drop => Color.red,
        _ => Color.white,
    };
}
