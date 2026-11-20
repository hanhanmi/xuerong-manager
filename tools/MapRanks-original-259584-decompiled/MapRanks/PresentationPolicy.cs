namespace MapRanks;

public static class PresentationPolicy
{
	public static bool CanPublish(string phase)
	{
		bool flag = ((phase == "mvp" || phase == "awards") ? true : false);
		return !flag;
	}

	public static bool TabCanRequest(string phase, bool optIn)
	{
		if (optIn)
		{
			return CanPublish(phase);
		}
		return false;
	}
}
