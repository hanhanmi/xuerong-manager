using System;

namespace MapRanks;

public sealed class ResetFreezeGate
{
	public long Generation { get; private set; }

	public long RoundToken { get; private set; }

	public long BoundaryTick { get; private set; } = -1L;

	public bool Open { get; private set; }

	public bool Queued { get; private set; }

	public bool Dispatched { get; private set; }

	public void Begin(long generation, long roundToken, long tick)
	{
		if (generation < 1 || roundToken < 1 || tick < 0)
		{
			throw new ArgumentOutOfRangeException("tick");
		}
		Generation = generation;
		RoundToken = roundToken;
		BoundaryTick = tick;
		Open = true;
		Queued = false;
		Dispatched = false;
	}

	public bool Ready(long tick, long postThinkTick, long transmitTick, LiveRules rules, bool canPublish)
	{
		if (((Open && !Queued && !Dispatched) & canPublish) && rules.Known && rules.Freeze && !rules.Warmup && tick > BoundaryTick && tick - BoundaryTick >= 2 && postThinkTick > BoundaryTick)
		{
			return transmitTick > BoundaryTick;
		}
		return false;
	}

	public void MarkQueued()
	{
		if (!Open || Queued || Dispatched)
		{
			throw new InvalidOperationException("No reset-freeze work to queue.");
		}
		Queued = true;
	}

	public void Complete()
	{
		if (Open && Queued)
		{
			Queued = false;
			Dispatched = true;
		}
	}

	public void CancelQueued()
	{
		Queued = false;
	}

	public void Close()
	{
		Open = false;
		Queued = false;
	}

	public object Snapshot()
	{
		return new
		{
			generation = Generation,
			round_token = RoundToken,
			boundary_tick = BoundaryTick,
			open = Open,
			queued = Queued,
			dispatched = Dispatched,
			ui_verified = false
		};
	}
}
