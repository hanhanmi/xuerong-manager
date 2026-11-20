namespace MapRanks;

public static class RosterPolicy
{
	public const int SlotCount = 64;

	public static RosterDecision Decide(RosterInput x)
	{
		bool flag = x.ManagedByBotHider || x.CssIsBot;
		if (x.Slot < 0 || x.Slot >= 64)
		{
			return new RosterDecision(Include: false, flag, "slot_out_of_range");
		}
		if (!x.Valid)
		{
			return new RosterDecision(Include: false, flag, "invalid");
		}
		if (!x.IsController)
		{
			return new RosterDecision(Include: false, flag, "not_player_controller");
		}
		if (!x.Connected)
		{
			return new RosterDecision(Include: false, flag, "not_connected");
		}
		if (x.IsHltv && !x.ManagedByBotHider)
		{
			return new RosterDecision(Include: false, flag, "hltv_not_managed_bot");
		}
		bool isBot = flag;
		string reason;
		if (x.ManagedByBotHider)
		{
			reason = "managed_bot";
		}
		else
		{
			reason = (flag ? "bot" : "human");
		}
		return new RosterDecision(Include: true, isBot, reason);
	}
}
