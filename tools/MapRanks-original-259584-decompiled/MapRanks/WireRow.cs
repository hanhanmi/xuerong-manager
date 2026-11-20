namespace MapRanks;

public readonly struct WireRow(int accountId, int rating, int wins, int type)
{
	public readonly int AccountId = accountId;

	public readonly int Rating = rating;

	public readonly int Wins = wins;

	public readonly int Type = type;
}
