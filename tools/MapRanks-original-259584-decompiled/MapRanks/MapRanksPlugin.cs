using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;

namespace MapRanks;

public sealed class MapRanksPlugin : BasePlugin
{
	private sealed record BotReservation(string Name, string Key, ulong DisplayXuid);

	private sealed record PlayerState(int Rating, int Wins, sbyte Type, int Win, int Loss, int Tie, ulong Xuid);

	private sealed class Binding
	{
		public required CCSPlayerController Player { get; init; }

		public required uint RawHandle { get; init; }

		public required int Slot { get; init; }

		public required bool IsBot { get; set; }

		public required string Name { get; init; }

		public required string Key { get; init; }

		public required RankValue Rank { get; init; }

		public required PlayerState Original { get; init; }

		public bool ManagedByBotHider { get; set; }

		public ulong DisplayXuid { get; set; }

		public string IdentitySource { get; set; } = "waiting";

		public int Errors { get; set; }
	}

	private sealed record RosterObservation(CCSPlayerController? Player, RosterInput Input, RosterDecision Decision, uint RawHandle, string Error);

	private RankConfig _config = new RankConfig();

	private RankLedger? _ledger;

	private RevealQueue _reveals = new RevealQueue(8);

	private RevealQueue _seeds = new RevealQueue(8);

	private BotBootstrapTracker _bootstrap = new BotBootstrapTracker();

	private readonly FirstLiveRecoveryGate _firstLive = new FirstLiveRecoveryGate();

	private readonly MatchCycleSignals _matchCycles = new MatchCycleSignals();

	private readonly ResetFreezeGate _freezeGate = new ResetFreezeGate();

	private readonly Dictionary<int, long> _freezeRecoveryViewers = new Dictionary<int, long>();

	private readonly AutoSnapshotQueue _autoSnapshots = new AutoSnapshotQueue();

	private long _freezeRecoveryQueued;

	private long _freezeRecoveryCompleted;

	private readonly Queue<object> _visibilityEdges = new Queue<object>();

	private readonly Dictionary<int, long> _liveRecoveryViewers = new Dictionary<int, long>();

	private CCSGameRulesProxy? _rulesProxy;

	private long _liveRecoveryQueued;

	private long _liveRecoveryCompleted;

	private long _probeCount;

	private double _lastProbe = -10.0;

	private string _rulesSignature = "";

	private WireRow[]? _expectedWireRows;

	private int[] _expectedWireRecipients = Array.Empty<int>();

	private long _bootstrapBatches;

	private long _ownUpdatePre;

	private long _ownRowsPre;

	private long _ownRevealPre;

	private long _packetMismatches;

	private long _observerSkippedThread;

	private string _lastPacketAudit = "not_observed";

	private readonly RepairJournal _repairs = new RepairJournal();

	private long _lastJournalLogged;

	private long _transmitCalls;

	private long _transmitSkippedThread;

	private long _lastTransmitTick = -100L;

	private int _gameThreadId;

	private bool _insideTransmit;

	private string? _deferredTransmitError;

	private string _rosterSignature = "";

	private long _rosterScans;

	private bool _transmitListenerRegistered;

	private readonly BotHiderReader _botHider = new BotHiderReader();

	private readonly RankWire _wire = new RankWire();

	private readonly Dictionary<int, (ulong Xuid, long Since)> _stableIds = new Dictionary<int, (ulong, long)>();

	private string _seedFingerprint = "";

	private long _wireBatches;

	private long _wireRows;

	private long _tabSuppressed;

	private long _revealsObserved;

	private bool _insideWireSend;

	private bool _insideRevealSend;

	private int _wireAttempts;

	private readonly MapEpochTracker _maps = new MapEpochTracker();

	private readonly Stopwatch _clock = Stopwatch.StartNew();

	private readonly Dictionary<int, Binding> _bindings = new Dictionary<int, Binding>();

	private readonly BotKeyCache _botKeys = new BotKeyCache();

	private readonly Dictionary<int, BotReservation> _restartBots = new Dictionary<int, BotReservation>();

	private bool _sameMapCyclePending;

	private bool _competitiveTabWarmupDone;

	private readonly Dictionary<int, bool> _tabHeld = new Dictionary<int, bool>();

	private readonly HashSet<string> _once = new HashSet<string>();

	private readonly HashSet<int> _observedMessageIds = new HashSet<int>();

	private readonly byte[] _logSalt = RandomNumberGenerator.GetBytes(16);

	private UserMessage? _revealMessage;

	private string _blocked = "awaiting_map";

	private string _phase = "startup";

	private string _logPath = "";

	private int _logPart;

	private long _tick;

	private long _botSerial;

	private long _fieldsChanged;

	private long _revealsSent;

	private long _tabEdges;

	private long _roundStarts;

	private long _freezeEnds;

	private long _mvpEvents;

	private long _postThinkCalls;

	private long _updatesObserved;

	private long _lastPostThinkTick = -100L;

	private double _lastDump = -10.0;

	private double _lastMark = -10.0;

	private double _lastRepairLog = -10.0;

	private double _lastRestartValue;

	private int _gameType = -1;

	private int _gameMode = -1;

	private int _lockedMode = -1;

	private int _messageHookAttempts;

	private int _sendFailures;

	private bool _active;

	private bool _transition;

	private bool _schemaReady;

	private bool _messageHooksReady;

	private bool _disposed;

	private bool _rosterNeeded = true;

	private ConVar? _restartCvar;

	private static readonly string[] RankFields = new string[6] { "m_iCompetitiveRanking", "m_iCompetitiveWins", "m_iCompetitiveRankType", "m_iCompetitiveRankingPredicted_Win", "m_iCompetitiveRankingPredicted_Loss", "m_iCompetitiveRankingPredicted_Tie" };

	public override string ModuleName => "MapRanks T02.6";

	public override string ModuleVersion => "0.2.6-test02.6";

	public override string ModuleAuthor => "MapRanks rebuild";

	public override string ModuleDescription => "Map-scoped ranks with read-only BotHider integration and account-keyed BOT cache seeding.";

	private bool ProtectedPresentation => !PresentationPolicy.CanPublish(_phase);

	private int CompetitiveSeedDelayTicks
	{
		get
		{
			if (!(_config.Mode == "competitive"))
			{
				return 2;
			}
			return 1;
		}
	}

	private int CompetitiveIdentitySettleTicks
	{
		get
		{
			if (!(_config.Mode == "competitive"))
			{
				return _config.IdentitySettleTicks;
			}
			return 1;
		}
	}

