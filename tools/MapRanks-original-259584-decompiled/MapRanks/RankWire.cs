using System;
using System.IO;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;

namespace MapRanks;

internal sealed class RankWire : IDisposable
{
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int VersionFn();

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int InitFn(nint networkMessages, nint eventSystem);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int SendFn([In][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] WireRow[] rows, int count, ulong recipients);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void ResetFn();

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate nint TextFn();

	private nint _library;

	private InitFn? _init;

	private SendFn? _send;

	private ResetFn? _reset;

	private TextFn? _error;

	private TextFn? _buildInfo;

	public bool Ready { get; private set; }

	public string Error { get; private set; } = "not_initialized";

	public string BuildInfo { get; private set; } = "";

	private T Export<T>(string name) where T : Delegate
	{
		return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
	}

	public bool Initialize(string moduleDirectory)
	{
		if (Ready)
		{
			return true;
		}
		try
		{
			if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
			{
				throw new PlatformNotSupportedException("T02 wire test supports Windows x64 only.");
			}
			if (Marshal.SizeOf<WireRow>() != 16)
			{
				throw new InvalidOperationException("WireRow ABI size differs.");
			}
			if (_library == IntPtr.Zero)
			{
				string text = Path.Combine(moduleDirectory, "native", "win64", "MapRanksWire.dll");
				if (!File.Exists(text))
				{
					throw new FileNotFoundException("Build/install MapRanksWire.dll together with MapRanks.dll.", text);
				}
				_library = NativeLibrary.Load(text);
				if (Export<VersionFn>("mr_wire_abi")() != 512)
				{
					throw new InvalidOperationException("MapRanksWire ABI mismatch; install both T02 components together.");
				}
				_init = Export<InitFn>("mr_wire_init");
				_send = Export<SendFn>("mr_wire_send");
				_reset = Export<ResetFn>("mr_wire_reset");
				_error = Export<TextFn>("mr_wire_error");
				_buildInfo = Export<TextFn>("mr_wire_build_info");
				BuildInfo = Marshal.PtrToStringUTF8(_buildInfo()) ?? "";
			}
			nint valveInterface = NativeAPI.GetValveInterface(0, "NetworkMessagesVersion001");
			nint pointer = ValveInterface.GameEventSystem.Pointer;
			if (valveInterface == IntPtr.Zero || pointer == IntPtr.Zero)
			{
				throw new InvalidOperationException("Engine message/event interface unavailable.");
			}
			int num = _init(valveInterface, pointer);
			Ready = num == 0;
			Error = (Ready ? "" : NativeError(num));
		}
		catch (Exception ex)
		{
			Ready = false;
			Error = ex.GetBaseException().Message;
			if (_init == null || _send == null || _reset == null || _error == null)
			{
				if (_library != IntPtr.Zero)
				{
					NativeLibrary.Free(_library);
				}
				_library = IntPtr.Zero;
			}
		}
		return Ready;
	}

	public bool Send(WireRow[] rows, ulong recipients)
	{
		if (!Ready || _send == null)
		{
			Error = "wire_not_ready";
			return false;
		}
		if (rows.Length == 0)
		{
			return true;
		}
		int num = _send(rows, rows.Length, recipients);
		if (num != 0)
		{
			Error = NativeError(num);
			return false;
		}
		Error = "";
		return true;
	}

	private string NativeError(int code)
	{
		return $"wire_code={code}: " + ((_error == null) ? "missing error export" : (Marshal.PtrToStringUTF8(_error()) ?? "unknown"));
	}

	public void Reset()
	{
		Ready = false;
		_reset?.Invoke();
		Error = "waiting_for_map_interfaces";
	}

	public void Dispose()
	{
		Reset();
		if (_library != IntPtr.Zero)
		{
			NativeLibrary.Free(_library);
		}
		_library = IntPtr.Zero;
		_init = null;
		_send = null;
		_reset = null;
		_error = null;
		_buildInfo = null;
	}
}
