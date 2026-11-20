using System;
using System.Collections.Generic;
using System.Linq;

namespace MapRanks;

public sealed class AutoSnapshotQueue
{
	private sealed record Sample(long Due, string Reason);

	private readonly List<Sample> _pending = new List<Sample>();

	public int Count => _pending.Count;

	public void Schedule(string reason, long tick)
	{
		if (string.IsNullOrWhiteSpace(reason) || tick < 0)
		{
			throw new ArgumentException("Invalid diagnostic sample.");
		}
		int[] array = new int[4] { 2, 8, 32, 128 };
		foreach (int num in array)
		{
			Sample item = new Sample(checked(tick + num), $"auto_{reason}_plus_{num}ticks");
			if (!_pending.Contains(item))
			{
				_pending.Add(item);
			}
		}
		while (_pending.Count > 16)
		{
			_pending.RemoveAt(0);
		}
	}

	public string[] Due(long tick)
	{
		Sample[] array = (from s in _pending
			where s.Due <= tick
			orderby s.Due
			select s).ToArray();
		Sample[] array2 = array;
		foreach (Sample item in array2)
		{
			_pending.Remove(item);
		}
		return array.Select((Sample s) => s.Reason).ToArray();
	}

	public void Clear()
	{
		_pending.Clear();
	}
}
