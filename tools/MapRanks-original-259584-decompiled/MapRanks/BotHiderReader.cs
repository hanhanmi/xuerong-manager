using System;
using System.Linq;
using System.Reflection;
using CounterStrikeSharp.API.Core.Capabilities;

namespace MapRanks;

internal sealed class BotHiderReader
{
	private object? _api;

	private MethodInfo? _isManaged;

	private MethodInfo? _getId;

	public bool Ready => _api != null;

	public string Error { get; private set; } = "not_resolved";

	public string ApiAssembly { get; private set; } = "";

	public int Attempts { get; private set; }

	public void Resolve()
	{
		if (Ready)
		{
			return;
		}
		Attempts++;
		string error = "IBotHiderApi not loaded; fallback is CSS.IsBot + existing controller SteamID (no writes)";
		foreach (Assembly item in from a in AppDomain.CurrentDomain.GetAssemblies()
			where (a.GetName().Name ?? "").Contains("BotHider", StringComparison.OrdinalIgnoreCase)
			select a)
		{
			Type[] source;
			try
			{
				source = item.GetExportedTypes();
			}
			catch (ReflectionTypeLoadException ex)
			{
				source = ex.Types.Where((Type t) => t != null).Cast<Type>().ToArray();
			}
			catch
			{
				continue;
			}
			foreach (Type item2 in source.Where((Type t) => t.IsInterface && t.Name == "IBotHiderApi"))
			{
				try
				{
					Type type = typeof(PluginCapability<>).MakeGenericType(item2);
					object obj2 = Activator.CreateInstance(type, "bothider:api");
					object obj3 = type.GetMethod("Get", BindingFlags.Instance | BindingFlags.Public).Invoke(obj2, null);
					if (obj3 == null)
					{
						continue;
					}
					MethodInfo method = item2.GetMethod("IsManagedBot", new Type[1] { typeof(int) });
					MethodInfo method2 = item2.GetMethod("GetBotSteamId", new Type[1] { typeof(int) });
					if (method?.ReturnType != typeof(bool) || method2?.ReturnType != typeof(ulong))
					{
						throw new InvalidOperationException("BotHider read API signature differs from documented contract.");
					}
					_api = obj3;
					_isManaged = method;
					_getId = method2;
					ApiAssembly = item.FullName ?? item.GetName().Name ?? "";
					Error = "";
					return;
				}
				catch (Exception ex2)
				{
					error = ex2.GetBaseException().Message;
				}
			}
		}
		Error = error;
	}

	public bool IsManaged(int slot)
	{
		if (_api == null)
		{
			return false;
		}
		try
		{
			return (bool)_isManaged.Invoke(_api, new object[1] { slot });
		}
		catch (Exception ex)
		{
			Fail(ex);
			return false;
		}
	}

	public ulong GetDisplayId(int slot)
	{
		if (_api == null)
		{
			return 0uL;
		}
		try
		{
			return (ulong)_getId.Invoke(_api, new object[1] { slot });
		}
		catch (Exception ex)
		{
			Fail(ex);
			return 0uL;
		}
	}

	private void Fail(Exception ex)
	{
		Error = ex.GetBaseException().Message;
		_api = null;
		_isManaged = null;
		_getId = null;
	}

	public object Diagnostics()
	{
		return new
		{
			Ready = Ready,
			Error = Error,
			ApiAssembly = ApiAssembly,
			Attempts = Attempts,
			policy = "read-only; no SteamID/name/avatar/ping/flair/identity-mode writes"
		};
	}
}
