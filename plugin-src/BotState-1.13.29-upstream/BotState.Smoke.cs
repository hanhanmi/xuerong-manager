using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

public partial class BotState
{
    // Active smoke clouds, tracked from detonate/expired events.
    private sealed class SmokeCloud
    {
        public int Id;
        public float X, Y, Z;
        public float Start;
    }

    private const float SmokeRadius = 150f;      // solid part of a CS2 smoke
    private const float SmokeEdge = 230f;        // "at the edge" for reaction purposes
    private const float SmokeLifetime = 21f;     // fallback if the expired event is missed
    private readonly List<SmokeCloud> _smokes = new();

    private HookResult OnSmokeDetonate(EventSmokegrenadeDetonate @event, GameEventInfo info)
    {
        try
        {
            _smokes.RemoveAll(s => s.Id == @event.Entityid);
            _smokes.Add(new SmokeCloud
            {
                Id = @event.Entityid, X = @event.X, Y = @event.Y, Z = @event.Z,
                Start = Server.CurrentTime,
            });
        }
        catch { }
        return HookResult.Continue;
    }

    private HookResult OnSmokeExpired(EventSmokegrenadeExpired @event, GameEventInfo info)
    {
        try { _smokes.RemoveAll(s => s.Id == @event.Entityid); } catch { }
        return HookResult.Continue;
    }

    private void ExpireSmokes(float now)
    {
        if (_smokes.Count > 0) _smokes.RemoveAll(s => now - s.Start > SmokeLifetime);
    }

    // Smoke within `radius` of a position (2D, with a loose height check).
    private SmokeCloud? SmokeNear(Vector pos, float radius)
    {
        foreach (var s in _smokes)
        {
            if (MathF.Abs(pos.Z - s.Z) > 220f) continue;
            float dx = pos.X - s.X, dy = pos.Y - s.Y;
            if (dx * dx + dy * dy < radius * radius) return s;
        }
        return null;
    }

    // True when the segment a->b passes through a smoke cloud (2D distance to centre).
    private bool SegmentThroughSmoke(Vector a, Vector b, float radius = SmokeRadius)
    {
        foreach (var s in _smokes)
        {
            float minZ = MathF.Min(a.Z, b.Z) - 200f, maxZ = MathF.Max(a.Z, b.Z) + 200f;
            if (s.Z < minZ || s.Z > maxZ) continue;
            float abx = b.X - a.X, aby = b.Y - a.Y;
            float len2 = abx * abx + aby * aby;
            float t = len2 < 1f ? 0f : Math.Clamp(((s.X - a.X) * abx + (s.Y - a.Y) * aby) / len2, 0f, 1f);
            float px = a.X + abx * t - s.X, py = a.Y + aby * t - s.Y;
            if (px * px + py * py < radius * radius) return true;
        }
        return false;
    }

    private void ResetSmokeRoundState()
    {
        _smokes.Clear();
        _smokeDecisions.Clear();
        _smokeHold.Clear();
        _smokeNextCheck.Clear();
        _smokeSpam.Clear();
    }
}
