using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapRanks;

public sealed class RankConfig
{
	public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		PropertyNameCaseInsensitive = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
	};

	[JsonPropertyName("version")]
	public int Version { get; set; } = 2;

	[JsonPropertyName("enabled")]
	public bool Enabled { get; set; } = true;

	[JsonPropertyName("mode")]
	public string Mode { get; set; } = "premier";

	[JsonPropertyName("premier_min")]
	public int PremierMin { get; set; } = 1000;

	[JsonPropertyName("premier_max")]
	public int PremierMax { get; set; } = 30000;

	[JsonPropertyName("competitive_min")]
	public int CompetitiveMin { get; set; } = 1;

	[JsonPropertyName("competitive_max")]
	public int CompetitiveMax { get; set; } = 18;

	[JsonPropertyName("wins")]
	public int Wins { get; set; } = 10;

	[JsonPropertyName("bot_identity")]
	public string BotIdentity { get; set; } = "external";

	[JsonPropertyName("allow_wingman")]
	public bool AllowWingman { get; set; }

	[JsonPropertyName("repair_interval_ticks")]
	public int RepairIntervalTicks { get; set; } = 1;

	[JsonPropertyName("roster_scan_ticks")]
	public int RosterScanTicks { get; set; } = 16;

	[JsonPropertyName("reveal_cooldown_ticks")]
	public int RevealCooldownTicks { get; set; } = 8;

	[JsonPropertyName("trace_tab_edges")]
	public bool TraceTabEdges { get; set; } = true;

	[JsonPropertyName("observe_rank_messages")]
	public bool ObserveRankMessages { get; set; } = true;

	[JsonPropertyName("diagnostics_enabled")]
	public bool DiagnosticsEnabled { get; set; }

	[JsonPropertyName("identity_settle_ticks")]
	public int IdentitySettleTicks { get; set; } = 8;

	[JsonPropertyName("reveal_on_tab")]
	public bool RevealOnTab { get; set; }

	[JsonPropertyName("first_live_recovery")]
	public bool FirstLiveRecovery { get; set; } = true;

	[JsonPropertyName("reset_freeze_recovery")]
	public bool ResetFreezeRecovery { get; set; } = true;

	public string SerializeUserConfig()
	{
		return JsonSerializer.Serialize(new Dictionary<string, object>
		{
			["enabled"] = Enabled,
			["mode"] = Mode,
			["premier_min"] = PremierMin,
			["premier_max"] = PremierMax,
			["competitive_min"] = CompetitiveMin,
			["competitive_max"] = CompetitiveMax,
			["wins"] = Wins,
			["allow_wingman"] = AllowWingman
		}, JsonOptions);
	}

	public void Validate()
	{
		Mode = (Mode ?? "").Trim().ToLowerInvariant();
		BotIdentity = (BotIdentity ?? "").Trim().ToLowerInvariant();
		bool flag = Version == 1;
		string botIdentity;
		if (flag)
		{
			botIdentity = BotIdentity;
			bool flag2 = ((botIdentity == "persistent" || botIdentity == "native") ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			Version = 2;
			BotIdentity = "external";
		}
		if (Version != 2)
		{
			throw new ArgumentException("Unsupported config version.");
		}
		botIdentity = Mode;
		if ((!(botIdentity == "premier") && !(botIdentity == "competitive")) || 1 == 0)
		{
			throw new ArgumentException("mode must be premier or competitive.");
		}
		if (BotIdentity != "external")
		{
			throw new ArgumentException("T02 only supports external read-only BOT identities.");
		}
		if (IdentitySettleTicks < 1 || IdentitySettleTicks > 128)
		{
			throw new ArgumentException("identity_settle_ticks must be 1..128.");
		}
		if (PremierMin < 1 || PremierMax > 999999 || PremierMin > PremierMax)
		{
			throw new ArgumentException("Invalid premier range (1..999999).");
		}
		if (CompetitiveMin < 1 || CompetitiveMax > 18 || CompetitiveMin > CompetitiveMax)
		{
			throw new ArgumentException("Invalid competitive range (1..18).");
		}
		if (Wins < 1 || Wins > 1000000)
		{
			throw new ArgumentException("wins must be 1..1000000.");
		}
		if (RepairIntervalTicks < 1 || RepairIntervalTicks > 64)
		{
			throw new ArgumentException("repair_interval_ticks must be 1..64.");
		}
		if (RosterScanTicks < 1 || RosterScanTicks > 128)
		{
			throw new ArgumentException("roster_scan_ticks must be 1..128.");
		}
		if (RevealCooldownTicks < 1 || RevealCooldownTicks > 64)
		{
			throw new ArgumentException("reveal_cooldown_ticks must be 1..64.");
		}
	}

	public bool AllowsMode(int type, int mode)
	{
		if (type == 0)
		{
			return mode switch
			{
				2 => AllowWingman, 
				1 => true, 
				_ => false, 
			};
		}
		return false;
	}

	public sbyte TypeFor(int gameMode)
	{
		if (gameMode != 2)
		{
			if (!(Mode == "premier"))
			{
				return 12;
			}
			return 11;
		}
		return 7;
	}
}
