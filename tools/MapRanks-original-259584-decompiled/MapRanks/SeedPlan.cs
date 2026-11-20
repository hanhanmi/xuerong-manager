namespace MapRanks;

public sealed record SeedPlan(WireRow[] Rows, int[] SentSlots, int[] BlockedSlots);
