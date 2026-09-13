using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using System.Numerics;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using SheetAetheryte = Lumina.Excel.Sheets.Aetheryte;

namespace MissFisherIceScheduler;

internal sealed class IceTravelHelper
{
    private enum Phase
    {
        Resolving,
        WaitingForTeleport,
        MovingToNpc,
        Interacting,
    }

    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<bool> navReady;
    private readonly ICallGateSubscriber<bool> pathRunning;
    private readonly ICallGateSubscriber<Vector3, bool, bool> moveTo;
    private readonly ICallGateSubscriber<object> pathStop;

    private Phase phase;
    private uint entranceTerritoryId;
    private DateTime nextActionUtc;
    private DateTime nextNpcScanLogUtc;
    private bool moveRequested;

    public IceTravelHelper(IDalamudPluginInterface pi, IClientState clientState, ICondition condition,
        IDataManager dataManager, IGameGui gameGui, IObjectTable objects, IPluginLog log)
    {
        this.clientState = clientState;
        this.condition = condition;
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.objects = objects;
        this.log = log;
        navReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        pathRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        moveTo = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        pathStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    public string Status { get; private set; } = string.Empty;

    public void Reset()
    {
        phase = Phase.Resolving;
        entranceTerritoryId = 0;
        nextActionUtc = DateTime.MinValue;
        nextNpcScanLogUtc = DateTime.MinValue;
        moveRequested = false;
    }

    public bool Tick(DateTime now, Configuration config, out string? error)
    {
        error = null;
        try
        {
            if (clientState.TerritoryType == config.IceTerritoryId)
                return true;

            return phase switch
            {
                Phase.Resolving => TickResolveAndTeleport(now, config, out error),
                Phase.WaitingForTeleport => TickWaitForTeleport(config),
                Phase.MovingToNpc => TickMoveToNpc(now, config, out error),
                Phase.Interacting => TickInteract(now, config),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            log.Error(ex, "Built-in ICE travel failed");
            error = $"内置 ICE 旅行异常：{ex.Message}";
            return false;
        }
    }

    private unsafe bool TickResolveAndTeleport(DateTime now, Configuration config, out string? error)
    {
        error = null;
        var aetherytes = dataManager.GetExcelSheet<SheetAetheryte>();
        var match = aetherytes
            .Where(x => x.IsAetheryte)
            .Select(x => new
            {
                Row = x,
                Place = x.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty,
                Network = x.AethernetName.ValueNullable?.Name.ToString() ?? string.Empty,
            })
            .FirstOrDefault(x => x.Row.RowId == config.IceEntranceAetheryteId);
        match ??= aetherytes
            .Where(x => x.IsAetheryte)
            .Select(x => new
            {
                Row = x,
                Place = x.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty,
                Network = x.AethernetName.ValueNullable?.Name.ToString() ?? string.Empty,
            })
            .FirstOrDefault(x => string.Equals(x.Place, config.IceEntranceAetheryteName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Network, config.IceEntranceAetheryteName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            error = $"找不到入口以太之光：ID {config.IceEntranceAetheryteId} / {config.IceEntranceAetheryteName}";
            return false;
        }

        entranceTerritoryId = match.Row.Territory.RowId;
        if (clientState.TerritoryType == entranceTerritoryId)
        {
            phase = Phase.MovingToNpc;
            Status = "已在最佳兔威洞所在区域，准备前往架行威";
            return false;
        }

        var telepo = Telepo.Instance();
        if (telepo is null)
        {
            error = "游戏传送服务尚未就绪";
            return false;
        }

        telepo->Teleport(match.Row.RowId, 0);
        phase = Phase.WaitingForTeleport;
        nextActionUtc = now.AddSeconds(2);
        Status = $"正在传送到 {config.IceEntranceAetheryteName}";
        log.Information("Built-in ICE travel: teleport requested, aetheryte={AetheryteId}, territory={TerritoryId}",
            match.Row.RowId, entranceTerritoryId);
        return false;
    }

    private bool TickWaitForTeleport(Configuration config)
    {
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            Status = $"正在传送到 {config.IceEntranceAetheryteName}";
            return false;
        }

        if (entranceTerritoryId != 0 && clientState.TerritoryType == entranceTerritoryId)
        {
            phase = Phase.MovingToNpc;
            moveRequested = false;
            Status = "传送完成，准备前往架行威";
        }
        return false;
    }

    private bool TickMoveToNpc(DateTime now, Configuration config, out string? error)
    {
        error = null;
        var player = objects.LocalPlayer;
        if (player is null)
            return false;

        var npc = FindEntranceNpc(config, player);
        if (npc is null)
        {
            // The configured coordinates are map/UI coordinates, not world coordinates.
            // Wait for the actual event NPC instead of sending an unreachable point to vnavmesh.
            Status = $"等待入口 NPC：{config.IceEntranceNpcName}";
            if (now >= nextNpcScanLogUtc)
            {
                var nearby = objects
                    .Where(x => Vector3.DistanceSquared(x.Position, player.Position) <= 10000f)
                    .Where(x => x.ObjectKind is DalamudObjectKind.EventNpc or DalamudObjectKind.BattleNpc)
                    .Take(30)
                    .Select(x => $"{x.Name} kind={x.ObjectKind} baseId={x.BaseId} targetable={x.IsTargetable} pos={x.Position}")
                    .ToArray();
                log.Information("Built-in ICE travel: entrance NPC not resolved; nearby objects: {Objects}",
                    nearby.Length == 0 ? "<none>" : string.Join(" | ", nearby));
                nextNpcScanLogUtc = now.AddSeconds(5);
            }
            return false;
        }

        var destination = npc.Position;
        var distance = Vector3.Distance(player.Position, destination);
        if (distance <= 4.5f)
        {
            TryStopPath();
            phase = Phase.Interacting;
            nextActionUtc = now;
            Status = "已到达架行威附近，准备交互";
            return false;
        }

        if (!moveRequested)
        {
            bool ready;
            try { ready = navReady.InvokeFunc(); }
            catch (Exception ex)
            {
                error = $"vnavmesh IPC 不可用：{ex.Message}";
                return false;
            }
            if (!ready)
            {
                Status = "等待 vnavmesh 地图构建完成";
                return false;
            }
            log.Information("Built-in ICE travel: resolved NPC name={Name}, dataId={DataId}, worldPosition={Position}",
                npc.Name, npc.BaseId, destination);
            log.Information("Built-in ICE travel: vnav target NPC world position={Position}", destination);
            if (!moveTo.InvokeFunc(destination, false))
            {
                error = "vnavmesh 未能开始前往架行威";
                return false;
            }
            moveRequested = true;
        }

        Status = $"正在前往架行威，剩余 {distance:F1} 米";
        return false;
    }

    private unsafe bool TickInteract(DateTime now, Configuration config)
    {
        if (now < nextActionUtc)
            return false;

        nextActionUtc = now.AddMilliseconds(700);

        if (TryAdvanceTalk() || TrySelectString(config.IceEntranceOptionIndex) || TryConfirmYes())
        {
            Status = "正在处理前往宇宙探索区域的对话";
            return false;
        }

        var player = objects.LocalPlayer;
        if (player is null)
            return false;

        var npc = FindEntranceNpc(config, player);
        if (npc is null)
        {
            Status = $"在入口附近等待 NPC：{config.IceEntranceNpcName}";
            return false;
        }

        if (condition[ConditionFlag.Mounted])
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 9);
            Status = "正在下坐骑，准备与架行威交互";
            return false;
        }

        TargetSystem.Instance()->InteractWithObject((ClientGameObject*)npc.Address, false);
        Status = $"正在与 {config.IceEntranceNpcName} 交互";
        return false;
    }

