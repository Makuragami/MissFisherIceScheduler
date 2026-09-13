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
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System.Numerics;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using SheetAetheryte = Lumina.Excel.Sheets.Aetheryte;

namespace MissFisherIceScheduler;

internal sealed class IceTravelHelper
{
    private static readonly Vector3 EntranceWorldPosition = new(25.680908f, -137.41669f, -411.30695f);
    private static readonly HashSet<uint> IceTerritories = [1237, 1291, 1310, 1319];

    public static bool IsIceTerritory(uint territoryId) => IceTerritories.Contains(territoryId);

    private enum Phase
    {
        Resolving,
        WaitingForTeleport,
        MovingToNpc,
        Interacting,
    }

    private enum SelectStringAction
    {
        None,
        OpenedAreaMenu,
        SelectedDestination,
    }

    private enum PlanetSelectAction
    {
        None,
        WaitingForTarget,
        WaitingForMoveButton,
        ClickedMove,
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
    private DateTime entranceSelectionUtc;
    private DateTime areaMenuRequestUtc;
    private bool moveRequested;
    private bool areaMenuRequested;
    private bool planetUiLogged;

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
        entranceSelectionUtc = DateTime.MinValue;
        areaMenuRequestUtc = DateTime.MinValue;
        moveRequested = false;
        areaMenuRequested = false;
        planetUiLogged = false;
    }

