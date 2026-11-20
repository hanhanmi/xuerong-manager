using System.Collections.Generic;
using System.Linq;

namespace MapRanks;

public static class SeedPlanner
{
	public static SeedPlan Plan(IEnumerable<SeedInput> inputs)
	{
		SeedInput[] source = inputs.ToArray();
		HashSet<uint> hashSet = (from x in source
			where x.DisplayId != 0
			group x by (uint)x.DisplayId into g
			where g.Count() > 1
			select g.Key).ToHashSet();
		List<WireRow> list = new List<WireRow>();
		List<int> list2 = new List<int>();
		List<int> list3 = new List<int>();
		foreach (SeedInput item in from x in source
			where x.IsBot
			orderby x.Slot
			select x)
		{
			uint num = (uint)item.DisplayId;
			if (item.Slot < 0 || item.Slot >= 64 || item.DisplayId == 0L || num == 0 || !item.Settled || hashSet.Contains(num))
			{
				list3.Add(item.Slot);
				continue;
			}
			list.Add(new WireRow((int)num, item.Rank.Rating, item.Rank.Wins, item.Rank.Type));
			list2.Add(item.Slot);
		}
		return new SeedPlan(list.ToArray(), list2.ToArray(), list3.ToArray());
	}
}
