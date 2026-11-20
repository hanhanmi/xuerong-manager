using System;
using System.Collections.Generic;

namespace MapRanks;

public sealed class BotKeyCache
{
	private readonly Dictionary<uint, string> _keys = new Dictionary<uint, string>();

	public string GetOrAdd(uint rawHandle, Func<string> create)
	{
		if (_keys.TryGetValue(rawHandle, out string value))
		{
			return value;
		}
		value = create();
		if (string.IsNullOrEmpty(value))
		{
			throw new InvalidOperationException("Empty BOT logical key.");
		}
		_keys.Add(rawHandle, value);
		return value;
	}

	public void Remember(uint rawHandle, string existingKey)
	{
		if (string.IsNullOrEmpty(existingKey))
		{
			throw new ArgumentException("Empty BOT logical key.");
		}
		_keys.TryAdd(rawHandle, existingKey);
	}

	public void Clear()
	{
		_keys.Clear();
	}
}
