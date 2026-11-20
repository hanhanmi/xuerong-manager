using System.Collections.Generic;
using System.Linq;

namespace MapRanks;

public sealed class RevealQueue
{
	private readonly Dictionary<int, long> _pending = new Dictionary<int, long>();

	private readonly Dictionary<int, long> _lastSent = new Dictionary<int, long>();

	private readonly int _cooldown;

	public int PendingCount => _pending.Count;

	public RevealQueue(int cooldown)
	{
		_cooldown = cooldown;
	}

	public void Request(int slot, long due)
	{
		if (slot >= 0 && slot < 64 && (!_pending.TryGetValue(slot, out var value) || due < value))
		{
			_pending[slot] = due;
		}
	}

	public int[] Ready(long tick, IReadOnlySet<int> connectedHumans)
	{
		int[] array = _pending.Keys.Where((int s) => !connectedHumans.Contains(s)).ToArray();
		foreach (int key in array)
		{
			_pending.Remove(key);
		}
		return (from kv in _pending
			where kv.Value <= tick && (!_lastSent.TryGetValue(kv.Key, out var value) || tick - value >= _cooldown)
			select kv.Key into s
			orderby s
			select s).ToArray();
	}

	public void MarkSent(IEnumerable<int> slots, long tick)
	{
		foreach (int slot in slots)
		{
			_pending.Remove(slot);
			_lastSent[slot] = tick;
		}
	}

	public void Drop(int slot)
	{
		_pending.Remove(slot);
		_lastSent.Remove(slot);
	}

	public void Clear()
	{
		_pending.Clear();
		_lastSent.Clear();
	}
}
