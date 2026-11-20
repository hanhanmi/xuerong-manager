using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace BotFreezeActions;

/// <summary>
/// 已停用：不再向人机下发任何指令，也不注册定时器。
/// </summary>
public sealed class BotFreezeActionsPlugin : BasePlugin
{
    public override string ModuleName => "Bot Freeze Actions";
    public override string ModuleVersion => "6.0.0";
    public override string ModuleAuthor => "XueRong";
    public override string ModuleDescription => "Disabled: does nothing.";

    public override void Load(bool hotReload)
    {
        Logger.LogInformation("Bot Freeze Actions disabled; no timers, no commands.");
    }
}
