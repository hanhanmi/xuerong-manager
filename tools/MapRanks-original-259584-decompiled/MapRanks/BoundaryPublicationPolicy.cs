namespace MapRanks;

public static class BoundaryPublicationPolicy
{
	public static bool SeedBots(string reason)
	{
		return reason == "round_start";
	}

	public static bool RevealAll(string reason)
	{
		return false;
	}
}