    private IGameObject? FindEntranceNpc(Configuration config, IGameObject player)
    {
        var configuredName = config.IceEntranceNpcName.Trim();
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            configuredName,
            "架行威",
            "驾行威",
            "驾驶威",
            "Drivingway",
        };

        return objects
            .Where(x => x.IsTargetable && (x.ObjectKind is DalamudObjectKind.EventNpc or DalamudObjectKind.BattleNpc))
            .Where(x => aliases.Contains(x.Name.ToString().Trim()))
            .OrderBy(x => Vector3.DistanceSquared(x.Position, player.Position))
            .FirstOrDefault();
    }

    private unsafe bool TryAdvanceTalk()
    {
        var addon = (AddonTalk*)gameGui.GetAddonByName("Talk", 1).Address;
        if (addon is null || !addon->AtkUnitBase.IsVisible || !addon->AtkUnitBase.IsReady)
            return false;
        addon->AtkUnitBase.FireCallbackInt(0);
        return true;
    }

    private unsafe bool TrySelectString(int index)
    {
        var addon = (AddonSelectString*)gameGui.GetAddonByName("SelectString", 1).Address;
        if (addon is null || !addon->AtkUnitBase.IsVisible || !addon->AtkUnitBase.IsReady)
            return false;
        var count = addon->PopupMenu.PopupMenu.EntryCount;
        if (count <= 0)
            return false;
        addon->FireCallbackInt(Math.Clamp(index, 0, count - 1));
        return true;
    }

    private unsafe bool TryConfirmYes()
    {
        var addon = (AddonSelectYesno*)gameGui.GetAddonByName("SelectYesno", 1).Address;
        if (addon is null || !addon->AtkUnitBase.IsVisible || !addon->AtkUnitBase.IsReady)
            return false;
        addon->AtkUnitBase.FireCallbackInt(0);
        return true;
    }

    private void TryStopPath()
    {
        try
        {
            if (pathRunning.InvokeFunc())
                pathStop.InvokeAction();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Unable to stop vnavmesh path");
        }
    }
}
