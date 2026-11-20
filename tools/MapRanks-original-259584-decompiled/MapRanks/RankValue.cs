namespace MapRanks;

public sealed record RankValue(int Rating, int Wins, sbyte Type)
{
	public int PredictedWin => Rating;

	public int PredictedLoss => Rating;

	public int PredictedTie => Rating;
}
