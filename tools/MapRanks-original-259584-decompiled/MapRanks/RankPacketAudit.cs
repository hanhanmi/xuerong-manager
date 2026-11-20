using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MapRanks;

public static class RankPacketAudit
{
	private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(40L);

	public static ParsedRank[] Parse(string text)
	{
		if (text.Length > 65536)
		{
			throw new ArgumentException("rank_debug_text_too_long");
		}
		MatchCollection matchCollection = Regex.Matches(text, "rank_update\\s*\\{([^{}]*)\\}", RegexOptions.CultureInvariant, Timeout);
		if (matchCollection.Count > 64)
		{
			throw new ArgumentException("rank_debug_too_many_records");
		}
		return (from Match m in matchCollection
			select new ParsedRank(Number(m.Groups[1].Value, "account_id"), Number(m.Groups[1].Value, "rank_old"), Number(m.Groups[1].Value, "rank_new"), Number(m.Groups[1].Value, "num_wins"), Number(m.Groups[1].Value, "rank_type_id"))).ToArray();
	}

	private static int? Number(string text, string field)
	{
		if (!long.TryParse(Regex.Match(text, "(?m)^\\s*" + Regex.Escape(field) + "\\s*:\\s*(-?\\d+)\\s*$", RegexOptions.CultureInvariant, Timeout).Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			return null;
		}
		if (result < int.MinValue || result > uint.MaxValue)
		{
			return null;
		}
		return (int)result;
	}

	public static PacketAudit Compare(ParsedRank[] parsed, IReadOnlyCollection<WireRow> expected, int reportedCount)
	{
		if (reportedCount != expected.Count || parsed.Length != expected.Count)
		{
			return new PacketAudit(Match: false, parsed.Length, expected.Count, "row_count_mismatch");
		}
		if (parsed.Any((ParsedRank x) => !x.AccountId.HasValue || !x.OldRank.HasValue || !x.NewRank.HasValue || !x.Wins.HasValue || !x.Type.HasValue))
		{
			return new PacketAudit(Match: false, parsed.Length, expected.Count, "required_field_missing");
		}
		if (parsed.Select((ParsedRank x) => x.AccountId).Distinct().Count() != parsed.Length)
		{
			return new PacketAudit(Match: false, parsed.Length, expected.Count, "duplicate_account");
		}
		Dictionary<int, ParsedRank> dictionary = parsed.ToDictionary((ParsedRank x) => x.AccountId.Value);
		foreach (WireRow item in expected)
		{
			if (!dictionary.TryGetValue(item.AccountId, out var value))
			{
				return new PacketAudit(Match: false, parsed.Length, expected.Count, "account_mismatch");
			}
			if (value.OldRank != item.Rating || value.NewRank != item.Rating || value.Wins != item.Wins || value.Type != item.Type)
			{
				return new PacketAudit(Match: false, parsed.Length, expected.Count, "rank_fields_mismatch");
			}
		}
		return new PacketAudit(Match: true, parsed.Length, expected.Count, "exact_at_pre_dispatch_not_client_ack");
	}
}
