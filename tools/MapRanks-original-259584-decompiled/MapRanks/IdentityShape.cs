namespace MapRanks;

public readonly record struct IdentityShape(int Universe, int AccountType, uint Instance, bool AccountIdZero)
{
	public static IdentityShape Read(ulong id)
	{
		return new IdentityShape((int)(id >> 56), (int)((id >> 52) & 0xF), (uint)((id >> 32) & 0xFFFFF), (int)id == 0);
	}
}
