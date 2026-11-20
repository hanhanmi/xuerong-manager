namespace MapRanks;

public sealed class MatchCycleSignals
{
	private long _lastBeginRound = -1L;

	public bool AwaitingNextMatch { get; private set; }

	public long ConsumedCycles { get; private set; }

	public void Awards()
	{
		AwaitingNextMatch = true;
	}

	public void MatchEndRestart()
	{
		AwaitingNextMatch = true;
	}

	public bool ConsumeNextRound()
	{
		if (!AwaitingNextMatch)
		{
			return false;
		}
		AwaitingNextMatch = false;
		ConsumedCycles++;
		return true;
	}

	public bool AcceptBegin(long roundToken)
	{
		if (roundToken < 0 || _lastBeginRound == roundToken)
		{
			return false;
		}
		_lastBeginRound = roundToken;
		return true;
	}

	public void Clear()
	{
		AwaitingNextMatch = false;
		_lastBeginRound = -1L;
		ConsumedCycles = 0L;
	}

	public object Snapshot()
	{
		return new
		{
			awaiting_next_match = AwaitingNextMatch,
			last_begin_round = _lastBeginRound,
			consumed_cycles = ConsumedCycles
		};
	}
}
