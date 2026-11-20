namespace MapRanks;

public readonly record struct RosterInput(int Slot, bool Valid, bool IsController, bool Connected, bool ManagedByBotHider, bool CssIsBot, bool IsHltv);
