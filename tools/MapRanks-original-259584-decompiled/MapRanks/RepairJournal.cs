using System;
using System.Collections.Generic;

namespace MapRanks;

public sealed class RepairJournal
{
	private readonly Queue<FieldRepair> _recent = new Queue<FieldRepair>();

	private readonly Dictionary<string, long> _byStage = new Dictionary<string, long>(StringComparer.Ordinal);

	private readonly Dictionary<string, long> _byField = new Dictionary<string, long>(StringComparer.Ordinal);

	private readonly int _capacity;

	public long Total { get; private set; }

	public long InitialWrites { get; private set; }

	public long DriftWrites => Total - InitialWrites;

	public long Discarded => Math.Max(0L, Total - _capacity);

	public RepairJournal(int capacity = 128)
	{
		if (capacity < 1 || capacity > 4096)
		{
			throw new ArgumentOutOfRangeException("capacity");
		}
		_capacity = capacity;
	}

	public void Record(long tick, int slot, string field, int before, int after, string stage, bool initial)
	{
		if (before != after)
		{
			Total++;
			if (initial)
			{
				InitialWrites++;
			}
			_byStage[stage] = _byStage.GetValueOrDefault(stage) + 1;
			_byField[field] = _byField.GetValueOrDefault(field) + 1;
			if (_recent.Count == _capacity)
			{
				_recent.Dequeue();
			}
			_recent.Enqueue(new FieldRepair(Total, tick, slot, field, before, after, stage, initial));
		}
	}

	public FieldRepair[] Recent()
	{
		return _recent.ToArray();
	}

	public Dictionary<string, long> StageCounts()
	{
		return new Dictionary<string, long>(_byStage, StringComparer.Ordinal);
	}

	public Dictionary<string, long> FieldCounts()
	{
		return new Dictionary<string, long>(_byField, StringComparer.Ordinal);
	}
}
