using System;

namespace MapRanks;

public sealed class FirstLiveRecoveryGate
{
	public long Generation { get; private set; }

	public long RoundToken { get; private set; }

	public string Reason { get; private set; } = "not_armed";

	public bool Armed { get; private set; }

	public bool Queued { get; private set; }

	public bool WaitingForRound { get; private set; } = true;

	public bool RoundOpen { get; private set; }

	public bool FreezeObserved { get; private set; }

	public bool EndEventObserved { get; private set; }

	public long TransitionTick { get; private set; } = -1L;

	public LiveRules Rules { get; private set; }

	public string State
	{
		get
		{
			if (Armed)
			{
				if (!WaitingForRound)
				{
					if (RoundOpen)
					{
						if (!Queued)
						{
							if (Rules.Known)
							{
								if (!Rules.Warmup)
								{
									if (!Rules.Freeze)
									{
										if (TransitionTick >= 0)
										{
											return "waiting_stages_or_rows";
										}
										return "waiting_live_edge";
									}
									return "waiting_freeze_end";
								}
								return "waiting_warmup_end";
							}
							return "waiting_rules";
						}
						return "ordered_publication_pending";
					}
					return "waiting_next_round";
				}
				return "waiting_round_start";
			}
			return "idle_or_completed";
		}
	}

	public void Arm(string reason, long tick)
	{
		if (string.IsNullOrWhiteSpace(reason) || tick < 0)
		{
			throw new ArgumentException("Invalid reset event.");
		}
		Generation++;
		Reason = reason;
		Armed = true;
		Queued = false;
		WaitingForRound = true;
		RoundOpen = false;
		ResetRoundEvidence();
	}

	public void ArmCurrentRound(string reason, long tick)
	{
		bool waitingForRound = WaitingForRound;
		bool roundOpen = RoundOpen;
		long roundToken = RoundToken;
		Arm(reason, tick);
		WaitingForRound = waitingForRound;
		RoundOpen = roundOpen;
		RoundToken = roundToken;
	}

	public void StartRound(long tick)
	{
		if (tick < 0)
		{
			throw new ArgumentOutOfRangeException("tick");
		}
		RoundToken++;
		WaitingForRound = false;
		RoundOpen = true;
		Queued = false;
		ResetRoundEvidence();
	}

	public void EndRound()
	{
		RoundOpen = false;
		Queued = false;
		ResetRoundEvidence();
	}

	private void ResetRoundEvidence()
	{
		FreezeObserved = false;
		EndEventObserved = false;
		TransitionTick = -1L;
		Rules = default;
	}

	public void FreezeEndEvent(long tick)
	{
		if (tick < 0)
		{
			throw new ArgumentOutOfRangeException("tick");
		}
		if (Armed && !WaitingForRound && RoundOpen)
		{
			EndEventObserved = true;
		}
	}

	public void Observe(long tick, LiveRules rules)
	{
		Rules = rules;
		if (Armed && !WaitingForRound && RoundOpen && !Queued && rules.Known)
		{
			if (rules.Warmup)
			{
				TransitionTick = -1L;
				FreezeObserved = false;
				EndEventObserved = false;
			}
			else if (rules.Freeze)
			{
				FreezeObserved = true;
				TransitionTick = -1L;
			}
			else if ((FreezeObserved || EndEventObserved) && TransitionTick < 0)
			{
				TransitionTick = tick;
			}
		}
	}

	public bool Ready(long tick, long postThinkTick, long transmitTick, bool canPublish)
	{
		if (((Armed && !Queued && !WaitingForRound && RoundOpen) & canPublish) && Rules.Known && !Rules.Freeze && !Rules.Warmup && TransitionTick >= 0 && tick > TransitionTick && postThinkTick >= TransitionTick)
		{
			return transmitTick >= TransitionTick;
		}
		return false;
	}

	public void MarkQueued()
	{
		if (!Armed || Queued || TransitionTick < 0 || !RoundOpen)
		{
			throw new InvalidOperationException("No live transition ready to queue.");
		}
		Queued = true;
	}

	public void Complete()
	{
		if (Queued)
		{
			Armed = false;
			Queued = false;
		}
	}

	public void CancelQueued()
	{
		Queued = false;
	}

	public object Snapshot()
	{
		return new
		{
			generation = Generation,
			reason = Reason,
			state = State,
			armed = Armed,
			queued = Queued,
			waiting_round = WaitingForRound,
			round_open = RoundOpen,
			round_token = RoundToken,
			freeze_observed = FreezeObserved,
			end_event_observed = EndEventObserved,
			transition_tick = TransitionTick,
			rules = Rules
		};
	}
}