	public override void Load(bool hotReload)
	{
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Expected Obj, but got Unknown
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Expected Obj, but got Unknown
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0054: Expected Obj, but got Unknown
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Expected Obj, but got Unknown
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Expected Obj, but got Unknown
		//IL_034f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0359: Expected Obj, but got Unknown
		//IL_0361: Unknown result type (might be due to invalid IL or missing references)
		//IL_036b: Expected Obj, but got Unknown
		//IL_0485: Unknown result type (might be due to invalid IL or missing references)
		//IL_0490: Expected Obj, but got Unknown
		//IL_04ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_04b6: Expected Obj, but got Unknown
		//IL_02bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c9: Expected Obj, but got Unknown
		//IL_02d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_02db: Expected Obj, but got Unknown
		//IL_02e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ed: Expected Obj, but got Unknown
		//IL_02f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ff: Expected Obj, but got Unknown
		//IL_0307: Unknown result type (might be due to invalid IL or missing references)
		//IL_0311: Expected Obj, but got Unknown
		//IL_0319: Unknown result type (might be due to invalid IL or missing references)
		//IL_0323: Expected Obj, but got Unknown
		((BasePlugin)this).AddCommand("css_mapranks_status", "Read MapRanks status (does not assign ranks)", (CommandCallback)StatusCommand);
		((BasePlugin)this).AddCommand("css_mapranks_dump", "Write a read-only MapRanks diagnostic snapshot", (CommandCallback)DumpCommand);
		((BasePlugin)this).AddCommand("css_mapranks_refresh", "Reveal existing ranks once; never rerolls values", (CommandCallback)RefreshCommand);
		((BasePlugin)this).AddCommand("css_mapranks_probe", "LIVE-only diagnostic: seed, reveal or pair; never changes team/identity/rank", (CommandCallback)ProbeCommand);
		((BasePlugin)this).AddCommand("css_mapranks_mark", "Add a timestamped test label to the diagnostic log", (CommandCallback)MarkCommand);
		try
		{
			string path = Path.Combine(((BasePlugin)this).ModuleDirectory, "MapRanks.json");
			if (!File.Exists(path))
			{
				File.WriteAllText(path, _config.SerializeUserConfig());
			}
			_config = JsonSerializer.Deserialize<RankConfig>(File.ReadAllText(path), RankConfig.JsonOptions) ?? throw new InvalidOperationException("Config was null.");
			_config.Validate();
			_bootstrap = new BotBootstrapTracker((_config.Mode == "competitive") ? 1 : 2);
			if (_config.DiagnosticsEnabled)
			{
				string text = Path.Combine(((BasePlugin)this).ModuleDirectory, "logs");
				Directory.CreateDirectory(text);
				_logPath = Path.Combine(text, $"trace_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.jsonl");
			}
			else
			{
				_logPath = "";
			}
			Log("load", new
			{
				version = ((BasePlugin)this).ModuleVersion,
				api = typeof(BasePlugin).Assembly.GetName().Version?.ToString(),
				hotReload = hotReload
			});
			Version version = typeof(BasePlugin).Assembly.GetName().Version;
			if (version == null || version < new Version(1, 0, 376, 0))
			{
				throw new InvalidOperationException("T02 requires CounterStrikeSharp API 1.0.376 or later; validate newer releases separately.");
			}
			_reveals = new RevealQueue(_config.RevealCooldownTicks);
			_seeds = new RevealQueue(_config.RevealCooldownTicks);
			if (hotReload)
			{
				throw new InvalidOperationException("T02 requires a cold CS2/server restart; hot reload is not supported.");
			}
			if (!_config.Enabled)
			{
				_blocked = "disabled_in_config";
				return;
			}
			if (CoreConfig.FollowCS2ServerGuidelines)
			{
				throw new InvalidOperationException("FollowCS2ServerGuidelines=true blocks rank fields. See README; global config was NOT changed.");
			}
		}
		catch (Exception ex)
		{
			_blocked = ex.Message;
			Log("startup_blocked", new
			{
				reason = _blocked
			});
			Server.PrintToConsole("[MapRanks] BLOCKED: " + _blocked);
			SaveSnapshot("startup_blocked");
			return;
		}
		((BasePlugin)this).RegisterListener<OnMapStart>((OnMapStart)OnMapStart);
		((BasePlugin)this).RegisterListener<OnMapEnd>((OnMapEnd)OnMapEnd);
		((BasePlugin)this).RegisterListener<OnServerPrecacheResources>((OnServerPrecacheResources)((ResourceManifest _) =>
		{
			Log("resource_precache", new { });
		}));
		((BasePlugin)this).RegisterListener<OnTick>((OnTick)OnTick);
		((BasePlugin)this).RegisterListener<OnServerPostEntityThink>((OnServerPostEntityThink)OnPostThink);
		try
		{
			((BasePlugin)this).RegisterListener<CheckTransmit>((CheckTransmit)((CCheckTransmitInfoList _) =>
			{
				OnCheckTransmit();
			}));
			_transmitListenerRegistered = true;
		}
		catch (Exception ex2)
		{
			Log("check_transmit_registration_failed", new
			{
				error = ex2.Message
			});
		}
		((BasePlugin)this).RegisterListener<OnClientPutInServer>((OnClientPutInServer)OnPutInServer);
		((BasePlugin)this).RegisterListener<OnClientDisconnect>((OnClientDisconnect)OnDisconnect);
		((BasePlugin)this).RegisterEventHandler<EventRoundStart>((GameEventHandler<EventRoundStart>)OnRoundStart, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventRoundFreezeEnd>((GameEventHandler<EventRoundFreezeEnd>)OnFreezeEnd, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventRoundEnd>((GameEventHandler<EventRoundEnd>)OnRoundEnd, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventRoundMvp>((GameEventHandler<EventRoundMvp>)OnRoundMvp, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventCsWinPanelMatch>((GameEventHandler<EventCsWinPanelMatch>)OnMatchAwards, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventPlayerSpawn>((GameEventHandler<EventPlayerSpawn>)OnSpawn, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventPlayerTeam>((GameEventHandler<EventPlayerTeam>)OnPlayerTeam, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventCsMatchEndRestart>((GameEventHandler<EventCsMatchEndRestart>)OnMatchEndRestart, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventCsPreRestart>((GameEventHandler<EventCsPreRestart>)OnPreRestartEvent, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventBeginNewMatch>((GameEventHandler<EventBeginNewMatch>)OnBeginNewMatch, (HookMode)1);
		string[] array = new string[4] { "map", "changelevel", "ds_workshop_changelevel", "host_workshop_map" };
		foreach (string text2 in array)
		{
			string captured = text2;
			((BasePlugin)this).AddCommandListener(captured, (CommandListenerCallback)((CCSPlayerController? caller, CommandInfo info) =>
			{
				//IL_000d: Unknown result type (might be due to invalid IL or missing references)
				return ObserveReload(captured, info);
			}), (HookMode)0);
		}
		((BasePlugin)this).AddCommandListener("mp_restartgame", (CommandListenerCallback)ObserveRestart, (HookMode)0);
		_restartCvar = ConVar.Find("mp_restartgame");
		Server.NextWorldUpdate((Action)(() =>
		{
			if (!_disposed)
			{
				string mapName = Server.MapName;
				if (!string.IsNullOrWhiteSpace(mapName) && _maps.Epoch == 0L)
				{
					OnMapStart(mapName);
				}
			}
		}));
		Server.PrintToConsole("[MapRanks] T02.6 loaded. Diagnostics save automatically; after testing run 02_COLLECT_RETURN.cmd. UI not yet validated.");
	}

	public override void Unload(bool hotReload)
	{
		_disposed = true;
		CancelLivePublication("unload");
		SaveSnapshot("plugin_unload_before_restore");
		RestoreAll("plugin_unload");
		Log("unload", new { hotReload });
		UserMessage? revealMessage = _revealMessage;
		if (revealMessage != null)
		{
			revealMessage.Dispose();
		}
		_revealMessage = null;
		_reveals.Clear();
		_seeds.Clear();
		_wire.Dispose();
		_bootstrap.Clear();
		_freezeGate.Close();
	}

	public override void OnAllPluginsLoaded(bool hotReload)
	{
		_botHider.Resolve();
		Log("bothider_api", _botHider.Diagnostics());
		_rosterNeeded = true;
	}

	private HookResult OnMatchAwards(EventCsWinPanelMatch ev, GameEventInfo info)
	{
		_phase = "awards";
		_matchCycles.Awards();
		_competitiveTabWarmupDone = false;
		_maps.MarkRestart(_clock.Elapsed.TotalSeconds, 60.0);
		CaptureRestartBots();
		_sameMapCyclePending = true;
		CancelLivePublication("match_awards");
		_firstLive.EndRound();
		Log("match_awards_open", new
		{
			policy = "no_auto_rank_messages_during_awards"
		});
		SaveSnapshot("match_awards");
		return (HookResult)0;
	}

	private HookResult OnMatchEndRestart(EventCsMatchEndRestart ev, GameEventInfo info)
	{
		_competitiveTabWarmupDone = false;
		_maps.MarkRestart(_clock.Elapsed.TotalSeconds, 60.0);
		CaptureRestartBots();
		_sameMapCyclePending = true;
		bool flag = _firstLive.Armed && !_firstLive.WaitingForRound && _firstLive.Reason == "awards_or_match_end_to_next_round";
		if (!flag)
		{
			_matchCycles.MatchEndRestart();
		}
		Log("match_cycle_signal", new
		{
			signal = "cs_match_end_restart",
			action = (flag ? "coalesced_with_current_match_generation" : "wait_for_next_round"),
			phase = _phase
		});
		return (HookResult)0;
	}

	private HookResult OnPreRestartEvent(EventCsPreRestart ev, GameEventInfo info)
	{
		Log("match_cycle_signal", new
		{
			signal = "cs_pre_restart",
			awaiting_next = _matchCycles.AwaitingNextMatch,
			generation = _firstLive.Generation,
			armed = _firstLive.Armed
		});
		return (HookResult)0;
	}

	private HookResult OnBeginNewMatch(EventBeginNewMatch ev, GameEventInfo info)
	{
		if (_matchCycles.AcceptBegin(_firstLive.RoundToken))
		{
			LiveRules rules = ReadLiveRules();
			Log("match_cycle_signal", new
			{
				signal = "begin_new_match",
				rules = rules,
				round_token = _firstLive.RoundToken,
				generation = _firstLive.Generation,
				policy = "record_boundary_only_no_synchronous_reveal"
			});
			if (!_firstLive.Armed)
			{
				bool preserveRound = _firstLive.RoundOpen && !_firstLive.WaitingForRound;
				ArmFirstLive("begin_new_match", preserveRound);
			}
			if (_firstLive.RoundOpen && !_firstLive.WaitingForRound)
			{
				CancelFreezePublication("begin_new_match_boundary");
				_freezeGate.Begin(_firstLive.Generation, _firstLive.RoundToken, _tick);
			}
			if (!_transition && !ProtectedPresentation && rules.Known && !rules.Warmup && !rules.Freeze)
			{
				_firstLive.FreezeEndEvent(_tick);
				_firstLive.Observe(_tick, rules);
			}
			_autoSnapshots.Schedule("begin_new_match", _tick);
			return (HookResult)0;
		}
		return (HookResult)0;
	}

	private HookResult ObserveReload(string command, CommandInfo info)
	{
		if (info.ArgCount > 1)
		{
			_maps.MarkReload(_clock.Elapsed.TotalSeconds);
			Log("explicit_map_reload_requested", new { command });
		}
		return (HookResult)0;
	}

	private HookResult ObserveRestart(CCSPlayerController? caller, CommandInfo info)
	{
		if (info.ArgCount > 1 && double.TryParse(info.GetArg(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result > 0.0)
		{
			ArmRestart(result, "command");
		}
		return (HookResult)0;
	}

	private void PollRestart()
	{
		if (_restartCvar == null)
		{
			_restartCvar = ConVar.Find("mp_restartgame");
		}
		if (_restartCvar != null && double.TryParse(_restartCvar.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			if (result > 0.0 && (result != _lastRestartValue || !_maps.InRestartWindow(_clock.Elapsed.TotalSeconds)))
			{
				ArmRestart(result, "convar_poll");
			}
			_lastRestartValue = result;
		}
	}

	private void ArmRestart(double seconds, string reason)
	{
		_maps.MarkRestart(_clock.Elapsed.TotalSeconds, seconds);
		ArmFirstLive("mp_restartgame:" + reason);
		_restartBots.Clear();
		CaptureRestartBots();
		_sameMapCyclePending = true;
		Log("soft_restart_armed", new
		{
			seconds = seconds,
			reason = reason,
			saved_bots = _restartBots.Count
		});
		_rosterNeeded = true;
	}

	private void CaptureRestartBots()
	{
		foreach (Binding item in _bindings.Values.Where((Binding b) => b.IsBot))
		{
			_restartBots[item.Slot] = new BotReservation(item.Name, item.Key, item.DisplayXuid);
		}
	}

	private void OnMapStart(string map)
	{
		if (!_disposed)
		{
			MapStartDecision mapStartDecision = _maps.Start(map, _clock.Elapsed.TotalSeconds);
			_rulesProxy = null;
			_rulesSignature = "";
			_autoSnapshots.Clear();
			_freezeGate.Close();
			_matchCycles.Clear();
			_competitiveTabWarmupDone = false;
			if (mapStartDecision != MapStartDecision.DuplicateCallback)
			{
				ArmFirstLive("map_start:" + mapStartDecision);
			}
			if (mapStartDecision == MapStartDecision.NewMap)
			{
				_bindings.Clear();
				_restartBots.Clear();
				_tabHeld.Clear();
				_stableIds.Clear();
				_sameMapCyclePending = false;
				_ledger = new RankLedger(_config);
				_botKeys.Clear();
				_bootstrap.Clear();
				_lockedMode = -1;
			}
			_reveals.Clear();
			_seeds.Clear();
			_bootstrap.CancelPending();
			_stableIds.Clear();
			_seedFingerprint = "";
			_wire.Reset();
			_wireAttempts = 0;
			_transition = false;
			_schemaReady = false;
			_lastPostThinkTick = -100L;
			_active = false;
			_phase = "map_start";
			_rosterNeeded = true;
			_blocked = "initializing";
			_messageHookAttempts = 0;
			_sendFailures = 0;
			Log("map_start", new
			{
				decision = mapStartDecision.ToString(),
				map = map,
				epoch = _maps.Epoch
			});
		}
	}

	private void OnMapEnd()
	{
		if (!_disposed)
		{
			try
			{
				PollRestart();
			}
			catch (Exception ex)
			{
				Once("restart_poll_error", ex.Message);
			}
			SaveSnapshot("before_map_end");
			_autoSnapshots.Clear();
			CancelLivePublication("map_end");
			_firstLive.EndRound();
			_rulesProxy = null;
			CaptureRestartBots();
			_sameMapCyclePending = true;
			RestoreAll("map_end");
			_bindings.Clear();
			_tabHeld.Clear();
			_reveals.Clear();
			_seeds.Clear();
			_stableIds.Clear();
			_wire.Reset();
			_seedFingerprint = "";
			_bootstrap.CancelPending();
			_freezeGate.Close();
			_maps.MarkEnd();
			_transition = true;
			_active = false;
			_phase = "map_end";
			UserMessage? revealMessage = _revealMessage;
			if (revealMessage != null)
			{
				revealMessage.Dispose();
			}
			_revealMessage = null;
			Log("map_end", new
			{
				restart_window = _maps.InRestartWindow(_clock.Elapsed.TotalSeconds)
			});
		}
	}

	private void OnPutInServer(int slot)
	{
		_rosterNeeded = true;
		Log("client_put", new { slot });
	}

	private void OnDisconnect(int slot)
	{
		if (_bindings.TryGetValue(slot, out Binding value) && value.IsBot)
		{
			_restartBots[slot] = new BotReservation(value.Name, value.Key, value.DisplayXuid);
			_sameMapCyclePending = true;
		}
		if (_bindings.TryGetValue(slot, out Binding value2))
		{
			RestoreBinding(value2, "disconnect");
		}
		if (_liveRecoveryViewers.Remove(slot) && _liveRecoveryViewers.Count == 0)
		{
			_firstLive.CancelQueued();
		}
		_freezeRecoveryViewers.Remove(slot);
		if (_freezeRecoveryViewers.Count == 0)
		{
			_freezeGate.CancelQueued();
		}
		_bindings.Remove(slot);
		_tabHeld.Remove(slot);
		_reveals.Drop(slot);
		_seeds.Drop(slot);
		_bootstrap.Drop(slot);
		_stableIds.Remove(slot);
		_rosterNeeded = true;
		Log("client_disconnect", new { slot });
	}

	private HookResult OnSpawn(EventPlayerSpawn ev, GameEventInfo info)
	{
		_rosterNeeded = true;
		return (HookResult)0;
	}

	private HookResult OnPlayerTeam(EventPlayerTeam ev, GameEventInfo info)
	{
		CCSPlayerController userid = ev.Userid;
		int num = ((userid != null && ((CEntityInstance)userid).IsValid) ? userid.Slot : (-1));
		bool flag = num >= 0 && (_botHider.IsManaged(num) || (_bindings.TryGetValue(num, out Binding value) && value.IsBot));
		Log("player_team", new
		{
			slot = num,
			bot = flag,
			old_team = ev.Oldteam,
			new_team = ev.Team,
			disconnect = ev.Disconnect,
			bots_now = Enumerable.Count(_bindings.Values, (Binding b) => b.IsBot),
			seed_rows_now = PlanSeeds().Rows.Length,
			policy = "observe_only_no_publication"
		});
		_rosterNeeded = true;
		if (!flag && num >= 0)
		{
			SaveSnapshot("human_team_change");
		}
		return (HookResult)0;
	}

	private void ArmFirstLive(string reason, bool preserveRound = false)
	{
		if (reason.StartsWith("mp_restartgame:", StringComparison.Ordinal) && _firstLive.Armed && _firstLive.WaitingForRound && _firstLive.Reason.StartsWith("mp_restartgame:", StringComparison.Ordinal))
		{
			Log("visibility_intent_coalesced", new
			{
				reason = reason,
				generation = _firstLive.Generation
			});
			return;
		}
		CancelLivePublication("new_visibility_generation");
		if (preserveRound)
		{
			_firstLive.ArmCurrentRound(reason, _tick);
		}
		else
		{
			_firstLive.Arm(reason, _tick);
		}
		_freezeGate.Close();
		_sendFailures = 0;
		Log("first_live_armed", new
		{
			reason = reason,
			generation = _firstLive.Generation,
			rank_map_epoch = _maps.Epoch,
			policy = "publication_state_only_no_rank_reroll"
		});
	}

	private void CancelLivePublication(string reason)
	{
		CancelFreezePublication(reason);
		if (_liveRecoveryViewers.Count > 0)
		{
			int[] array = _liveRecoveryViewers.Keys.ToArray();
			int[] array2 = array;
			foreach (int slot in array2)
			{
				_bootstrap.Drop(slot);
				_seeds.Drop(slot);
				_reveals.Drop(slot);
			}
			Log("first_live_publication_cancelled", new
			{
				reason = reason,
				slots = array,
				generation = _firstLive.Generation
			});
			_liveRecoveryViewers.Clear();
		}
		_firstLive.CancelQueued();
	}

	private LiveRules ReadLiveRules()
	{
		if (_transition || _maps.Epoch == 0L)
		{
			return new LiveRules(Known: false, Freeze: false, Warmup: false);
		}
		try
		{
			CCSGameRulesProxy? rulesProxy = _rulesProxy;
			if (rulesProxy == null || !((CEntityInstance)rulesProxy).IsValid)
			{
				_rulesProxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault((CCSGameRulesProxy p) => ((CEntityInstance)p).IsValid);
			}
			CCSGameRulesProxy? rulesProxy2 = _rulesProxy;
			CCSGameRules val = ((rulesProxy2 != null) ? rulesProxy2.GameRules : null);
			return (val == null) ? new LiveRules(Known: false, Freeze: false, Warmup: false) : new LiveRules(Known: true, val.FreezePeriod, val.WarmupPeriod);
		}
		catch (Exception ex)
		{
			Once("first_live_rules_read_error", ex.Message);
			return new LiveRules(Known: false, Freeze: false, Warmup: false);
		}
	}

	private void CancelFreezePublication(string reason)
	{
		if (_freezeRecoveryViewers.Count > 0)
		{
			int[] array = _freezeRecoveryViewers.Keys.ToArray();
			int[] array2 = array;
			foreach (int slot in array2)
			{
				_bootstrap.Drop(slot);
				_seeds.Drop(slot);
				_reveals.Drop(slot);
			}
			Log("reset_freeze_publication_cancelled", new
			{
				reason = reason,
				slots = array,
				generation = _freezeGate.Generation
			});
			_freezeRecoveryViewers.Clear();
		}
		_freezeGate.Close();
	}

	private void PollResetFreezeRecovery()
	{
		if (!_config.ResetFreezeRecovery || !_config.FirstLiveRecovery || !_firstLive.Armed || !_wire.Ready || _sendFailures >= 3 || _transition || !_active)
		{
			return;
		}
		LiveRules rules = ReadLiveRules();
		if (rules.Known && (!rules.Freeze || rules.Warmup))
		{
			if (_freezeRecoveryViewers.Count > 0)
			{
				CancelFreezePublication("freeze_phase_left");
			}
		}
		else
		{
			if (!_freezeGate.Ready(_tick, _lastPostThinkTick, _lastTransmitTick, rules, !ProtectedPresentation))
			{
				return;
			}
			SeedPlan seedPlan = PlanSeeds();
			if (seedPlan.Rows.Length == 0 || seedPlan.BlockedSlots.Length != 0)
			{
				return;
			}
			int[] array = (from b in _bindings.Values
				where !b.IsBot && IsSame(b)
				select b.Slot into x
				orderby x
				select x).ToArray();
			if (array.Length != 0)
			{
				int[] array2 = array;
				foreach (int num2 in array2)
				{
					_bootstrap.Drop(num2);
					_reveals.Drop(num2);
					_seeds.Drop(num2);
					_seeds.Request(num2, _tick);
					_freezeRecoveryViewers[num2] = _firstLive.Generation;
				}
				_freezeGate.MarkQueued();
				_freezeRecoveryQueued++;
				Log("reset_freeze_publication_queued", new
				{
					generation = _firstLive.Generation,
					round_token = _firstLive.RoundToken,
					boundary_tick = _freezeGate.BoundaryTick,
					tick = _tick,
					last_post_think = _lastPostThinkTick,
					last_transmit = _lastTransmitTick,
					targets = array,
					rows = seedPlan.Rows.Length,
					policy = "one_fresh_seed_then_two_tick_reveal_no_loop"
				});
			}
		}
	}

	private void PollFirstLiveRecovery(string origin = "tick")
	{
		int[] array = _liveRecoveryViewers.Keys.ToArray();
		foreach (int num in array)
		{
			if (!_bindings.TryGetValue(num, out Binding value) || value.IsBot || !IsSame(value))
			{
				_liveRecoveryViewers.Remove(num);
				_bootstrap.Drop(num);
				_seeds.Drop(num);
				_reveals.Drop(num);
			}
		}
		if (_firstLive.Queued && _liveRecoveryViewers.Count == 0)
		{
			_firstLive.CancelQueued();
		}
		LiveRules rules = ReadLiveRules();
		string text = $"{rules.Known}:{rules.Freeze}:{rules.Warmup}";
		if (text != _rulesSignature)
		{
			_rulesSignature = text;
			Log("rules_visibility_edge", new
			{
				rules = rules,
				generation = _firstLive.Generation,
				armed = _firstLive.Armed,
				last_post_think = _lastPostThinkTick,
				last_transmit = _lastTransmitTick
			});
		}
		_firstLive.Observe(_tick, rules);
		if (rules.Known && !rules.Freeze && _freezeRecoveryViewers.Count > 0)
		{
			CancelFreezePublication("first_live_supersedes_freeze_work");
		}
		if (!_config.FirstLiveRecovery || !_wire.Ready || _sendFailures >= 3 || !_firstLive.Ready(_tick, _lastPostThinkTick, _lastTransmitTick, !ProtectedPresentation))
		{
			return;
		}
		SeedPlan seedPlan = PlanSeeds();
		if (seedPlan.Rows.Length == 0 || seedPlan.BlockedSlots.Length != 0)
		{
			return;
		}
		int[] array2 = (from b in _bindings.Values
			where !b.IsBot && IsSame(b)
			select b.Slot into x
			orderby x
			select x).ToArray();
		if (array2.Length != 0)
		{
			array = array2;
			foreach (int num2 in array)
			{
				_bootstrap.Drop(num2);
				_reveals.Drop(num2);
				_seeds.Drop(num2);
				_seeds.Request(num2, _tick);
				_liveRecoveryViewers[num2] = _firstLive.Generation;
			}
			_firstLive.MarkQueued();
			_liveRecoveryQueued++;
			Log("first_live_publication_queued", new
			{
				generation = _firstLive.Generation,
				source = _firstLive.Reason,
				transition_tick = _firstLive.TransitionTick,
				tick = _tick,
				origin = origin,
				last_post_think = _lastPostThinkTick,
				last_transmit = _lastTransmitTick,
				viewers = array2,
				rows = seedPlan.Rows.Length,
				rank_map_epoch = _maps.Epoch,
				policy = "T02.4_ordered_path_fresh_seed_then_two_tick_reveal_not_client_ack"
			});
		}
	}

	private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
	{
		CancelLivePublication("next_round");
		if (_matchCycles.ConsumeNextRound())
		{
			ArmFirstLive("awards_or_match_end_to_next_round");
			Log("same_map_match_cycle_rearmed", new
			{
				generation = _firstLive.Generation,
				epoch = _maps.Epoch
			});
		}
		_firstLive.StartRound(_tick);
		_roundStarts++;
		_phase = "freeze";
		_rosterNeeded = true;
		PrewarmCompetitiveTabCache();
		RepairAtBoundary("round_start");
		if (_firstLive.Armed && _config.FirstLiveRecovery)
		{
			_freezeGate.Begin(_firstLive.Generation, _firstLive.RoundToken, _tick);
			_autoSnapshots.Schedule("reset_round_start", _tick);
			if (!_config.ResetFreezeRecovery && BoundaryPublicationPolicy.SeedBots("round_start"))
			{
				RequestSeeds(CompetitiveSeedDelayTicks);
			}
		}
		else if (BoundaryPublicationPolicy.SeedBots("round_start"))
		{
			RequestSeeds(CompetitiveSeedDelayTicks);
		}
		Log("round_start", new
		{
			count = _roundStarts,
			reset_armed = _firstLive.Armed,
			freeze_recovery_enabled = _config.ResetFreezeRecovery,
			synchronous_reveal = false
		});
		SaveSnapshot("round_start");
		return (HookResult)0;
	}

	private HookResult OnFreezeEnd(EventRoundFreezeEnd ev, GameEventInfo info)
	{
		_freezeEnds++;
		if (ProtectedPresentation)
		{
			Log("late_freeze_end_ignored", new
			{
				count = _freezeEnds
			});
			return (HookResult)0;
		}
		CancelFreezePublication("round_freeze_end");
		_phase = "live";
		_rosterNeeded = true;
		_firstLive.FreezeEndEvent(_tick);
		PrewarmCompetitiveTabCache();
		RepairAtBoundary("freeze_end");
		Log("freeze_end", new
		{
			count = _freezeEnds,
			first_live_armed = _firstLive.Armed,
			visibility_generation = _firstLive.Generation,
			synchronous_reveal = false,
			policy = "T02.4_deferred_ordered_recovery"
		});
		if (_firstLive.Armed)
		{
			_autoSnapshots.Schedule("first_freeze_end", _tick);
		}
		SaveSnapshot("freeze_end");
		return (HookResult)0;
	}

	private HookResult OnRoundEnd(EventRoundEnd ev, GameEventInfo info)
	{
		_phase = "mvp";
		CancelLivePublication("round_end");
		_firstLive.EndRound();
		Log("round_end", new { });
		SaveSnapshot("round_end");
		return (HookResult)0;
	}

	private HookResult OnRoundMvp(EventRoundMvp ev, GameEventInfo info)
	{
		_mvpEvents++;
		CCSPlayerController userid = ev.Userid;
		Log("round_mvp", new
		{
			slot = ((userid != null && ((CEntityInstance)userid).IsValid) ? userid.Slot : (-1)),
			reason = ev.Reason,
			count = _mvpEvents
		});
		SaveSnapshot("round_mvp");
		return (HookResult)0;
	}

	private void OnTick()
	{
		if (_disposed)
		{
			return;
		}
		_tick++;
		_gameThreadId = Environment.CurrentManagedThreadId;
		try
		{
			if (_tick % 4 == 0L)
			{
				PollRestart();
			}
			if (_tick % 128 == 0L && !_botHider.Ready)
			{
				_botHider.Resolve();
				_rosterNeeded = true;
			}
			if (_transition || _maps.Epoch == 0L)
			{
				return;
			}
			if (_tick % 16 == 0L || _blocked == "initializing")
			{
				EvaluateModeAndSchema();
			}
			if (!_active)
			{
				return;
			}
			if (_rosterNeeded || _tick % _config.RosterScanTicks == 0L)
			{
				ScanRoster();
			}
			if (_tick % _config.RepairIntervalTicks == 0L)
			{
				RepairBindings("tick");
			}
			if (_deferredTransmitError != null)
			{
				Once("check_transmit_error", _deferredTransmitError);
				_deferredTransmitError = null;
			}
			if (!_messageHooksReady && _tick % 128 == 0L && _messageHookAttempts < 3)
			{
				InstallMessageObservers();
			}
			PollTabs();
			if (!_wire.Ready && _wireAttempts < 3 && _tick % 128 == 0L)
			{
				_wireAttempts++;
				_wire.Initialize(((BasePlugin)this).ModuleDirectory);
				Log("wire_init", new
				{
					attempt = _wireAttempts,
					Ready = _wire.Ready,
					Error = _wire.Error,
					BuildInfo = _wire.BuildInfo
				});
				if (_wire.Ready)
				{
					RequestSeeds(CompetitiveSeedDelayTicks);
				}
			}
			DetectReadySeedChange();
			PollFirstLiveRecovery();
			PollResetFreezeRecovery();
			SendPendingMessages();
			string[] array = _autoSnapshots.Due(_tick);
			foreach (string reason in array)
			{
				SaveSnapshot(reason);
			}
			FlushRepairJournal();
			if (_tick % 640 == 0L)
			{
				SaveSnapshot("heartbeat");
			}
		}
		catch (Exception ex)
		{
			Once("tick_exception_" + ex.GetType().Name, ex.ToString());
		}
	}

	private void OnPostThink()
	{
		_postThinkCalls++;
		_lastPostThinkTick = _tick;
		if (!_disposed && _active && !_transition && _tick % _config.RepairIntervalTicks == 0L)
		{
			RepairBindings("post_think");
			if (_firstLive.Armed)
			{
				PollFirstLiveRecovery("post_entity_think");
			}
		}
	}

	private void OnCheckTransmit()
	{
		Interlocked.Increment(ref _transmitCalls);
		if (_gameThreadId == 0 || Environment.CurrentManagedThreadId != _gameThreadId)
		{
			Interlocked.Increment(ref _transmitSkippedThread);
		}
		else
		{
			if (_disposed || !_active || _transition || _insideTransmit)
			{
				return;
			}
			_lastTransmitTick = _tick;
			_insideTransmit = true;
			try
			{
				RepairBindings("check_transmit");
			}
			catch (Exception ex)
			{
				_deferredTransmitError = ex.Message;
			}
			finally
			{
				_insideTransmit = false;
			}
		}
	}

	private void RepairAtBoundary(string reason)
	{
		if (_active && !_transition)
		{
			object[] before = ReadBoundFields();
			RepairBindings(reason);
			Log("boundary_rank_fields", new
			{
				reason = reason,
				before = before,
				after = ReadBoundFields(),
				policy = "change-only repair; no RevealAll from this boundary"
			});
		}
	}

	private object[] ReadBoundFields()
	{
		List<object> list = new List<object>();
		foreach (Binding value in _bindings.Values)
		{
			try
			{
				if (IsSame(value) && !CoreConfig.FollowCS2ServerGuidelines)
				{
					list.Add(new
					{
						slot = value.Slot,
						bot = value.IsBot,
						assigned = value.Rank.Rating,
						rating = value.Player.CompetitiveRanking,
						wins = value.Player.CompetitiveWins,
						type = value.Player.CompetitiveRankType
					});
				}
			}
			catch (Exception ex)
			{
				list.Add(new
				{
					slot = value.Slot,
					error = ex.Message
				});
			}
		}
		return list.ToArray();
	}

	private void FlushRepairJournal()
	{
		if (_repairs.Total != _lastJournalLogged && !(_clock.Elapsed.TotalSeconds - _lastRepairLog < 1.0))
		{
			long since = _lastJournalLogged;
			_lastRepairLog = _clock.Elapsed.TotalSeconds;
			_lastJournalLogged = _repairs.Total;
			FieldRepair[] array = (from x in _repairs.Recent()
				where x.Sequence > since
				select x).ToArray();
			Log("field_repair_batch", new
			{
				initial_writes = _repairs.InitialWrites,
				drift_writes = _repairs.DriftWrites,
				by_stage = _repairs.StageCounts(),
				by_field = _repairs.FieldCounts(),
				recent = array,
				omitted_since_previous_batch = Math.Max(0L, _repairs.Total - since - array.Length),
				writer = "not_identified; these are observations before MapRanks writes"
			});
		}
	}

	private void EvaluateModeAndSchema()
	{
		if (CoreConfig.FollowCS2ServerGuidelines)
		{
			if (_active)
			{
				RestoreAll("guidelines_enabled");
			}
			_active = false;
			_blocked = "FollowCS2ServerGuidelines=true";
			Once("guidelines_changed", "Rank access paused. Cold restart after reviewing core.json.");
			return;
		}
		_gameType = ReadCvarInt("game_type");
		_gameMode = ReadCvarInt("game_mode");
		if (!_config.AllowsMode(_gameType, _gameMode))
		{
			if (_active)
			{
				RestoreAll("unsupported_mode");
				_bindings.Clear();
				_reveals.Clear();
				_seeds.Clear();
			}
			_active = false;
			_blocked = $"mode_not_enabled: {_gameType}/{_gameMode}";
			return;
		}
		if (_lockedMode != -1 && _lockedMode != _gameMode)
		{
			if (_active)
			{
				RestoreAll("mode_changed");
			}
			_active = false;
			_blocked = "mode_changed_inside_map: reload_map_required";
			return;
		}
		if (!_schemaReady)
		{
			try
			{
				string[] rankFields = RankFields;
				foreach (string text in rankFields)
				{
					if (Schema.GetSchemaOffset("CCSPlayerController", text) <= 0 || !Schema.IsSchemaFieldNetworked("CCSPlayerController", text))
					{
						throw new InvalidOperationException("Missing/non-networked schema field: " + text);
					}
				}
				_schemaReady = true;
				Log("schema_ready", new
				{
					fields = RankFields,
					identity = _config.BotIdentity
				});
			}
			catch (Exception ex)
			{
				_blocked = ex.Message;
				_active = false;
				Once("schema_error", ex.ToString());
				return;
			}
		}
		bool active = _active;
		_active = true;
		_lockedMode = _gameMode;
		_blocked = "";
		if (!active)
		{
			Log("active", new
			{
				game_type = _gameType,
				game_mode = _gameMode,
				style = _config.Mode
			});
			_rosterNeeded = true;
		}
	}

	private static int ReadCvarInt(string name)
	{
		ConVar val = ConVar.Find(name);
		if (!int.TryParse((val != null) ? val.StringValue : null, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			return -1;
		}
		return result;
	}

	private void ScanRoster()
	{
		_rosterNeeded = false;
		_rosterScans++;
		RosterObservation[] source = InspectRoster();
		RosterObservation[] array = source.Where((RosterObservation x) => (CEntityInstance)(object)x.Player != (CEntityInstance)null && x.Decision.Include).ToArray();
		HashSet<int> seen = array.Select((RosterObservation x) => x.Input.Slot).ToHashSet();
		string text = string.Join(";", source.Select((RosterObservation x) => $"{x.Input.Slot}:{x.RawHandle}:{x.Decision.Reason}"));
		if (text != _rosterSignature)
		{
			_rosterSignature = text;
			Log("roster_source", new
			{
				scan_slots = 64,
				reported_max_players = ReportedMaxPlayers(),
				convenience_slots = ConveniencePlayerSlots(),
				accepted = seen.Order().ToArray(),
				observations = source.Select(RosterDiagnostic).ToArray()
			});
		}
		int[] array2 = _bindings.Keys.Where((int s) => !seen.Contains(s)).ToArray();
		foreach (int num2 in array2)
		{
			Binding binding = _bindings[num2];
			if (IsSame(binding))
			{
				ObserveDisplayIdentity(binding);
				Once($"roster_gap_{num2}_{binding.RawHandle}", "Kept same connected controller and its map rank across a roster-policy gap.");
				continue;
			}
			if (binding.IsBot)
			{
				_restartBots[num2] = new BotReservation(binding.Name, binding.Key, binding.DisplayXuid);
				_sameMapCyclePending = true;
			}
			_bindings.Remove(num2);
			_tabHeld.Remove(num2);
			_reveals.Drop(num2);
			_seeds.Drop(num2);
			_bootstrap.Drop(num2);
			_stableIds.Remove(num2);
		}
		bool flag = false;
		RosterObservation[] array3 = array;
		foreach (RosterObservation rosterObservation in array3)
		{
			CCSPlayerController p = rosterObservation.Player;
			try
			{
				bool managed = rosterObservation.Input.ManagedByBotHider;
				bool isBot = rosterObservation.Decision.IsBot;
				if (_bindings.TryGetValue(p.Slot, out Binding value) && value.RawHandle == ((CHandle<CEntityInstance>)(object)((NativeEntity)p).EntityHandle).Raw)
				{
					bool num3 = isBot && !value.IsBot;
					value.IsBot |= isBot;
					value.ManagedByBotHider = managed;
					if (num3)
					{
						_botKeys.Remember(value.RawHandle, value.Key);
						_tabHeld.Remove(p.Slot);
						_reveals.Drop(p.Slot);
						_seeds.Drop(p.Slot);
						_bootstrap.Drop(p.Slot);
						Log("bot_classification_refined", new
						{
							slot = p.Slot,
							rating = value.Rank.Rating
						});
					}
					ObserveDisplayIdentity(value);
					continue;
				}
				if (_bindings.TryGetValue(p.Slot, out Binding value2) && value2.IsBot)
				{
					_restartBots[p.Slot] = new BotReservation(value2.Name, value2.Key, value2.DisplayXuid);
					_sameMapCyclePending = true;
				}
				if (_bindings.ContainsKey(p.Slot))
				{
					_bootstrap.Drop(p.Slot);
				}
				_bindings.Remove(p.Slot);
				_stableIds.Remove(p.Slot);
				string name = ((CBasePlayerController)p).PlayerName ?? "";
				string key;
				if (isBot)
				{
					key = _botKeys.GetOrAdd(((CHandle<CEntityInstance>)(object)((NativeEntity)p).EntityHandle).Raw, () => ReuseRestartBot(p.Slot, name, managed ? _botHider.GetDisplayId(p.Slot) : ((CBasePlayerController)p).SteamID) ?? $"bot:{++_botSerial}");
					goto IL_054c;
				}
				if (((CBasePlayerController)p).SteamID == 0L)
				{
					Once($"human_zero_id_{p.Slot}", "Waiting for a nonzero human identity.");
					continue;
				}
				key = "human:" + ((CBasePlayerController)p).SteamID.ToString(CultureInfo.InvariantCulture);
				goto IL_054c;
				IL_054c:
				RankValue rankValue = _ledger.AssignOnce(key, _gameMode);
				Binding binding2 = new Binding
				{
					Player = p,
					Slot = p.Slot,
					RawHandle = ((CHandle<CEntityInstance>)(object)((NativeEntity)p).EntityHandle).Raw,
					IsBot = isBot,
					Name = name,
					Key = key,
					Rank = rankValue,
					Original = ReadState(p),
					ManagedByBotHider = managed
				};
				_bindings.Add(p.Slot, binding2);
				_tabHeld.Remove(p.Slot);
				ObserveDisplayIdentity(binding2);
				WriteBinding(binding2, "initial_bind", initial: true);
				if (!binding2.IsBot)
				{
					RequestViewer(binding2.Slot, (_config.Mode == "competitive") ? 1 : 2, reveal: true);
				}
				flag = true;
				Log("bind", new
				{
					slot = p.Slot,
					bot = isBot,
					css_is_bot = p.IsBot,
					managed = managed,
					raw_handle = ((CHandle<CEntityInstance>)(object)((NativeEntity)p).EntityHandle).Raw,
					rating = rankValue.Rating,
					rank_type = rankValue.Type,
					identity = binding2.IdentitySource,
					display_tag = Tag(binding2.DisplayXuid),
					controller_tag = Tag(((CBasePlayerController)p).SteamID)
				});
			}
			catch (Exception ex)
			{
				Once($"bind_error_{p.Slot}", ex.ToString());
			}
		}
		if (flag)
		{
			RequestSeeds(CompetitiveSeedDelayTicks);
		}
	}

	private void ObserveDisplayIdentity(Binding b)
	{
		ulong num = (b.ManagedByBotHider ? _botHider.GetDisplayId(b.Slot) : ((CBasePlayerController)b.Player).SteamID);
		string text;
		if (b.ManagedByBotHider)
		{
			text = "bothider_api";
		}
		else
		{
			text = (b.IsBot ? "controller_fallback" : "human_unchanged");
		}
		if (!_stableIds.TryGetValue(b.Slot, out (ulong, long) value) || value.Item1 != num)
		{
			_stableIds[b.Slot] = (num, _tick);
			Log("display_identity_observed", new
			{
				slot = b.Slot,
				source = text,
				xuid_tag = Tag(num),
				zero = (num == 0),
				previous_tag = Tag(value.Item1),
				rating_unchanged = b.Rank.Rating
			});
		}
		b.DisplayXuid = num;
		b.IdentitySource = text;
	}

	private SeedPlan PlanSeeds()
	{
		return SeedPlanner.Plan(from b in _bindings.Values.Where(IsSame)
			select new SeedInput(b.Slot, b.IsBot, b.DisplayXuid, _stableIds.TryGetValue(b.Slot, out (ulong, long) value) && _tick - value.Item2 >= CompetitiveIdentitySettleTicks, b.Rank));
	}

	private void DetectReadySeedChange()
	{
		SeedPlan seedPlan = PlanSeeds();
		string text = BotBootstrapTracker.Fingerprint(seedPlan.Rows);
		if (!(text == _seedFingerprint))
		{
			_seedFingerprint = text;
			Log("seed_plan_changed", new
			{
				rows = seedPlan.Rows.Length,
				blocked = seedPlan.BlockedSlots
			});
			if (seedPlan.Rows.Length != 0)
			{
				RequestSeeds(CompetitiveSeedDelayTicks);
			}
		}
	}

	private string? ReuseRestartBot(int slot, string name, ulong displayId)
	{
		if (!_sameMapCyclePending && !_maps.InRestartWindow(_clock.Elapsed.TotalSeconds))
		{
			return null;
		}
		if (displayId != 0L)
		{
			KeyValuePair<int, BotReservation>[] array = _restartBots.Where((KeyValuePair<int, BotReservation> kv) => kv.Value.DisplayXuid == displayId).ToArray();
			if (array.Length == 1)
			{
				_restartBots.Remove(array[0].Key);
				return array[0].Value.Key;
			}
		}
		KeyValuePair<int, BotReservation>[] array2 = _restartBots.Where((KeyValuePair<int, BotReservation> kv) => kv.Value.Name == name).ToArray();
		if (array2.Length == 1)
		{
			_restartBots.Remove(array2[0].Key);
			return array2[0].Value.Key;
		}
		if (_maps.InRestartWindow(_clock.Elapsed.TotalSeconds) && _restartBots.TryGetValue(slot, out BotReservation value))
		{
			_restartBots.Remove(slot);
			return value.Key;
		}
		return null;
	}

	private static bool IsSame(Binding b)
	{
		if (((CEntityInstance)b.Player).IsValid && ((CHandle<CEntityInstance>)(object)((NativeEntity)b.Player).EntityHandle).Raw == b.RawHandle)
		{
			return (int)((CBasePlayerController)b.Player).Connected == 0;
		}
		return false;
	}

	private static PlayerState ReadState(CCSPlayerController p)
	{
		return new PlayerState(p.CompetitiveRanking, p.CompetitiveWins, p.CompetitiveRankType, p.CompetitiveRankingPredicted_Win, p.CompetitiveRankingPredicted_Loss, p.CompetitiveRankingPredicted_Tie, ((CBasePlayerController)p).SteamID);
	}

	private void RepairBindings(string stage)
	{
		Binding[] array = _bindings.Values.ToArray();
		foreach (Binding binding in array)
		{
			if (binding.Errors >= 3)
			{
				continue;
			}
			try
			{
				if (IsSame(binding))
				{
					WriteBinding(binding, stage);
				}
				else
				{
					_rosterNeeded = true;
				}
			}
			catch (Exception ex)
			{
				binding.Errors++;
				if (_insideTransmit)
				{
					_deferredTransmitError = $"slot={binding.Slot}: {ex.Message}";
				}
				else
				{
					Log("binding_error", new
					{
						slot = binding.Slot,
						count = binding.Errors,
						stage = stage,
						error = ex.Message
					});
				}
			}
		}
	}

	private void WriteBinding(Binding b, string stage, bool initial = false)
	{
		if (IsSame(b) && !CoreConfig.FollowCS2ServerGuidelines)
		{
			CCSPlayerController player = b.Player;
			SetInt(b, ref player.CompetitiveRanking, b.Rank.Rating, RankFields[0], stage, initial);
			SetInt(b, ref player.CompetitiveWins, b.Rank.Wins, RankFields[1], stage, initial);
			if (player.CompetitiveRankType != b.Rank.Type)
			{
				int competitiveRankType = player.CompetitiveRankType;
				player.CompetitiveRankType = b.Rank.Type;
				Utilities.SetStateChanged((CBaseEntity)(object)player, "CCSPlayerController", RankFields[2], 0);
				_fieldsChanged++;
				_repairs.Record(_tick, b.Slot, RankFields[2], competitiveRankType, b.Rank.Type, stage, initial);
			}
			SetInt(b, ref player.CompetitiveRankingPredicted_Win, b.Rank.PredictedWin, RankFields[3], stage, initial);
			SetInt(b, ref player.CompetitiveRankingPredicted_Loss, b.Rank.PredictedLoss, RankFields[4], stage, initial);
			SetInt(b, ref player.CompetitiveRankingPredicted_Tie, b.Rank.PredictedTie, RankFields[5], stage, initial);
		}
	}

	private void SetInt(Binding b, ref int field, int value, string fieldName, string stage, bool initial)
	{
		if (field != value)
		{
			int before = field;
			field = value;
			Utilities.SetStateChanged((CBaseEntity)(object)b.Player, "CCSPlayerController", fieldName, 0);
			_fieldsChanged++;
			_repairs.Record(_tick, b.Slot, fieldName, before, value, stage, initial);
		}
	}

	private static void RestoreInt(CCSPlayerController p, ref int field, int ours, int original, string name)
	{
		if (field == ours && field != original)
		{
			field = original;
			Utilities.SetStateChanged((CBaseEntity)(object)p, "CCSPlayerController", name, 0);
		}
	}

	private void RestoreBinding(Binding b, string reason)
	{
		try
		{
			if (!IsSame(b))
			{
				return;
			}
			CCSPlayerController player = b.Player;
			PlayerState original = b.Original;
			if (!CoreConfig.FollowCS2ServerGuidelines)
			{
				RestoreInt(player, ref player.CompetitiveRanking, b.Rank.Rating, original.Rating, RankFields[0]);
				RestoreInt(player, ref player.CompetitiveWins, b.Rank.Wins, original.Wins, RankFields[1]);
				if (player.CompetitiveRankType == b.Rank.Type && player.CompetitiveRankType != original.Type)
				{
					player.CompetitiveRankType = original.Type;
					Utilities.SetStateChanged((CBaseEntity)(object)player, "CCSPlayerController", RankFields[2], 0);
				}
				RestoreInt(player, ref player.CompetitiveRankingPredicted_Win, b.Rank.PredictedWin, original.Win, RankFields[3]);
				RestoreInt(player, ref player.CompetitiveRankingPredicted_Loss, b.Rank.PredictedLoss, original.Loss, RankFields[4]);
				RestoreInt(player, ref player.CompetitiveRankingPredicted_Tie, b.Rank.PredictedTie, original.Tie, RankFields[5]);
			}
		}
		catch (Exception ex)
		{
			Log("restore_error", new
			{
				slot = b.Slot,
				reason = reason,
				error = ex.Message
			});
		}
	}

	private void RestoreAll(string reason)
	{
		Binding[] array = _bindings.Values.ToArray();
		foreach (Binding b in array)
		{
			RestoreBinding(b, reason);
		}
	}

	private void PollTabs()
	{
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Invalid comparison between Unknown and I8
		Binding[] array = _bindings.Values.Where((Binding b) => !b.IsBot).ToArray();
		foreach (Binding binding in array)
		{
			if (!IsSame(binding))
			{
				continue;
			}
			CBasePlayerPawn value = ((CBasePlayerController)binding.Player).Pawn.Value;
			if (value == null || !((CEntityInstance)value).IsValid || value.MovementServices == null)
			{
				continue;
			}
			bool flag = (binding.Player.Buttons & 0x200000000L) > 0L;
			bool flag2 = _tabHeld.TryGetValue(binding.Slot, out var value2) & value2;
			_tabHeld[binding.Slot] = flag;
			if (!(!flag | flag2))
			{
				_tabEdges++;
				if (_config.Mode == "competitive")
				{
					WriteBinding(binding, "tab_open");
				}
				bool flag3 = _config.Mode != "competitive" && PresentationPolicy.TabCanRequest(_phase, _config.RevealOnTab);
				if (flag3)
				{
					_reveals.Request(binding.Slot, _tick);
				}
				else
				{
					_tabSuppressed++;
				}
				if (_config.TraceTabEdges)
				{
					Log("tab_open", new
					{
						slot = binding.Slot,
						count = _tabEdges,
						message_queued = flag3,
						protected_ui = ProtectedPresentation
					});
				}
			}
		}
	}

	private void PrewarmCompetitiveTabCache()
	{
		if (!(_config.Mode != "competitive") && !_competitiveTabWarmupDone && _bindings.Values.Any((Binding b) => !b.IsBot && IsSame(b)))
		{
			RequestAll(1);
			_competitiveTabWarmupDone = true;
			Log("competitive_tab_cache_prewarm", new
			{
				delay_ticks = 1,
				viewers = Enumerable.Count(_bindings.Values, (Binding b) => !b.IsBot && IsSame(b))
			});
		}
	}

	private void RequestViewer(int slot, int delayTicks, bool reveal)
	{
		_seeds.Request(slot, _tick + delayTicks);
		if (reveal)
		{
			_reveals.Request(slot, _tick + delayTicks);
		}
	}

	private void RequestSeeds(int delayTicks)
	{
		foreach (Binding item in _bindings.Values.Where((Binding b) => !b.IsBot))
		{
			RequestViewer(item.Slot, delayTicks, reveal: false);
		}
	}

	private void RequestAll(int delayTicks)
	{
		foreach (Binding item in _bindings.Values.Where((Binding b) => !b.IsBot))
		{
			RequestViewer(item.Slot, delayTicks, reveal: true);
		}
	}

	private void SendPendingMessages()
	{
		//IL_0357: Unknown result type (might be due to invalid IL or missing references)
		//IL_035e: Expected Obj, but got Unknown
		if (_sendFailures >= 3 || ProtectedPresentation || !_wire.Ready)
		{
			return;
		}
		Dictionary<int, CCSPlayerController> dictionary = _bindings.Values.Where((Binding b) => !b.IsBot && IsSame(b)).ToDictionary((Binding b) => b.Slot, (Binding b) => b.Player);
		HashSet<int> hashSet = dictionary.Keys.ToHashSet();
		int[] array = _seeds.Ready(_tick, hashSet);
		int[] array2 = _reveals.Ready(_tick, hashSet);
		if (array.Length == 0 && array2.Length == 0 && _bootstrap.PendingCount == 0)
		{
			return;
		}
		try
		{
			SeedPlan plan = PlanSeeds();
			string fingerprint = BotBootstrapTracker.Fingerprint(plan.Rows);
			if (array.Length != 0 && plan.Rows.Length != 0)
			{
				ulong num = 0uL;
				int[] array3 = array;
				foreach (int num3 in array3)
				{
					num |= (ulong)(1L << num3);
				}
				_expectedWireRows = plan.Rows;
				_expectedWireRecipients = array;
				_insideWireSend = true;
				try
				{
					if (!_wire.Send(plan.Rows, num))
					{
						throw new InvalidOperationException(_wire.Error);
					}
				}
				finally
				{
					_insideWireSend = false;
					_expectedWireRows = null;
					_expectedWireRecipients = Array.Empty<int>();
				}
				_seeds.MarkSent(array, _tick);
				_wireBatches++;
				_wireRows += plan.Rows.Length;
				int bootstrap_queued_viewers = _bootstrap.SeedEnqueued(array, fingerprint, _tick);
				Log("bot_rank_update_sent", new
				{
					recipients = array,
					entries = plan.Rows.Length,
					slots = plan.SentSlots,
					blocked = plan.BlockedSlots,
					count = _wireBatches,
					values = plan.Rows.Select((WireRow r, int i) => new
					{
						slot = plan.SentSlots[i],
						rating = r.Rating,
						type = r.Type
					}).ToArray(),
					bootstrap_queued_viewers = bootstrap_queued_viewers,
					delivery = "server_enqueue_returned; not_client_ack"
				});
			}
			else if (array.Length != 0)
			{
				int[] array3 = array;
				foreach (int slot in array3)
				{
					_seeds.Drop(slot);
				}
				Log("bot_seed_empty", new
				{
					blocked = plan.BlockedSlots
				});
			}
			int[] second = _bootstrap.Ready(_tick, hashSet, fingerprint, !ProtectedPresentation);
			int[] array4 = (from s in array2.Concat(second).Distinct()
				orderby s
				where !_bootstrap.WaitingForOrder(s, fingerprint, _tick)
				select s).ToArray();
			if (array4.Length != 0)
			{
				if (_revealMessage == null)
				{
					_revealMessage = UserMessage.FromPartialName("CCSUsrMsg_ServerRankRevealAll");
				}
				RecipientFilter val = new RecipientFilter();
				int[] array3 = array4;
				foreach (int key in array3)
				{
					val.Add(dictionary[key]);
				}
				_insideRevealSend = true;
				try
				{
					_revealMessage.Send(val);
				}
				finally
				{
					_insideRevealSend = false;
				}
				_reveals.MarkSent(array4, _tick);
				_bootstrap.Revealed(array4, fingerprint, _tick);
				_revealsSent++;
				int[] array5 = array4.Intersect(second).ToArray();
				if (array5.Length != 0)
				{
					_bootstrapBatches++;
				}
				LiveRules completionRules = ReadLiveRules();
				int[] array6 = array5.Where((int key4) => _freezeRecoveryViewers.TryGetValue(key4, out var value) && value == _firstLive.Generation && completionRules.Known && completionRules.Freeze && !completionRules.Warmup).ToArray();
				array3 = array6;
				foreach (int key2 in array3)
				{
					_freezeRecoveryViewers.Remove(key2);
				}
				if (array6.Length != 0)
				{
					_freezeRecoveryCompleted++;
					if (_freezeRecoveryViewers.Count == 0)
					{
						_freezeGate.Complete();
					}
					RecordOrderedBoundary("freeze", array6);
					Log("reset_freeze_publication_completed", new
					{
						recipients = array6,
						generation = _firstLive.Generation,
						count = _freezeRecoveryCompleted,
						delivery = "fresh_seed_then_ordered_reveal_dispatch_only"
					});
					_autoSnapshots.Schedule("freeze_pair_sent", _tick);
				}
				int[] array7 = array5.Where((int key4) => _liveRecoveryViewers.TryGetValue(key4, out var value) && value == _firstLive.Generation && completionRules.Known && !completionRules.Freeze && !completionRules.Warmup).ToArray();
				array3 = array7;
				foreach (int key3 in array3)
				{
					_liveRecoveryViewers.Remove(key3);
				}
				if (array7.Length != 0)
				{
					_liveRecoveryCompleted++;
					RecordOrderedBoundary("live", array7);
					_autoSnapshots.Schedule("live_pair_sent", _tick);
					Log("first_live_publication_completed", new
					{
						recipients = array7,
						generation = _firstLive.Generation,
						count = _liveRecoveryCompleted,
						delivery = "seed_then_reveal_server_dispatch; not_client_ack"
					});
				}
				if (_firstLive.Queued && _liveRecoveryViewers.Count == 0)
				{
					_firstLive.Complete();
				}
				Log("reveal_sent", new
				{
					recipients = array4,
					count = _revealsSent,
					bootstrap_recipients = array5,
					bootstrap_batches = _bootstrapBatches,
					policy = "initial_viewer_or_stable_roster_or_first_live_generation_or_explicit; no_TAB_or_ordinary_round_reveal"
				});
			}
			_sendFailures = 0;
		}
		catch (Exception ex)
		{
			_sendFailures++;
			Log("rank_message_error", new
			{
				count = _sendFailures,
				error = ex.Message
			});
		}
	}

	private void InstallMessageObservers()
	{
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Expected Obj, but got Unknown
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Expected Obj, but got Unknown
		//IL_00b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c4: Expected Obj, but got Unknown
		if (!_config.ObserveRankMessages)
		{
			_messageHooksReady = true;
			return;
		}
		_messageHookAttempts++;
		try
		{
			int num = UserMessage.FindIdByName("CCSUsrMsg_ServerRankUpdate");
			int num2 = UserMessage.FindIdByName("CCSUsrMsg_ServerRankRevealAll");
			int num3 = UserMessage.FindIdByName("CCSUsrMsg_EndOfMatchAllPlayersData");
			if (!_observedMessageIds.Contains(num))
			{
				((BasePlugin)this).HookUserMessage(num, (UserMessageHandler)ObserveRankUpdate, (HookMode)0);
				_observedMessageIds.Add(num);
			}
			if (!_observedMessageIds.Contains(num2))
			{
				((BasePlugin)this).HookUserMessage(num2, (UserMessageHandler)ObserveReveal, (HookMode)0);
				_observedMessageIds.Add(num2);
			}
			if (!_observedMessageIds.Contains(num3))
			{
				((BasePlugin)this).HookUserMessage(num3, (UserMessageHandler)ObserveAwardsData, (HookMode)0);
				_observedMessageIds.Add(num3);
			}
			_messageHooksReady = true;
			Log("rank_update_observer_ready", new
			{
				id = num,
				revealId = num2,
				awardsId = num3,
				stage = "pre_dispatch",
				client_ack = false
			});
		}
		catch (Exception ex)
		{
			Log("message_observer_error", new
			{
				attempt = _messageHookAttempts,
				error = ex.Message
			});
		}
	}

	private bool SkipObserverThread()
	{
		if (_gameThreadId != 0 && Environment.CurrentManagedThreadId == _gameThreadId)
		{
			return false;
		}
		Interlocked.Increment(ref _observerSkippedThread);
		return true;
	}

	private HookResult ObserveRankUpdate(UserMessage message)
	{
		if (!SkipObserverThread())
		{
			_updatesObserved++;
			if (_insideWireSend)
			{
				_ownUpdatePre++;
			}
			try
			{
				ParsedRank[] array = RankPacketAudit.Parse(message.DebugString);
				int repeatedFieldCount = message.GetRepeatedFieldCount("rank_update");
				int[] array2 = (from p in (IEnumerable<CCSPlayerController>)message.Recipients
					select p.Slot into s
					orderby s
					select s).ToArray();
				PacketAudit packetAudit = null;
				bool? recipients_match_expected = null;
				if (_insideWireSend && _expectedWireRows != null)
				{
					_ownRowsPre += repeatedFieldCount;
					packetAudit = RankPacketAudit.Compare(array, _expectedWireRows, repeatedFieldCount);
					recipients_match_expected = array2.SequenceEqual(_expectedWireRecipients.OrderBy((int s) => s));
					ref string lastPacketAudit = ref _lastPacketAudit;
					string text;
					if (packetAudit.Match)
					{
						text = ((recipients_match_expected == true) ? packetAudit.Reason : "recipient_mismatch");
					}
					else
					{
						text = packetAudit.Reason;
					}
					lastPacketAudit = text;
					if (!packetAudit.Match || recipients_match_expected != true)
					{
						_packetMismatches++;
					}
				}
				var parsed = array.Select((ParsedRank p) => new
				{
					account_present = p.AccountId.HasValue,
					account_tag = Tag(p.AccountId.HasValue ? ((uint)p.AccountId.Value) : 0u),
					matched_slots = (from b in _bindings.Values
						where p.AccountId.HasValue && (int)b.DisplayXuid == p.AccountId.Value
						select b.Slot).ToArray(),
					old_rank = p.OldRank,
					new_rank = p.NewRank,
					wins = p.Wins,
					rank_type = p.Type
				}).ToArray();
				Log("observed_rank_update", new
				{
					count = _updatesObserved,
					during_our_send = _insideWireSend,
					stage = "pre_dispatch_not_client_ack",
					recipients = array2,
					entries = repeatedFieldCount,
					parsed = parsed,
					own_packet_audit = packetAudit,
					recipients_match_expected = recipients_match_expected
				});
			}
			catch (Exception ex)
			{
				if (_insideWireSend)
				{
					_lastPacketAudit = "audit_error:" + ex.GetType().Name;
					_packetMismatches++;
				}
				Once("observe_rank_update_error", ex.Message);
			}
			return (HookResult)0;
		}
		return (HookResult)0;
	}

	private HookResult ObserveReveal(UserMessage message)
	{
		if (!SkipObserverThread())
		{
			_revealsObserved++;
			if (_insideRevealSend)
			{
				_ownRevealPre++;
			}
			try
			{
				Log("observed_reveal", new
				{
					count = _revealsObserved,
					during_our_send = _insideRevealSend,
					stage = "pre_dispatch_not_client_ack",
					recipients = ((IEnumerable<CCSPlayerController>)message.Recipients).Select((CCSPlayerController p) => p.Slot).ToArray()
				});
			}
			catch (Exception ex)
			{
				Once("observe_reveal_error", ex.Message);
			}
			return (HookResult)0;
		}
		return (HookResult)0;
	}

	private HookResult ObserveAwardsData(UserMessage message)
	{
		if (!SkipObserverThread())
		{
			_phase = "awards";
			_matchCycles.Awards();
			_competitiveTabWarmupDone = false;
			CancelLivePublication("match_award_message");
			_firstLive.EndRound();
			try
			{
				string text = message.DebugString;
				if (text.Length > 65536)
				{
					text = text.Substring(0, 65536);
				}
				List<object> list = new List<object>();
				foreach (Match item in Regex.Matches(text, "allplayerdata\\s*\\{([^{}]*)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20L)).Take(64))
				{
					string value = item.Groups[1].Value;
					int? slot = DiagnosticInt(value, "slot");
					bool flag = ulong.TryParse(Regex.Match(value, "(?m)^\\s*xuid\\s*:\\s*(\\d+)").Groups[1].Value, out var result);
					Binding binding = ((slot.HasValue && _bindings.TryGetValue(slot.Value, out Binding value2)) ? value2 : null);
					list.Add(new
					{
						slot = slot,
						xuid_present = flag,
						award_xuid_tag = Tag(result),
						bound_bot = binding?.IsBot,
						display_tag = Tag(binding?.DisplayXuid ?? 0),
						same_identity = (flag && binding != null && result == binding.DisplayXuid)
					});
				}
				Log("match_award_identities", new
				{
					rows = list,
					entries = message.GetRepeatedFieldCount("allplayerdata")
				});
				SaveSnapshot("match_award_message");
			}
			catch (Exception ex)
			{
				Once("observe_awards_error", ex.Message);
			}
			return (HookResult)0;
		}
		return (HookResult)0;
	}

	private static int? DiagnosticInt(string text, string field)
	{
		if (!int.TryParse(Regex.Match(text, "(?m)^\\s*" + Regex.Escape(field) + "\\s*:\\s*(-?\\d+)").Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			return null;
		}
		return result;
	}

	private void RecordOrderedBoundary(string stage, int[] targets)
	{
		var item = new
		{
			stage = stage,
			tick = _tick,
			generation = _firstLive.Generation,
			round_token = _firstLive.RoundToken,
			targets = targets,
			fresh_seed = true,
			ordered_gap_ticks_min = 2,
			last_post_think = _lastPostThinkTick,
			last_transmit = _lastTransmitTick,
			server_dispatch_only = true
		};
		_visibilityEdges.Enqueue(item);
		while (_visibilityEdges.Count > 24)
		{
			_visibilityEdges.Dequeue();
		}
	}

	private static void WriteAtomicText(string path, string text)
	{
		string text2 = path + ".tmp";
		File.WriteAllText(text2, text);
		File.Move(text2, path, overwrite: true);
	}

	private string[] StatusLines()
	{
		SeedPlan seedPlan = PlanSeeds();
		return new string[15]
		{
			$"[MapRanks {((BasePlugin)this).ModuleVersion}] active={_active} phase={_phase}",
			$"map={_maps.MapName} epoch={_maps.Epoch} humans={Enumerable.Count(_bindings.Values, (Binding b) => !b.IsBot)} bots={Enumerable.Count(_bindings.Values, (Binding b) => b.IsBot)} active_assigned={_bindings.Count} assigned_map_total={_ledger?.Assignments ?? 0}",
			$"seed_rows={seedPlan.Rows.Length} seed_blocked={seedPlan.BlockedSlots.Length} wire_batches={_wireBatches} wire_rows={_wireRows}",
			$"wire={_wire.Ready} bh_api={_botHider.Ready} observer_pre={_messageHooksReady && _config.ObserveRankMessages}",
			$"own_update_pre={_ownUpdatePre} own_rows_pre={_ownRowsPre} packet_mismatches={_packetMismatches} pre_thread_skip={_observerSkippedThread}",
			$"bootstrap_reveals={_bootstrapBatches} bootstrap_pending={_bootstrap.PendingCount} own_reveal_pre={_ownRevealPre}",
			"packet_check=" + _lastPacketAudit,
			$"first_live_enabled={_config.FirstLiveRecovery} generation={_firstLive.Generation} state={_firstLive.State}",
			$"live_recovery_queued={_liveRecoveryQueued} live_recovery_completed={_liveRecoveryCompleted} pending_viewers={_liveRecoveryViewers.Count}",
			$"freeze_recovery_enabled={_config.ResetFreezeRecovery} freeze_queued={_freezeRecoveryQueued} freeze_sent={_freezeRecoveryCompleted}",
			$"next_match_pending={_matchCycles.AwaitingNextMatch} timing=ordered_T02.4_live_plus_separate_freeze_pair",
			"automatic_reports=True console_screenshots_required=False synchronous_boundary_reveal=False",
			$"checkTransmit={_transmitCalls} transmit_skipped={_transmitSkippedThread} postThink={_postThinkCalls}",
			"blocked=" + _blocked + " wire_error=" + _wire.Error,
			"UI=NOT_VERIFIED. Pre-dispatch observation is not client acknowledgement."
		}.SelectMany((string line) => DiagnosticLines.Split(line)).ToArray();
	}

	private void ReplyDiagnostic(CommandInfo info, string text)
	{
		string[] array = DiagnosticLines.Split(text);
		foreach (string text2 in array)
		{
			try
			{
				info.ReplyToCommand(text2);
			}
			catch (Exception ex)
			{
				Once("console_reply_error", ex.Message);
				Server.PrintToConsole(text2);
			}
		}
	}

	private void PrintStatus(CommandInfo info)
	{
		ReplyDiagnostic(info, "[MapRanks " + ((BasePlugin)this).ModuleVersion + "] status (read-only)");
		try
		{
			string[] array = StatusLines();
			string[] array2 = array;
			foreach (string text in array2)
			{
				ReplyDiagnostic(info, text);
			}
			if (_config.DiagnosticsEnabled)
			{
				try
				{
					File.WriteAllLines(Path.Combine(((BasePlugin)this).ModuleDirectory, "logs", "status_console.txt"), array);
					return;
				}
				catch (Exception ex)
				{
					Log("status_text_write_error", new
					{
						error = ex.Message
					});
					return;
				}
			}
		}
		catch (Exception ex2)
		{
			Log("status_error", new
			{
				error = ex2.ToString()
			});
			ReplyDiagnostic(info, "[MapRanks] status error: " + ex2.Message + "; details in trace log.");
		}
	}

	private void StatusCommand(CCSPlayerController? caller, CommandInfo info)
	{
		Log("status_command", new
		{
			caller_slot = ((caller != null && ((CEntityInstance)caller).IsValid) ? caller.Slot : (-1))
		});
		PrintStatus(info);
		SaveSnapshot("status_command");
	}

	private void DumpCommand(CCSPlayerController? caller, CommandInfo info)
	{
		if (_clock.Elapsed.TotalSeconds - _lastDump < 1.0)
		{
			info.ReplyToCommand("[MapRanks] Please wait one second.");
			return;
		}
		_lastDump = _clock.Elapsed.TotalSeconds;
		PrintStatus(info);
		SaveSnapshot("manual_dump");
		foreach (Binding item in _bindings.Values.OrderBy((Binding b) => b.Slot))
		{
			try
			{
				bool flag = IsSame(item);
				ReplyDiagnostic(info, $"slot={item.Slot} {(item.IsBot ? "BOT" : "HUMAN")} assigned={item.Rank.Rating} type={item.Rank.Type} actual={((flag && !CoreConfig.FollowCS2ServerGuidelines) ? item.Player.CompetitiveRanking : (-1))} source={item.IdentitySource} bh={item.ManagedByBotHider} errors={item.Errors} display={Tag(item.DisplayXuid)} controller={(flag ? Tag(((CBasePlayerController)item.Player).SteamID) : "invalid")}");
			}
			catch (Exception ex)
			{
				ReplyDiagnostic(info, $"slot={item.Slot} read_error={ex.Message}");
			}
		}
		info.ReplyToCommand(_config.DiagnosticsEnabled ? "[MapRanks] Saved diagnostic snapshots. No reroll or refresh." : "[MapRanks] Diagnostics are disabled; no files were written. No reroll or refresh.");
	}

	private void ProbeCommand(CCSPlayerController? caller, CommandInfo info)
	{
		//IL_043d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0444: Expected Obj, but got Unknown
		string text = ((info.ArgCount > 1) ? info.GetArg(1).ToLowerInvariant() : "");
		bool flag;
		switch (text)
		{
		case "seed":
		case "reveal":
		case "pair":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			ReplyDiagnostic(info, "Usage: css_mapranks_probe seed|reveal|pair (LIVE only; values unchanged)");
			return;
		}
		LiveRules liveRules = ReadLiveRules();
		if (!_active || _transition || _phase != "live" || !liveRules.Known || liveRules.Freeze || liveRules.Warmup)
		{
			ReplyDiagnostic(info, "Probe blocked: wait until an actual LIVE round; never during freeze/MVP/awards.");
			return;
		}
		if (_clock.Elapsed.TotalSeconds - _lastProbe < 2.0 || _liveRecoveryViewers.Count != 0 || _bootstrap.PendingCount != 0 || _seeds.PendingCount != 0 || _reveals.PendingCount != 0)
		{
			ReplyDiagnostic(info, "Probe blocked: wait two seconds for pending publication to finish.");
			return;
		}
		Dictionary<int, CCSPlayerController> dictionary = _bindings.Values.Where((Binding b) => !b.IsBot && IsSame(b)).ToDictionary((Binding b) => b.Slot, (Binding b) => b.Player);
		int[] array;
		if ((CEntityInstance)(object)caller != (CEntityInstance)null)
		{
			if (!((CEntityInstance)caller).IsValid || !dictionary.ContainsKey(caller.Slot))
			{
				ReplyDiagnostic(info, "Probe caller is not a connected human viewer.");
				return;
			}
			array = new int[1] { caller.Slot };
		}
		else
		{
			array = dictionary.Keys.OrderBy((int s) => s).ToArray();
		}
		if (array.Length == 0)
		{
			ReplyDiagnostic(info, "No human recipient.");
			return;
		}
		_lastProbe = _clock.Elapsed.TotalSeconds;
		_probeCount++;
		SaveSnapshot("probe_before_" + text);
		Log("manual_probe_requested", new
		{
			mode = text,
			targets = array,
			count = _probeCount,
			policy = "one explicit diagnostic; no reroll/SteamID/team/flags/gamerules writes"
		});
		try
		{
			if (text == "pair")
			{
				if (!_wire.Ready)
				{
					throw new InvalidOperationException("Rank sender is not ready.");
				}
				SeedPlan seedPlan = PlanSeeds();
				if (seedPlan.Rows.Length == 0 || seedPlan.BlockedSlots.Length != 0)
				{
					throw new InvalidOperationException("BOT records are empty or not settled.");
				}
				_sendFailures = 0;
				int[] array2 = array;
				foreach (int slot in array2)
				{
					_bootstrap.Drop(slot);
					_seeds.Request(slot, _tick);
				}
				ReplyDiagnostic(info, "Probe pair queued: one BOT seed, then ordered Reveal. No team change or reroll.");
			}
			else if (text == "seed")
			{
				SeedPlan seedPlan2 = PlanSeeds();
				if (!_wire.Ready || seedPlan2.Rows.Length == 0 || seedPlan2.BlockedSlots.Length != 0)
				{
					throw new InvalidOperationException("Rank sender/BOT records not ready.");
				}
				ulong num2 = 0uL;
				int[] array2 = array;
				foreach (int num3 in array2)
				{
					num2 |= (ulong)(1L << num3);
				}
				_expectedWireRows = seedPlan2.Rows;
				_expectedWireRecipients = array;
				_insideWireSend = true;
				try
				{
					if (!_wire.Send(seedPlan2.Rows, num2))
					{
						throw new InvalidOperationException(_wire.Error);
					}
				}
				finally
				{
					_insideWireSend = false;
					_expectedWireRows = null;
					_expectedWireRecipients = Array.Empty<int>();
				}
				_wireBatches++;
				_wireRows += seedPlan2.Rows.Length;
				Log("probe_seed_sent", new
				{
					targets = array,
					rows = seedPlan2.Rows.Length,
					reveal = false,
					client_ack = false
				});
				ReplyDiagnostic(info, "Probe seed sent: BOT records only. NO Reveal queued. Observe the existing TAB panel.");
			}
			else
			{
				if (_revealMessage == null)
				{
					_revealMessage = UserMessage.FromPartialName("CCSUsrMsg_ServerRankRevealAll");
				}
				RecipientFilter val = new RecipientFilter();
				int[] array2 = array;
				foreach (int key in array2)
				{
					val.Add(dictionary[key]);
				}
				_insideRevealSend = true;
				try
				{
					_revealMessage.Send(val);
				}
				finally
				{
					_insideRevealSend = false;
				}
				_revealsSent++;
				Log("probe_reveal_sent", new
				{
					targets = array,
					seed = false,
					client_ack = false
				});
				ReplyDiagnostic(info, "Probe reveal sent once. NO RankUpdate, reroll or team change. Observe the TAB panel.");
			}
		}
		catch (Exception ex)
		{
			Log("manual_probe_error", new
			{
				mode = text,
				error = ex.ToString()
			});
			ReplyDiagnostic(info, "Probe failed: " + ex.Message);
		}
	}

	private void RefreshCommand(CCSPlayerController? caller, CommandInfo info)
	{
		if (!_active)
		{
			info.ReplyToCommand("[MapRanks] Inactive: " + _blocked);
			return;
		}
		if (ProtectedPresentation)
		{
			info.ReplyToCommand("[MapRanks] Refresh blocked while MVP/awards are visible; no UI-reset probe during presentation.");
			return;
		}
		_sendFailures = 0;
		_wireAttempts = 0;
		if (caller != null && ((CEntityInstance)caller).IsValid && _bindings.TryGetValue(caller.Slot, out Binding value) && !value.IsBot)
		{
			RequestViewer(caller.Slot, 0, reveal: true);
		}
		else
		{
			RequestAll(0);
		}
		info.ReplyToCommand("[MapRanks] One BOT cache seed + reveal queued. Existing rank values unchanged.");
	}

	private void MarkCommand(CCSPlayerController? caller, CommandInfo info)
	{
		if (_clock.Elapsed.TotalSeconds - _lastMark < 1.0)
		{
			info.ReplyToCommand("[MapRanks] Please wait one second.");
			return;
		}
		_lastMark = _clock.Elapsed.TotalSeconds;
		string source = ((info.ArgCount > 1) ? info.GetArg(1) : "manual");
		source = new string(source.Take(48).Where((char c) =>
		{
			bool flag = char.IsLetterOrDigit(c);
			if (!flag)
			{
				bool flag2 = ((c == '-' || c == '_') ? true : false);
				flag = flag2;
			}
			return flag;
		}).ToArray());
		Log("user_mark", new
		{
			label = source
		});
		SaveSnapshot("mark_" + source);
		info.ReplyToCommand("[MapRanks] Marked: " + source);
	}

	private object? ReadRulesForDiagnostics()
	{
		if (_transition || _maps.Epoch == 0L)
		{
			return null;
		}
		try
		{
			CCSGameRulesProxy? val = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault((CCSGameRulesProxy p) => ((CEntityInstance)p).IsValid);
			CCSGameRules val2 = ((val != null) ? val.GameRules : null);
			if (val2 == null)
			{
				return null;
			}
			return new
			{
				queued = val2.IsQueuedMatchmaking,
				queued_mode = val2.QueuedMatchmakingMode,
				freeze = val2.FreezePeriod,
				warmup = val2.WarmupPeriod,
				rounds = val2.TotalRoundsPlayed
			};
		}
		catch (Exception ex)
		{
			return new
			{
				read_error = ex.Message
			};
		}
	}

	private void SaveSnapshot(string reason)
	{
		if (!_config.DiagnosticsEnabled)
		{
			return;
		}
		try
		{
			List<object> list = new List<object>();
			foreach (Binding item in _bindings.Values.OrderBy((Binding b) => b.Slot))
			{
				try
				{
					bool flag = !_transition && IsSame(item);
					PlayerState playerState = ((flag && !CoreConfig.FollowCS2ServerGuidelines) ? ReadState(item.Player) : null);
					list.Add(new
					{
						slot = item.Slot,
						bot = item.IsBot,
						raw_handle = item.RawHandle,
						valid = flag,
						assigned = item.Rank,
						actual = ((playerState == null) ? null : new
						{
							rating = playerState.Rating,
							wins = playerState.Wins,
							rank_type = playerState.Type,
							predicted_win = playerState.Win,
							predicted_loss = playerState.Loss,
							predicted_tie = playerState.Tie,
							xuid_tag = Tag(playerState.Xuid),
							xuid_zero = (playerState.Xuid == 0)
						}),
						css_is_bot = (flag ? new bool?(item.Player.IsBot) : ((bool?)null)),
						bh_managed = item.ManagedByBotHider,
						identity_source = item.IdentitySource,
						display_tag = Tag(item.DisplayXuid),
						display_zero = (item.DisplayXuid == 0),
						display_shape = IdentityShape.Read(item.DisplayXuid),
						controller_shape = ((playerState == null) ? ((IdentityShape?)null) : new IdentityShape?(IdentityShape.Read(playerState.Xuid))),
						identity_matches_controller = (playerState != null && item.DisplayXuid == playerState.Xuid),
						team = (flag ? new int?(((CBaseEntity)item.Player).TeamNum) : ((int?)null)),
						identity_settled = (_stableIds.TryGetValue(item.Slot, out (ulong, long) value) && _tick - value.Item2 >= CompetitiveIdentitySettleTicks),
						owned_identity = false,
						errors = item.Errors
					});
				}
				catch (Exception ex)
				{
					list.Add(new
					{
						slot = item.Slot,
						error = ex.Message
					});
				}
			}
			object obj = new
			{
				version = ((BasePlugin)this).ModuleVersion,
				utc = DateTime.UtcNow,
				reason = reason,
				map = _maps.MapName,
				epoch = _maps.Epoch,
				active = _active,
				blocked = _blocked,
				phase = _phase,
				game_type = _gameType,
				game_mode = _gameMode,
				style = _config.Mode,
				bot_identity = _config.BotIdentity,
				ui_verified = false,
				rules = ReadRulesForDiagnostics(),
				boundary_visibility = new
				{
					reset_freeze_enabled = _config.ResetFreezeRecovery,
					freeze_queued = _freezeRecoveryQueued,
					freeze_dispatched = _freezeRecoveryCompleted,
					freeze_gate = _freezeGate.Snapshot(),
					pending_freeze_slots = _freezeRecoveryViewers.Keys.OrderBy((int x) => x).ToArray(),
					match_signals = _matchCycles.Snapshot(),
					recent_edges = _visibilityEdges.ToArray(),
					synchronous_boundary_reveal = false,
					client_ack = false
				},
				first_live = new
				{
					enabled = _config.FirstLiveRecovery,
					gate = _firstLive.Snapshot(),
					queued = _liveRecoveryQueued,
					completed = _liveRecoveryCompleted,
					pending_slots = _liveRecoveryViewers.Keys.OrderBy((int x) => x).ToArray(),
					policy = "ordered_fresh_seed_then_reveal_after_stages; no_synchronous_boundary_reveal; no_client_ack"
				},
				architecture = "CSS rank schema + BotHider read-only identity + hook-free MapRanksWire RankUpdate sender",
				publication = new
				{
					observer_stage = "pre_dispatch_not_client_ack",
					pre_thread_skip = _observerSkippedThread,
					own_update_pre = _ownUpdatePre,
					own_rows_pre = _ownRowsPre,
					own_reveal_pre = _ownRevealPre,
					packet_mismatches = _packetMismatches,
					last_packet_audit = _lastPacketAudit,
					bootstrap_reveals = _bootstrapBatches,
					bootstrap_pending = _bootstrap.PendingCount,
					policy = "once_per_viewer_stable_roster_and_first_live_generation_after_seed"
				},
				bothider = _botHider.Diagnostics(),
				wire = new
				{
					Ready = _wire.Ready,
					Error = _wire.Error,
					BuildInfo = _wire.BuildInfo,
					attempts = _wireAttempts
				},
				seed_plan = new
				{
					slots = PlanSeeds().SentSlots,
					blocked = PlanSeeds().BlockedSlots
				},
				observed_controllers = ObserveControllers(),
				roster = new
				{
					scan_slots = 64,
					scans = _rosterScans,
					reported_max_players = ReportedMaxPlayers(),
					convenience_slots = ConveniencePlayerSlots()
				},
				field_repairs = new
				{
					initial_writes = _repairs.InitialWrites,
					drift_writes = _repairs.DriftWrites,
					by_stage = _repairs.StageCounts(),
					by_field = _repairs.FieldCounts(),
					recent = _repairs.Recent()
				},
				transmit = new
				{
					registered = _transmitListenerRegistered,
					calls = _transmitCalls,
					last_tick = _lastTransmitTick,
					skipped_thread = _transmitSkippedThread,
					policy = "public CSS callback; no transmit-mask changes; no messages; no identity writes"
				},
				counters = new
				{
					active_assigned = _bindings.Count,
					assigned = (_ledger?.Assignments ?? 0),
					fields_changed = _fieldsChanged,
					identity_writes = 0,
					wire_batches = _wireBatches,
					wire_rows = _wireRows,
					tab_refresh_suppressed = _tabSuppressed,
					reveals_sent = _revealsSent,
					pending_reveals = _reveals.PendingCount,
					pending_seeds = _seeds.PendingCount,
					tab_edges = _tabEdges,
					round_starts = _roundStarts,
					freeze_ends = _freezeEnds,
					mvp_events = _mvpEvents,
					post_think_calls = _postThinkCalls,
					rank_updates_observed = _updatesObserved,
					reveals_observed = _revealsObserved
				},
				players = list
			};
			string text = JsonSerializer.Serialize(obj, new JsonSerializerOptions
			{
				WriteIndented = true
			});
			string path = Path.Combine(((BasePlugin)this).ModuleDirectory, "logs");
			string text2 = Path.Combine(path, "status_latest.json.tmp");
			File.WriteAllText(text2, text);
			File.Move(text2, Path.Combine(path, "status_latest.json"), overwrite: true);
			if (!_transition && _bindings.Count > 0 && _bindings.Values.All(IsSame))
			{
				File.WriteAllText(Path.Combine(path, "status_last_populated.json"), text);
			}
			string text3 = AutoDiagnosticText.Render(text);
			WriteAtomicText(Path.Combine(path, "status_console.txt"), text3);
			WriteAtomicText(Path.Combine(path, "dump_console.txt"), text3);
			if (!_transition && _bindings.Count > 0 && _bindings.Values.All(IsSame))
			{
				WriteAtomicText(Path.Combine(path, "automatic_last_populated.txt"), text3);
			}
			if (reason != "heartbeat")
			{
				Log("snapshot", obj);
			}
		}
		catch (Exception ex2)
		{
			Once("snapshot_io_error", ex2.Message);
		}
	}

	private RosterObservation[] InspectRoster()
	{
		if (_transition || _maps.Epoch == 0L)
		{
			return Array.Empty<RosterObservation>();
		}
		List<RosterObservation> list = new List<RosterObservation>();
		for (int i = 0; i < 64; i++)
		{
			try
			{
				CCSPlayerController playerFromSlot = Utilities.GetPlayerFromSlot(i);
				if (playerFromSlot != null && ((CEntityInstance)playerFromSlot).IsValid)
				{
					if (!string.Equals(((CEntityInstance)playerFromSlot).DesignerName, "cs_player_controller", StringComparison.Ordinal))
					{
						RosterInput rosterInput = new RosterInput(i, Valid: true, IsController: false, Connected: false, ManagedByBotHider: false, CssIsBot: false, IsHltv: false);
						list.Add(new RosterObservation(null, rosterInput, RosterPolicy.Decide(rosterInput), ((CHandle<CEntityInstance>)(object)((NativeEntity)playerFromSlot).EntityHandle).Raw, ""));
					}
					else
					{
						bool managedByBotHider = _botHider.IsManaged(i);
						RosterInput rosterInput2 = new RosterInput(i, playerFromSlot.Slot == i, IsController: true, (int)((CBasePlayerController)playerFromSlot).Connected == 0, managedByBotHider, playerFromSlot.IsBot, ((CBasePlayerController)playerFromSlot).IsHLTV);
						list.Add(new RosterObservation(playerFromSlot, rosterInput2, RosterPolicy.Decide(rosterInput2), ((CHandle<CEntityInstance>)(object)((NativeEntity)playerFromSlot).EntityHandle).Raw, ""));
					}
				}
			}
			catch (Exception ex)
			{
				RosterInput rosterInput3 = new RosterInput(i, Valid: false, IsController: false, Connected: false, ManagedByBotHider: false, CssIsBot: false, IsHltv: false);
				list.Add(new RosterObservation(null, rosterInput3, RosterPolicy.Decide(rosterInput3), 0u, ex.Message));
			}
		}
		return list.ToArray();
	}

	private object RosterDiagnostic(RosterObservation x)
	{
		return new
		{
			slot = x.Input.Slot,
			valid = x.Input.Valid,
			player_controller = x.Input.IsController,
			connected = x.Input.Connected,
			css_is_bot = x.Input.CssIsBot,
			bh_managed = x.Input.ManagedByBotHider,
			is_hltv = x.Input.IsHltv,
			accepted = x.Decision.Include,
			reason = x.Decision.Reason,
			bound = _bindings.ContainsKey(x.Input.Slot),
			raw_handle = x.RawHandle,
			error = x.Error
		};
	}

	private object[] ObserveControllers()
	{
		List<object> list = new List<object>();
		RosterObservation[] array = InspectRoster();
		foreach (RosterObservation rosterObservation in array)
		{
			try
			{
				object roster = RosterDiagnostic(rosterObservation);
				int slot = rosterObservation.Input.Slot;
				bool connected = rosterObservation.Input.Connected;
				bool cssIsBot = rosterObservation.Input.CssIsBot;
				bool managedByBotHider = rosterObservation.Input.ManagedByBotHider;
				bool isHltv = rosterObservation.Input.IsHltv;
				bool include = rosterObservation.Decision.Include;
				string reason = rosterObservation.Decision.Reason;
				bool bound = _bindings.ContainsKey(rosterObservation.Input.Slot);
				uint rawHandle = rosterObservation.RawHandle;
				CCSPlayerController? player = rosterObservation.Player;
				list.Add(new
				{
					roster = roster,
					slot = slot,
					connected = connected,
					css_is_bot = cssIsBot,
					bh_managed = managedByBotHider,
					is_hltv = isHltv,
					accepted = include,
					reason = reason,
					bound = bound,
					raw_handle = rawHandle,
					controller_tag = Tag((player != null) ? ((CBasePlayerController)player).SteamID : 0),
					bh_tag = Tag(rosterObservation.Input.ManagedByBotHider ? _botHider.GetDisplayId(rosterObservation.Input.Slot) : 0)
				});
			}
			catch (Exception ex)
			{
				list.Add(new
				{
					slot = rosterObservation.Input.Slot,
					error = ex.Message
				});
			}
		}
		return list.ToArray();
	}

	private static int ReportedMaxPlayers()
	{
		try
		{
			return Server.MaxPlayers;
		}
		catch
		{
			return -1;
		}
	}

	private static int[] ConveniencePlayerSlots()
	{
		try
		{
			return (from p in Utilities.GetPlayers()
				where ((CEntityInstance)p).IsValid
				select p.Slot).ToArray();
		}
		catch
		{
			return Array.Empty<int>();
		}
	}

	private string Tag(ulong value)
	{
		if (value == 0L)
		{
			return "zero";
		}
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Convert.ToHexString(_logSalt) + ":" + value.ToString(CultureInfo.InvariantCulture)))).Substring(0, 12);
	}

	private void Once(string key, string text)
	{
		if (_once.Add(key))
		{
			Log(key, new
			{
				detail = text
			});
		}
	}

	private void Log(string kind, object data)
	{
		if (!_config.DiagnosticsEnabled || string.IsNullOrEmpty(_logPath))
		{
			return;
		}
		try
		{
			if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 8388608)
			{
				if (++_logPart > 3)
				{
					return;
				}
				_logPath = Path.Combine(((BasePlugin)this).ModuleDirectory, "logs", $"trace_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{_logPart}.jsonl");
			}
			File.AppendAllText(_logPath, JsonSerializer.Serialize(new
			{
				utc = DateTime.UtcNow,
				elapsed_s = _clock.Elapsed.TotalSeconds,
				tick = _tick,
				epoch = _maps.Epoch,
				phase = _phase,
				kind = kind,
				data = data
			}) + "\n");
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
