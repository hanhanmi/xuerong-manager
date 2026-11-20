using System;
using System.Collections.Generic;
using System.Linq;

namespace MapRanks;

public sealed class BotBootstrapTracker
{
	private sealed record Pending(string Fingerprint, long Due);

	private readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();

	private readonly Dictionary<int, string> _revealed = new Dictionary<int, string>();

	private readonly int _delay;

	public int PendingCount => _pending.Count;

	public BotBootstrapTracker(int delayTicks = 2)
	{
		if (delayTicks < 1 || delayTicks > 128)
		{
			throw new ArgumentOutOfRangeException("delayTicks");
		}
		_delay = delayTicks;
	}

	public static string Fingerprint(IEnumerable<WireRow> rows)
	{
		return string.Join(";", from r in rows
			orderby (uint)r.AccountId
			select FormattableString.Invariant($"{r.AccountId}:{r.Rating}:{r.Wins}:{r.Type}"));
	}

	public int SeedEnqueued(IEnumerable<int> viewers, string fingerprint, long tick)
	{
		if (string.IsNullOrEmpty(fingerprint))
		{
			return 0;
		}
		int num = 0;
		foreach (int item in viewers.Distinct())
		{
			if (item < 0 || item >= 64)
			{
				throw new ArgumentOutOfRangeException("viewers");
			}
			if ((!_revealed.TryGetValue(item, out string value) || !(value == fingerprint)) && (!_pending.TryGetValue(item, out Pending value2) || !(value2.Fingerprint == fingerprint)))
			{
				_pending[item] = new Pending(fingerprint, checked(tick + _delay));
				num++;
			}
		}
		return num;
	}

	public int[] Ready(long tick, ISet<int> connected, string fingerprint, bool canPublish)
	{
		int[] array = _pending.Keys.Where((int s) => !connected.Contains(s)).ToArray();
		foreach (int slot in array)
		{
			Drop(slot);
		}
		if (!canPublish || string.IsNullOrEmpty(fingerprint))
		{
			return Array.Empty<int>();
		}
		return (from kv in _pending
			where kv.Value.Fingerprint == fingerprint && kv.Value.Due <= tick
			select kv.Key into s
			orderby s
			select s).ToArray();
	}

	public bool WaitingForOrder(int slot, string fingerprint, long tick)
	{
		if (_pending.TryGetValue(slot, out Pending value) && value.Fingerprint == fingerprint)
		{
			return tick < value.Due;
		}
		return false;
	}

	public void Revealed(IEnumerable<int> viewers, string fingerprint, long tick)
	{
		foreach (int viewer in viewers)
		{
			if (_pending.TryGetValue(viewer, out Pending value) && !(value.Fingerprint != fingerprint) && value.Due <= tick)
			{
				_revealed[viewer] = fingerprint;
				_pending.Remove(viewer);
			}
		}
	}

	public void CancelPending()
	{
		_pending.Clear();
	}

	public void Drop(int slot)
	{
		_pending.Remove(slot);
		_revealed.Remove(slot);
	}

	public void Clear()
	{
		_pending.Clear();
		_revealed.Clear();
	}
}
