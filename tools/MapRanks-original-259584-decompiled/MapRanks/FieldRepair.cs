namespace MapRanks;

public sealed record FieldRepair(long Sequence, long Tick, int Slot, string Field, int Before, int After, string Stage, bool Initial);