    public bool Tick(DateTime now, Configuration config, out string? error)
    {
        error = null;
        try
        {
            if (clientState.TerritoryType == config.IceTerritoryId)
                return true;

            if (IsIceTerritory(clientState.TerritoryType))
            {
                error = $"进入了错误的 ICE 区域 {clientState.TerritoryType}，目标为 {config.IceTerritoryId}";
                return false;
            }

            return phase switch
            {
                Phase.Resolving => TickResolveAndTeleport(now, config, out error),
                Phase.WaitingForTeleport => TickWaitForTeleport(now, config),
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
        nextActionUtc = now.AddSeconds(12);
        Status = $"正在传送到 {config.IceEntranceAetheryteName}";
        log.Information("Built-in ICE travel: teleport requested, aetheryte={AetheryteId}, territory={TerritoryId}",
            match.Row.RowId, entranceTerritoryId);
        return false;
    }

    private bool TickWaitForTeleport(DateTime now, Configuration config)
    {
        if (now >= nextActionUtc)
        {
            phase = Phase.Resolving;
            Status = $"传送请求未生效，正在重试 {config.IceEntranceAetheryteName}";
            log.Warning("Built-in ICE travel: teleport did not complete within timeout; retrying, current={CurrentTerritory}, expected={ExpectedTerritory}, betweenAreas={BetweenAreas}",
                clientState.TerritoryType, entranceTerritoryId,
                condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]);
            return false;
        }

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
            return false;
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
        var destination = npc?.Position ?? EntranceWorldPosition;
        if (npc is null && now >= nextNpcScanLogUtc)
        {
            log.Information("Built-in ICE travel: entrance NPC outside object range; using known world position={Position}",
                EntranceWorldPosition);
            nextNpcScanLogUtc = now.AddSeconds(15);
        }
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
            if (npc is not null)
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

        if (TryAdvanceTalk())
        {
            Status = "正在处理前往宇宙探索区域的对话";
            return false;
        }

        // 目标区域已经选定后只处理确认窗口并等待换区。SelectString 可能还会
        // 短暂保持可见，若再次点击会不断刷新等待计时，导致永远无法超时重试。
        if (entranceSelectionUtc != DateTime.MinValue)
        {
            if (TryConfirmYes())
            {
                Status = "正在确认进入 Auxesia";
                return false;
            }

            if (now - entranceSelectionUtc < TimeSpan.FromSeconds(20))
            {
                Status = "已选择 Auxesia，等待区域切换";
                return false;
            }

            entranceSelectionUtc = DateTime.MinValue;
            areaMenuRequested = false;
            areaMenuRequestUtc = DateTime.MinValue;
            log.Warning("Built-in ICE travel: entrance selection did not change territory; retrying NPC interaction");
        }

        var planetAction = TryHandlePlanetSelect(config.IceTerritoryId);
        if (planetAction == PlanetSelectAction.ClickedMove)
        {
            entranceSelectionUtc = now;
            Status = "已在目的地界面选择奥克塞西亚并点击移动，等待换区";
            log.Information("Built-in ICE travel: clicked WKSPlanetSelect Move for targetTerritory={Territory}",
                config.IceTerritoryId);
            return false;
        }
        if (planetAction == PlanetSelectAction.WaitingForTarget)
        {
            Status = "目的地界面当前不是奥克塞西亚，等待选择目标";
            return false;
        }
        if (planetAction == PlanetSelectAction.WaitingForMoveButton)
        {
            Status = "已识别奥克塞西亚，正在定位“移动”按钮";
            return false;
        }

        var selectAction = TrySelectString(config.IceTerritoryId, out var selectedIndex, out var entryCount);
        if (selectAction == SelectStringAction.OpenedAreaMenu)
        {
            areaMenuRequestUtc = now;
            Status = $"已打开宇宙探索区域选择（入口菜单共 {entryCount} 项）";
            log.Information("Built-in ICE travel: opened destination submenu from entry index={Index}, entryCount={Count}",
                selectedIndex, entryCount);
            return false;
        }

        if (selectAction == SelectStringAction.SelectedDestination)
        {
            entranceSelectionUtc = now;
            Status = $"已选择 Auxesia 入口（第 {selectedIndex + 1} 项），等待换区";
            log.Information("Built-in ICE travel: selected destination index={Index}, entryCount={Count}, targetTerritory={Territory}",
                selectedIndex, entryCount, config.IceTerritoryId);
            return false;
        }

        if (TryConfirmYes())
        {
            Status = "正在确认进入 Auxesia";
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

    private unsafe SelectStringAction TrySelectString(uint targetTerritoryId, out int selectedIndex, out int entryCount)
    {
        selectedIndex = targetTerritoryId switch
        {
            1237 => 0,
            1291 => 1,
            1310 => 2,
            1319 => 3,
            _ => 3,
        };
        entryCount = 0;
        var addon = (AddonSelectString*)gameGui.GetAddonByName("SelectString", 1).Address;
        if (addon is null || !addon->AtkUnitBase.IsVisible || !addon->AtkUnitBase.IsReady)
            return SelectStringAction.None;
        entryCount = addon->PopupMenu.PopupMenu.EntryCount;
        if (entryCount <= 0)
            return SelectStringAction.None;

        // 驾行威先显示入口操作菜单（当前中文客户端为 3 项），并不是四个区域。
        // 先选第一项打开区域列表，之后才按目标 Territory 的固定顺序选择。
        // 不可把索引 3 clamp 成索引 2，否则会点中取消/返回并无限重试。
        if (!areaMenuRequested && entryCount < 4)
        {
            selectedIndex = 0;
            addon->FireCallbackInt(selectedIndex);
            areaMenuRequested = true;
            return SelectStringAction.OpenedAreaMenu;
        }

        if (entryCount < 4)
        {
            if (areaMenuRequestUtc != DateTime.MinValue &&
                DateTime.UtcNow - areaMenuRequestUtc >= TimeSpan.FromSeconds(5))
            {
                selectedIndex = 0;
                addon->FireCallbackInt(selectedIndex);
                areaMenuRequestUtc = DateTime.UtcNow;
                log.Warning("Built-in ICE travel: destination submenu did not appear; retrying entry index 0, entryCount={Count}",
                    entryCount);
                return SelectStringAction.OpenedAreaMenu;
            }
            Status = $"等待区域选择菜单（当前仍为 {entryCount} 项）";
            return SelectStringAction.None;
        }

        selectedIndex = Math.Clamp(selectedIndex, 0, entryCount - 1);
        addon->FireCallbackInt(selectedIndex);
        return SelectStringAction.SelectedDestination;
    }

    private unsafe bool TryConfirmYes()
    {
        var addon = (AddonSelectYesno*)gameGui.GetAddonByName("SelectYesno", 1).Address;
        if (addon is null || !addon->AtkUnitBase.IsVisible || !addon->AtkUnitBase.IsReady)
            return false;
        addon->AtkUnitBase.FireCallbackInt(0);
        return true;
    }

    private unsafe PlanetSelectAction TryHandlePlanetSelect(uint targetTerritoryId)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("WKSPlanetSelect", 1).Address;
        if (addon is null || !addon->IsVisible || !addon->IsReady)
            return PlanetSelectAction.None;

        var texts = new List<string>();
        var buttonCandidates = new List<string>();
        AtkComponentButton* moveButton = null;
        AtkComponentButton* bottomButton = null;
        var bottomButtonY = float.MinValue;
        var visitedManagers = new HashSet<nint>();
        ScanPlanetUi(&addon->UldManager, texts, buttonCandidates, ref moveButton,
            ref bottomButton, ref bottomButtonY, visitedManagers);

        // “移动”文本和按钮组件并非直接关联。文字定位失败时，使用该界面
        // 最靠下的可见宽按钮；在 WKSPlanetSelect 中它就是底部移动键。
        if (moveButton is null)
            moveButton = bottomButton;

        if (!planetUiLogged)
        {
            log.Information("Built-in ICE travel: WKSPlanetSelect visible, texts=[{Texts}], buttons=[{Buttons}], moveButtonFound={MoveFound}",
                string.Join(" | ", texts.Distinct()), string.Join(" | ", buttonCandidates), moveButton is not null);
            planetUiLogged = true;
        }

        var targetVisible = targetTerritoryId == 1319
            && texts.Any(x => x.Contains("奥克塞西亚", StringComparison.OrdinalIgnoreCase)
                || x.Contains("Auxesia", StringComparison.OrdinalIgnoreCase));
        if (!targetVisible)
            return PlanetSelectAction.WaitingForTarget;
        if (moveButton is null)
            return PlanetSelectAction.WaitingForMoveButton;

        var ownerNode = moveButton->AtkComponentBase.OwnerNode;
        var clickEvent = ownerNode is null ? null : ownerNode->AtkResNode.AtkEventManager.Event;
        if (clickEvent is null)
        {
            log.Warning("Built-in ICE travel: WKSPlanetSelect Move button has no click event");
            return PlanetSelectAction.WaitingForTarget;
        }

        addon->ReceiveEvent(clickEvent->State.EventType, (int)clickEvent->Param, clickEvent, null);
        return PlanetSelectAction.ClickedMove;
    }

    private unsafe void ScanPlanetUi(AtkUldManager* manager, List<string> texts, List<string> buttonCandidates,
        ref AtkComponentButton* moveButton, ref AtkComponentButton* bottomButton, ref float bottomButtonY,
        HashSet<nint> visitedManagers)
    {
        if (manager is null || manager->NodeList is null || !visitedManagers.Add((nint)manager))
            return;

        for (var i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node is null || !node->IsVisible())
                continue;

            if (node->Type == NodeType.Text)
            {
                var value = ((AtkTextNode*)node)->NodeText.ToString().Trim();
                if (!string.IsNullOrEmpty(value))
                    texts.Add(value);
                continue;
            }

            if (node->Type != NodeType.Component)
                continue;
            var component = ((AtkComponentNode*)node)->Component;
            if (component is null)
                continue;

            var componentType = component->GetComponentType();
            AtkComponentButton* button = null;
            if (componentType == ComponentType.Button)
                button = (AtkComponentButton*)component;
            else if (componentType == ComponentType.HoldButton)
                button = &((AtkComponentHoldButton*)component)->AtkComponentButton;

            if (button is not null)
            {
                var buttonText = button->ButtonTextNode is null
                    ? string.Empty
                    : button->ButtonTextNode->NodeText.ToString().Trim();
                if (!string.IsNullOrEmpty(buttonText))
                    texts.Add(buttonText);
                if (buttonText.Contains("移动", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(buttonText, "Move", StringComparison.OrdinalIgnoreCase))
                    moveButton = button;

                var owner = button->AtkComponentBase.OwnerNode;
                if (owner is not null)
                {
                    var res = &owner->AtkResNode;
                    buttonCandidates.Add($"id={res->NodeId},type={componentType},text={buttonText},x={res->ScreenX:F0},y={res->ScreenY:F0},w={res->Width},h={res->Height},enabled={button->IsEnabled}");
                    var bottom = res->ScreenY + res->Height;
                    if (button->IsEnabled && res->Width >= 80 && res->Height >= 18 && bottom > bottomButtonY)
                    {
                        bottomButton = button;
                        bottomButtonY = bottom;
                    }
                }
            }

            ScanPlanetUi(&component->UldManager, texts, buttonCandidates, ref moveButton,
                ref bottomButton, ref bottomButtonY, visitedManagers);
        }
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
