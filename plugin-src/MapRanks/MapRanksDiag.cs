using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace MapRanks;

/// <summary>
/// Temporary read-only diagnostics used to recover the rank push path after the
/// CS2 build change invalidated the native MapRanksWire vtable offset.
/// Nothing here writes to the engine; it only reads pointers and current values.
/// </summary>
public sealed partial class MapRanksPlugin
{
	private bool _diagVTableDone;
	private bool _diagProbeDone;
	private int _diagRankSamples;

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate nint ProbeFn(nint nm, string name);

	internal void DiagProbeVTableSlots()
	{
		if (_diagProbeDone)
		{
			return;
		}
		_diagProbeDone = true;
		try
		{
			nint iface = NativeAPI.GetValveInterface(0, "NetworkMessagesVersion001");
			if (iface == nint.Zero)
			{
				Logger.LogWarning("[MapRanks][diag] probe: nm interface is null");
				return;
			}
			string dllPath = Path.Combine(((BasePlugin)this).ModuleDirectory, "native", "win64", "XrRankProbe.dll");
			if (!File.Exists(dllPath))
			{
				Logger.LogWarning("[MapRanks][diag] probe: missing {Path}", dllPath);
				return;
			}
			nint lib = NativeLibrary.Load(dllPath);
			nint fn = NativeLibrary.GetExport(lib, "xr_probe");
			ProbeFn probe = Marshal.GetDelegateForFunctionPointer<ProbeFn>(fn);
			nint text = probe(iface, "CCSUsrMsg_ServerRankUpdate");
			string body = text == nint.Zero ? "(null)" : (Marshal.PtrToStringAnsi(text) ?? "(empty)");
			Logger.LogInformation("[MapRanks][diag] probe {Text}", body);
		}
		catch (Exception ex)
		{
			Logger.LogWarning(ex, "[MapRanks][diag] probe failed");
		}
	}

	internal void DiagDumpNetworkMessagesVTable()
	{
		if (_diagVTableDone)
		{
			return;
		}
		_diagVTableDone = true;
		try
		{
			nint iface = NativeAPI.GetValveInterface(0, "NetworkMessagesVersion001");
			Logger.LogInformation("[MapRanks][diag] nm_iface=0x{Iface:X}", iface.ToInt64());
			if (iface == nint.Zero)
			{
				return;
			}
			nint vt = Marshal.ReadIntPtr(iface);
			Logger.LogInformation("[MapRanks][diag] nm_vtable=0x{Vt:X}", vt.ToInt64());

			List<(long Base, string Name)> mods = new List<(long, string)>();
			foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
			{
				mods.Add((m.BaseAddress.ToInt64(), m.ModuleName));
			}
			mods.Sort((a, b) => a.Base.CompareTo(b.Base));

			for (int i = 0; i <= 40; i++)
			{
				long addr = Marshal.ReadIntPtr(vt, i * IntPtr.Size).ToInt64();
				string where = "unknown";
				foreach ((long b, string n) in mods)
				{
					if (addr >= b)
					{
						where = n + "+0x" + (addr - b).ToString("x");
					}
				}
				Logger.LogInformation("[MapRanks][diag] vt[{Index}] off=0x{Off:X} addr=0x{Addr:X} {Where}", i, i * IntPtr.Size, addr, where);
			}
		}
		catch (Exception ex)
		{
			Logger.LogWarning(ex, "[MapRanks][diag] vtable dump failed");
		}
	}

	internal void DiagProbeRanks()
	{
		if (_diagRankSamples >= 12 || _tick % 192 != 0L)
		{
			return;
		}
		_diagRankSamples++;
		try
		{
			List<string> roster = new List<string>();
			foreach (RosterObservation o in InspectRoster())
			{
				if (!o.Decision.Include && o.Input.Slot > 20)
				{
					continue;
				}
				roster.Add(o.Input.Slot + ":" + o.Decision.Reason + ":inc=" + (o.Decision.Include ? 1 : 0)
					+ ":cssbot=" + (o.Input.CssIsBot ? 1 : 0) + ":bh=" + (o.Input.ManagedByBotHider ? 1 : 0)
					+ ":conn=" + (o.Input.Connected ? 1 : 0));
			}
			Logger.LogInformation("[MapRanks][diag] s{N} bindings={Count} roster[{Roster}]",
				_diagRankSamples, _bindings.Count, string.Join(",", roster));

			List<string> rows = new List<string>();
			foreach (Binding b in _bindings.Values)
			{
				if (!IsSame(b))
				{
					rows.Add(b.Slot + ":stale");
					continue;
				}
				int actual = CoreConfig.FollowCS2ServerGuidelines ? -1 : b.Player.CompetitiveRanking;
				rows.Add(b.Slot + ":" + (b.IsBot ? "BOT" : "HUM") + ":a=" + b.Rank.Rating + ":t=" + b.Rank.Type + ":act=" + actual);
			}
			Logger.LogInformation("[MapRanks][diag] s{N} rows[{Rows}]", _diagRankSamples, string.Join(",", rows));
		}
		catch (Exception ex)
		{
			Logger.LogWarning(ex, "[MapRanks][diag] probe failed");
		}
	}
}
