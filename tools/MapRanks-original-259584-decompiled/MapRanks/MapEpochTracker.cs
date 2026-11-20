using System;

namespace MapRanks;

public sealed class MapEpochTracker
{
	private double _reloadUntil = -1.0;

	public string MapName { get; private set; } = "";

	public long Epoch { get; private set; }

	public bool EndSeen { get; private set; }

	public double RestartUntil { get; private set; } = -1.0;

	public void MarkEnd()
	{
		EndSeen = true;
	}

	public void MarkReload(double now)
	{
		_reloadUntil = now + 120.0;
	}

	public void MarkRestart(double now, double delaySeconds)
	{
		RestartUntil = now + Math.Clamp(delaySeconds, 0.0, 60.0) + 15.0;
	}

	public bool InRestartWindow(double now)
	{
		return now <= RestartUntil;
	}

	public MapStartDecision Start(string mapName, double now)
	{
		bool flag = Epoch > 0 && StringComparer.OrdinalIgnoreCase.Equals(mapName, MapName);
		bool flag2 = now <= _reloadUntil;
		MapStartDecision mapStartDecision = ((flag && !flag2 && (InRestartWindow(now) || EndSeen)) ? MapStartDecision.SoftRestart : ((flag && !flag2 && !EndSeen) ? MapStartDecision.DuplicateCallback : MapStartDecision.NewMap));
		if (mapStartDecision == MapStartDecision.NewMap)
		{
			Epoch++;
			MapName = mapName;
			RestartUntil = -1.0;
		}
		EndSeen = false;
		_reloadUntil = -1.0;
		return mapStartDecision;
	}
}
