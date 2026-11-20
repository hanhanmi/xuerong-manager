using System.Security.Cryptography;
using CounterStrikeSharp.API;

namespace BotState;

public partial class BotState
{
    private enum DefusalTier { Easy, Normal, Hard }
    private DefusalTier? _defusalTier;
    private readonly float[] _firstShotMs = new float[256];
    private int _firstShotCount;

    private void RefreshDefusalDifficulty()
    {
        _defusalTier = null;
        try
        {
            var gameDir = Server.GameDirectory;
            string[] roots = { gameDir, Path.Combine(gameDir, "csgo"), Path.Combine(gameDir, "game", "csgo") };
            foreach (var root in roots)
            {
                string folder = Path.Combine(root, "overrides");
                string active = Path.Combine(folder, "botprofile.vpk");
                if (!File.Exists(active)) continue;
                byte[] hash = ProfileHash(active);
                string[] presets = { "Low", "Medium", "High" };
                DefusalTier? match = null;
                for (int i = 0; i < presets.Length; i++)
                {
                    string sample = Path.Combine(folder, presets[i], "botprofile.vpk");
                    if (!File.Exists(sample) || !hash.AsSpan().SequenceEqual(ProfileHash(sample))) continue;
                    if (match.HasValue) return;
                    match = (DefusalTier)i;
                }
                _defusalTier = match;
                return;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static byte[] ProfileHash(string file)
    {
        using var stream = File.OpenRead(file);
        return SHA256.HashData(stream);
    }

    private string DefusalReactionStatus()
    {
        string tier = _defusalTier?.ToString() ?? "custom";
        int used = Math.Min(_firstShotCount, _firstShotMs.Length);
        if (used == 0) return $"[Smarter-Bot] tier={tier} firstShots=0";
        var samples = _firstShotMs.AsSpan(0, used).ToArray();
        Array.Sort(samples);
        return $"[Smarter-Bot] tier={tier} firstShots={_firstShotCount} p10={samples[(used - 1) / 10]:F0}ms p50={samples[(used - 1) / 2]:F0}ms p90={samples[(used - 1) * 9 / 10]:F0}ms";
    }
}
