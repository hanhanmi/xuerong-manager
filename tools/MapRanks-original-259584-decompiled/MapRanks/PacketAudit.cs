namespace MapRanks;

public sealed record PacketAudit(bool Match, int ParsedRows, int ExpectedRows, string Reason);
