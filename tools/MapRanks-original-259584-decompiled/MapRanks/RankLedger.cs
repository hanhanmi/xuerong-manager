using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace MapRanks;

public sealed class RankLedger
{
	private readonly Dictionary<string, RankValue> _values = new Dictionary<string, RankValue>(StringComparer.Ordinal);

	private readonly RankConfig _config;

	private readonly Func<int, int, int> _random;

	public int Count => _values.Count;

	public int Assignments { get; private set; }

	public RankLedger(RankConfig config, Func<int, int, int>? random = null)
	{
		_config = config;
		_random = random ?? new Func<int, int, int>(RandomNumberGenerator.GetInt32);
	}

	public RankValue AssignOnce(string key, int gameMode)
	{
		if (_values.TryGetValue(key, out RankValue value))
		{
			return value;
		}
		bool flag = _config.TypeFor(gameMode) == 11;
		int num = (flag ? _config.PremierMin : _config.CompetitiveMin);
		int num2 = (flag ? _config.PremierMax : _config.CompetitiveMax);
		int num3 = _random(num, checked(num2 + 1));
		if (num3 < num || num3 > num2)
		{
			throw new InvalidOperationException("Random source returned out-of-range rank.");
		}
		RankValue rankValue = new RankValue(num3, _config.Wins, _config.TypeFor(gameMode));
		_values.Add(key, rankValue);
		Assignments++;
		return rankValue;
	}

	public bool TryGet(string key, out RankValue? value)
	{
		return _values.TryGetValue(key, out value);
	}
}
