namespace MapRanks;

public sealed record SeedInput(int Slot, bool IsBot, ulong DisplayId, bool Settled, RankValue Rank);
