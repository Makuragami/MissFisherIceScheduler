using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Numerics;

namespace MissFisherIceScheduler;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/mfice";
    private const uint FisherJobId = 18;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly PluginIpc ipc;
    private readonly GearsetHelper gearsets;
    private readonly EquipmentRepairHelper equipmentRepair;
    private readonly MissFisherTargetReader targetReader = new();
    private readonly IceTravelHelper iceTravel;
    private Configuration config;
    private SchedulerState state = SchedulerState.Idle;
    private DateTime stateSinceUtc = DateTime.UtcNow;
    private DateTime nextTickUtc;
    private DateTime nextDryRunLogUtc;
    private DateTime? windowStartUtc;
    private int? fisherGearsetId;
    private bool iceOwned;
    private bool actionSent;
    private string[] travelCommands = [];
    private int travelCommandIndex;
    private DateTime nextTravelCommandUtc;
    private bool resumeSent;
    private string cycleChecklistId = string.Empty;
    private string cycleChecklistName = string.Empty;
    private MissFisherResumeKind cycleResumeKind;
    private string status = "等待启用";
    private string lastError = string.Empty;
    private bool windowOpen;
    private uint settleTerritoryId;
    private DateTime territoryStableSinceUtc;
    private readonly Queue<string> uiLogs = new();
    private bool logAutoScroll = true;
    private bool testMode;
    private bool testMissionObserved;
    private uint observedMissionId;
    private DateTime iceRunStartedUtc;
    private DateTime suppressAutoUntilUtc;
    private string cycleOutcomeMessage = string.Empty;
    private int activeIceGearsetId = -1;
    private uint activeIceJobId;
    private string activeIceJobName = string.Empty;
    private readonly HashSet<uint> failedIceJobsThisCycle = [];
    private bool rotatingIceJob;
    private bool iceStopAfterCurrentOwned;
    private bool artisanStopRequestOwned;
    private DateTime nextIceStopRetryUtc;
    private DateTime? stopConfirmedSinceUtc;
    private bool stopTimeoutLogged;
    private int iceGearOptimizationStep;
    private DateTime nextIceGearActionUtc;
    private bool repairIceGearAfterStop;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework,
        ICondition condition, IClientState clientState, IPlayerState playerState, IDataManager dataManager,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui, IObjectTable objects, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.framework = framework;
        this.condition = condition;
        this.clientState = clientState;
        this.playerState = playerState;
        this.log = log;
        this.addonLifecycle = addonLifecycle;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Checkpoint ??= new CycleCheckpoint();
        config.ExcludedIceJobIds ??= [];
        config.Checkpoint.FailedIceJobIds ??= [];
        ipc = new PluginIpc(pluginInterface, log);
        iceTravel = new IceTravelHelper(pluginInterface, clientState, condition, dataManager, gameGui, objects, log,
            message => AddUiLog("旅行", message));
        gearsets = new GearsetHelper(playerState, dataManager);
        equipmentRepair = new EquipmentRepairHelper(gameGui, condition);
        commands.AddHandler(Command, new CommandInfo(OnCommand) { HelpMessage = "/mfice - 打开配置；enable | disable | abort | reset | status" });
        framework.Update += OnUpdate;
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += Open;
        pluginInterface.UiBuilder.OpenMainUi += Open;
        addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "WKSPlanetSelect", OnPlanetSelectReceiveEvent);
        ReconcileCheckpoint();
        AddUiLog("信息", $"插件已加载，版本 {GetType().Assembly.GetName().Version}");
    }

    public string Name => "MissFisher ICE Scheduler";

    public void Dispose()
    {
        if (iceOwned)
        {
            ipc.TryDisableIce();
            if (ipc.TryStopArtisan()) artisanStopRequestOwned = true;
        }
        ClearOwnedIceStopAfterCurrent();
        if (testMode) ipc.TrySetIceStopAfterCurrent(false);
        framework.Update -= OnUpdate;
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= Open;
        pluginInterface.UiBuilder.OpenMainUi -= Open;
        addonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, "WKSPlanetSelect", OnPlanetSelectReceiveEvent);
        commands.RemoveHandler(Command);
    }

    private void OnUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < nextTickUtc) return;
        nextTickUtc = now.AddMilliseconds(500);

        ipc.TryGetFisher(out var fisher);
        switch (state)
        {
            case SchedulerState.Idle: TickIdle(now, fisher); break;
            case SchedulerState.PausingFisher: TickPausing(now, fisher); break;
            case SchedulerState.WaitingForFisherTravel: TickWaitingForFisherTravel(now, fisher); break;
            case SchedulerState.TravellingToIce: TickTravelling(now); break;
            case SchedulerState.EquippingIceJob: TickEquippingIce(now); break;
            case SchedulerState.OptimizingIceGear: TickOptimizingIceGear(now); break;
            case SchedulerState.RepairingIceGear: TickRepairingIceGear(now); break;
            case SchedulerState.StartingIce: TickStartingIce(now); break;
            case SchedulerState.RunningIce: TickRunningIce(now); break;
            case SchedulerState.StoppingIce: TickStoppingIce(now); break;
            case SchedulerState.RestoringFisher: TickRestoring(now); break;
            case SchedulerState.ResumingFisher: TickResuming(now, fisher); break;
            case SchedulerState.RestartingFisher: TickRestarting(now, fisher); break;
        }
    }

    private unsafe void OnPlanetSelectReceiveEvent(AddonEvent _, AddonArgs args)
    {
        if (state != SchedulerState.TravellingToIce || args is not AddonReceiveEventArgs receive)
            return;

        var eventType = (int)receive.AtkEventType;
        var eventPtr = (FFXIVClientStructs.FFXIV.Component.GUI.AtkEvent*)receive.AtkEvent;
        var nodeId = eventPtr is null || eventPtr->Node is null ? 0u : eventPtr->Node->NodeId;
        log.Information("WKSPlanetSelect user event: type={Type}({TypeId}), param={Param}, nodeId={NodeId}",
            receive.AtkEventType, eventType, receive.EventParam, nodeId);

        // WKSPlanetSelect uses ButtonClick param 1 for changing the selected planet and
        // param 7 for the bottom Move button. Never let navigation clicks overwrite Move.
        if (eventType != (int)FFXIVClientStructs.FFXIV.Component.GUI.AtkEventType.ButtonClick
            || receive.EventParam != 7)
            return;

        config.PlanetMoveEventType = eventType;
        config.PlanetMoveEventParam = receive.EventParam;
        config.PlanetMoveEventNodeId = nodeId;
        Save();
        log.Information("Captured WKSPlanetSelect Move event: type={TypeId}, param={Param}, nodeId={NodeId}",
            eventType, receive.EventParam, nodeId);
    }

    private void TickIdle(DateTime now, MissFisherSnapshot fisher)
    {
        if (!config.Enabled) { status = "调度已关闭"; return; }
        if (now < suppressAutoUntilUtc)
        {
            status = $"上次 ICE 启动无可执行任务，{Math.Ceiling((suppressAutoUntilUtc - now).TotalSeconds)} 秒后再检查";
            return;
        }
        if (!ipc.TryGetIce(out var ice)) { status = "等待 ICE IPC"; return; }
        if (ice.IsRunning) { status = "ICE 已由外部启动，不接管"; return; }

        var valid = TryGetRemaining(now, fisher, out var remaining, out var source);
        var required = TimeSpan.FromMinutes(config.MinimumIceMinutes + config.RecoveryReserveMinutes).TotalSeconds;
        var eligible = fisher.IsRunning && fisher.IsWaiting && !fisher.IsPaused && !fisher.IsWindowActive
            && !fisher.IsInWindow && !fisher.IsAutoPreparing && valid && remaining >= Math.Max(required, config.WaitThresholdMinutes * 60d)
            && playerState.IsLoaded && playerState.ClassJob.RowId == FisherJobId && IsSafe();

        status = eligible ? $"检测到钓鱼空档 {Format(remaining)}（{source}）" : DescribeIdle(fisher, valid, remaining, source);
        if (!eligible) return;
        if (config.DryRun)
        {
            status += "；只观察模式不会启动 ICE";
            if (now >= nextDryRunLogUtc)
            {
                log.Information("Dry run: eligible ICE gap {Seconds:F0}s ({Source})", remaining, source);
                nextDryRunLogUtc = now.AddMinutes(1);
            }
            return;
        }

        StartCycle(now, remaining, false);
    }

    private bool StartCycle(DateTime now, double remainingSeconds, bool asTest)
    {
        var originalFisherGearset = gearsets.CurrentGearsetId;
        if (originalFisherGearset is null) { Fail("无法读取当前捕鱼套装"); return false; }
        failedIceJobsThisCycle.Clear();
        rotatingIceJob = false;
        iceStopAfterCurrentOwned = false;
        repairIceGearAfterStop = false;
        if (!TrySelectNextIceJob(out var selectionError))
        {
            lastError = selectionError;
            status = selectionError;
            suppressAutoUntilUtc = now.AddMinutes(5);
            AddUiLog("职业", selectionError);
            return false;
        }
        fisherGearsetId = originalFisherGearset;
        windowStartUtc = asTest ? null : now.AddSeconds(remainingSeconds);
        cycleChecklistId = config.MissFisherChecklistId;
        cycleChecklistName = config.MissFisherChecklistName.Trim();
        cycleResumeKind = config.MissFisherResumeKind;
        iceOwned = false;
        testMode = asTest;
        testMissionObserved = false;
        observedMissionId = 0;
        cycleOutcomeMessage = string.Empty;
        actionSent = false;
        SaveCheckpoint();
        commands.ProcessCommand("/mf pause");
        AddUiLog("测试", asTest ? "开始完整流程测试，已请求暂停 MissFisher" : $"检测到 {Format(remainingSeconds)} 空档，开始调度周期");
        Transition(SchedulerState.PausingFisher, asTest ? "完整测试：正在暂停 MissFisher" : "正在暂停 MissFisher");
        return true;
    }

    private bool TrySelectNextIceJob(out string error)
    {
        error = string.Empty;
        if (!config.AutoSelectIceJob)
        {
            if (config.IceGearsetId < 0)
            {
                activeIceGearsetId = gearsets.CurrentGearsetId ?? -1;
                activeIceJobId = gearsets.CurrentJobId;
                activeIceJobName = "当前职业";
                if (activeIceGearsetId >= 0 && activeIceJobId is >= 8 and <= 18) return true;
                error = "当前职业不是可用于 ICE 的生产/采集职业";
                return false;
            }
            var selected = gearsets.GetIceGearsets().FirstOrDefault(x => x.GearsetId == config.IceGearsetId);
            if (selected.GearsetId != config.IceGearsetId || selected.JobId == 0)
            {
                error = "配置的 ICE 套装不存在或不是生产/采集职业";
                return false;
            }
            activeIceGearsetId = selected.GearsetId;
            activeIceJobId = selected.JobId;
            activeIceJobName = selected.Name;
            return true;
        }

        var excluded = config.ExcludedIceJobIds.ToHashSet();
        excluded.UnionWith(failedIceJobsThisCycle);
        var candidate = gearsets.GetIceJobCandidates(Math.Clamp(config.IceJobLevelCap, 1, 100), excluded,
            config.IncludeFisherInIceRotation, config.IceJobSelectionStrategy).FirstOrDefault();
        if (candidate.JobId == 0)
        {
            error = $"没有等级低于 {config.IceJobLevelCap}、未排除且具有有效套装的生产/采集职业";
            return false;
        }
        activeIceGearsetId = candidate.GearsetId;
        activeIceJobId = candidate.JobId;
        activeIceJobName = candidate.JobName;
        AddUiLog("职业", $"选择 {candidate.JobName} Lv.{candidate.Level}（套装 {candidate.GearsetName}）");
        return true;
    }

    private void TickPausing(DateTime now, MissFisherSnapshot fisher)
    {
        if (fisher.IsPaused || !fisher.IsRunning)
        {
            settleTerritoryId = clientState.TerritoryType;
            territoryStableSinceUtc = DateTime.MinValue;
            Transition(SchedulerState.WaitingForFisherTravel, "MissFisher 已暂停，等待遗留传送完成");
            return;
        }
        if (Elapsed(now) > TimeSpan.FromSeconds(20)) Fail("MissFisher 暂停超时");
    }

    private void TickWaitingForFisherTravel(DateTime now, MissFisherSnapshot fisher)
    {
        if (fisher.IsRunning && !fisher.IsPaused)
        {
            if (!actionSent)
            {
                commands.ProcessCommand("/mf pause");
                actionSent = true;
            }
            territoryStableSinceUtc = DateTime.MinValue;
            status = "MissFisher 意外恢复，正在重新暂停";
            return;
        }

        var currentTerritory = clientState.TerritoryType;
        var betweenAreas = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
        var busy = betweenAreas || condition[ConditionFlag.Casting] || condition[ConditionFlag.Occupied]
            || !playerState.IsLoaded;

        if (currentTerritory != settleTerritoryId)
        {
            log.Information("MissFisher post-pause travel changed territory: {OldTerritory} -> {NewTerritory}",
                settleTerritoryId, currentTerritory);
            settleTerritoryId = currentTerritory;
            territoryStableSinceUtc = DateTime.MinValue;
        }

        if (busy)
        {
            territoryStableSinceUtc = DateTime.MinValue;
            status = betweenAreas
                ? "MissFisher 已暂停；正在等待其遗留传送完成"
                : "MissFisher 已暂停；等待角色结束咏唱或占用状态";
            if (Elapsed(now) > TimeSpan.FromSeconds(60))
                Fail("暂停 MissFisher 后，角色传送或占用状态超过 60 秒仍未结束");
            return;
        }

        if (territoryStableSinceUtc == DateTime.MinValue)
            territoryStableSinceUtc = now;

        var graceRemaining = Math.Max(0, 12 - Elapsed(now).TotalSeconds);
        var stableRemaining = Math.Max(0, 3 - (now - territoryStableSinceUtc).TotalSeconds);
        if (graceRemaining > 0 || stableRemaining > 0)
        {
            status = $"MissFisher 已暂停；等待传送稳定 {Math.Ceiling(Math.Max(graceRemaining, stableRemaining))} 秒";
            return;
        }

        log.Information("MissFisher post-pause travel settled: territory={Territory}, elapsed={Elapsed:F1}s",
            currentTerritory, Elapsed(now).TotalSeconds);
        Transition(SchedulerState.TravellingToIce, "MissFisher 遗留传送已结束，准备进入 ICE 区域");
    }

    private void TickTravelling(DateTime now)
    {
        if (clientState.TerritoryType == config.IceTerritoryId)
        {
            Transition(SchedulerState.EquippingIceJob, "已进入目标宇宙探索区域，准备切换 ICE 职业");
            return;
        }
        if (config.UseBuiltInIceTravel)
        {
            if (!actionSent)
            {
                iceTravel.Reset();
                actionSent = true;
            }
            if (iceTravel.Tick(now, config, out var error))
            {
                Transition(SchedulerState.EquippingIceJob, "已进入目标宇宙探索区域，准备切换 ICE 职业");
                return;
            }
            status = iceTravel.Status;
            if (!string.IsNullOrWhiteSpace(error))
            {
                Fail(error);
                return;
            }
            if (Elapsed(now) > TimeSpan.FromSeconds(Math.Clamp(config.IceTravelTimeoutSeconds, 30, 600)))
                Fail($"未能在限定时间内进入目标 ICE 区域 {config.IceTerritoryId}");
            return;
        }
        if (!actionSent)
        {
            if (string.IsNullOrWhiteSpace(config.IceTravelCommand))
            {
                status = $"不在目标 ICE 区域 {config.IceTerritoryId}；请手动进入或配置传送/交互命令";
            }
            else
            {
                travelCommands = config.IceTravelCommand
                    .Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                travelCommandIndex = 0;
                nextTravelCommandUtc = now;
                status = $"准备执行 {travelCommands.Length} 个跨区步骤，目标区域 {config.IceTerritoryId}";
            }
            actionSent = true;
        }
        if (travelCommandIndex < travelCommands.Length && now >= nextTravelCommandUtc)
        {
            commands.ProcessCommand(travelCommands[travelCommandIndex]);
            travelCommandIndex++;
            nextTravelCommandUtc = now.AddSeconds(Math.Clamp(config.IceTravelStepDelaySeconds, 1, 30));
            status = $"已执行跨区步骤 {travelCommandIndex}/{travelCommands.Length}，等待入口交互或换图确认";
        }
        if (Elapsed(now) > TimeSpan.FromSeconds(Math.Clamp(config.IceTravelTimeoutSeconds, 30, 600)))
            Fail($"未能在限定时间内进入目标 ICE 区域 {config.IceTerritoryId}");
    }

    private void TickEquippingIce(DateTime now)
    {
        if (activeIceGearsetId < 0)
        {
            Transition(config.RepairIceGearBeforeStart ? SchedulerState.RepairingIceGear : SchedulerState.StartingIce,
                config.RepairIceGearBeforeStart ? "保持当前职业，准备检查装备耐久" : "保持当前职业，准备启动 ICE");
            return;
        }
        var selected = gearsets.GetIceGearsets().FirstOrDefault(x => x.GearsetId == activeIceGearsetId);
        if (selected.GearsetId == activeIceGearsetId && gearsets.CurrentJobId == activeIceJobId)
        {
            if (config.EquipRecommendedGearOnSwitch)
            {
                Transition(SchedulerState.OptimizingIceGear, $"已切换至 {activeIceJobName}，准备选择最强装备");
                return;
            }
            Transition(config.RepairIceGearBeforeStart ? SchedulerState.RepairingIceGear : SchedulerState.StartingIce,
                config.RepairIceGearBeforeStart
                    ? $"已切换至 {activeIceJobName}，准备检查装备耐久"
                    : $"已切换至 {activeIceJobName}，准备启动 ICE");
            return;
        }
        if (IsSafe() && gearsets.Equip(activeIceGearsetId))
        {
            status = $"正在切换 ICE 套装 {selected.Name}（{activeIceJobName}）";
            return;
        }
        if (Elapsed(now) > TimeSpan.FromSeconds(30)) Fail("无法切换到配置的 ICE 套装");
    }

    private void TickOptimizingIceGear(DateTime now)
    {
        if (activeIceGearsetId < 0 || gearsets.CurrentGearsetId != activeIceGearsetId
            || gearsets.CurrentJobId != activeIceJobId)
        {
            Transition(SchedulerState.EquippingIceJob, "职业或套装状态发生变化，正在重新切换 ICE 套装");
            return;
        }

        if (Elapsed(now) > TimeSpan.FromSeconds(30))
        {
            Fail($"无法为 {activeIceJobName} 应用最强装备");
            return;
        }
        if (!IsSafe() || now < nextIceGearActionUtc) return;

        switch (iceGearOptimizationStep)
        {
            case 0:
                if (!gearsets.SetupRecommendedGear(activeIceJobId)) return;
                iceGearOptimizationStep = 1;
                nextIceGearActionUtc = now.AddSeconds(1);
                status = $"正在计算 {activeIceJobName} 的最强装备";
                AddUiLog("装备", status);
                break;
            case 1:
                if (gearsets.RecommendedGearIsUpdating() || !gearsets.EquipRecommendedGear()) return;
                iceGearOptimizationStep = 2;
                nextIceGearActionUtc = now.AddSeconds(1.5);
                status = $"已为 {activeIceJobName} 选择最强装备，等待装备状态稳定";
                AddUiLog("装备", status);
                break;
            default:
                AddUiLog("装备", $"{activeIceJobName} 已穿上最强装备；未保存或覆盖套装 {activeIceGearsetId}");
                Transition(config.RepairIceGearBeforeStart ? SchedulerState.RepairingIceGear : SchedulerState.StartingIce,
                    config.RepairIceGearBeforeStart
                        ? $"{activeIceJobName} 已穿上最强装备，准备检查装备耐久"
                        : $"{activeIceJobName} 已穿上最强装备，准备启动 ICE");
                break;
        }
    }

    private void TickRepairingIceGear(DateTime now)
    {
        // The repair window itself sets Occupied, so this phase must not use
        // IsSafe(), which would deadlock immediately after opening the window.
        if (!playerState.IsLoaded || condition[ConditionFlag.InCombat]
            || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]
            || condition[ConditionFlag.Casting])
        {
            status = "等待角色可操作后修理装备";
            return;
        }
        if (equipmentRepair.Tick(now, config.IceGearRepairThresholdPercent,
                config.IceGearRepairTimeoutSeconds, out var repairStatus, out var error))
        {
            AddUiLog("修理", repairStatus);
            repairIceGearAfterStop = false;
            if (!testMode && windowStartUtc is not null
                && Remaining(now) <= config.RecoveryReserveMinutes * 60d + 60)
            {
                BeginRestore($"{repairStatus}；钓鱼窗口临近，正在恢复 MissFisher");
                return;
            }
            Transition(SchedulerState.StartingIce, $"{repairStatus}，准备启动 ICE");
            return;
        }
        status = repairStatus;
        if (!string.IsNullOrWhiteSpace(error)) Fail(error);
    }

    private void TickStartingIce(DateTime now)
    {
        if (!actionSent)
        {
            if (!ipc.TryPrepareArtisanForIce())
            {
                Fail("无法解除 Artisan 的外部停止状态，ICE 启动已取消");
                return;
            }
            artisanStopRequestOwned = false;
            if (testMode && !ipc.TrySetIceStopAfterCurrent(true))
            {
                Fail("无法设置 ICE“完成当前任务后停止”，完整测试已取消");
                return;
            }
            if (!ipc.TryEnableIce()) { Fail("ICE Enable IPC 调用失败"); return; }
            iceOwned = true;
            actionSent = true;
            SaveCheckpoint();
            status = testMode ? "完整测试：已请求启动 ICE，并设置完成一个任务后停止" : "已请求启动 ICE";
            AddUiLog("ICE", status);
        }
        if (ipc.TryGetIce(out var ice) && ice.IsRunning)
        {
            iceRunStartedUtc = now;
            observedMissionId = ice.CurrentMission;
            testMissionObserved = ice.CurrentMission != 0;
            Transition(SchedulerState.RunningIce, $"ICE 已启动：{ice.State}");
            return;
        }
        if (Elapsed(now) > TimeSpan.FromSeconds(20)) Fail("ICE 启动后未进入运行状态");
    }

    private void TickRunningIce(DateTime now)
    {
        if (!testMode)
        {
            var remaining = Remaining(now);
            if (!config.Enabled || remaining <= config.RecoveryReserveMinutes * 60d)
            {
                rotatingIceJob = false;
                repairIceGearAfterStop = false;
                ClearOwnedIceStopAfterCurrent();
                Transition(SchedulerState.StoppingIce, "接近钓鱼窗口，正在停止 ICE");
                return;
            }
        }
        if (!ipc.TryGetIce(out var ice))
        {
            status = testMode ? "完整测试：ICE 运行中，IPC 暂不可用" : $"ICE 运行中；IPC 暂不可用；窗口剩余 {Format(Remaining(now))}";
            return;
        }
        if (ice.CurrentMission != 0 && ice.CurrentMission != observedMissionId)
        {
            observedMissionId = ice.CurrentMission;
            testMissionObserved = true;
            AddUiLog("ICE", $"已开始任务 {ice.CurrentMission}");
        }
        if (!ice.IsRunning)
        {
            if (ipc.TryGetArtisan(out var artisan) && artisan.IsBusy)
            {
                cycleOutcomeMessage = testMode && testMissionObserved
                    ? $"完整测试成功：ICE 已完成任务 {observedMissionId}"
                    : "ICE 已停止，但 Artisan 制作仍在收尾";
                rotatingIceJob = !testMode && (rotatingIceJob
                    || (config.AutoSelectIceJob && activeIceJobId != 0
                        && gearsets.GetJobLevel(activeIceJobId) >= config.IceJobLevelCap));
                Transition(SchedulerState.StoppingIce, "ICE 状态已停止，正在终止 Artisan 制作并等待完全空闲");
                return;
            }
            var currentLevel = activeIceJobId == 0 ? 0 : gearsets.GetJobLevel(activeIceJobId);
            if (!testMode && (rotatingIceJob || (config.AutoSelectIceJob && currentLevel >= config.IceJobLevelCap)))
            {
                RotateIceJobOrRestore(now, $"{activeIceJobName} 已达到 Lv.{currentLevel}");
                return;
            }
            if (!testMode && repairIceGearAfterStop)
            {
                iceOwned = false;
                ClearOwnedIceStopAfterCurrent();
                Transition(SchedulerState.RepairingIceGear, "ICE 已停止，正在修理当前装备");
                return;
            }
            if (testMode && testMissionObserved)
            {
                ipc.TrySetIceStopAfterCurrent(false);
                cycleOutcomeMessage = $"完整测试成功：ICE 已完成任务 {observedMissionId}";
                BeginRestore("完整测试：ICE 已完成一个任务，正在恢复 MissFisher");
            }
            else if (!testMissionObserved && now - iceRunStartedUtc < TimeSpan.FromSeconds(15))
            {
                if (!testMode && config.AutoSelectIceJob)
                {
                    failedIceJobsThisCycle.Add(activeIceJobId);
                    RotateIceJobOrRestore(now, $"{activeIceJobName} 在 {GetRegionLabel(config.IceTerritoryId)} 未能领取练级任务", true);
                    return;
                }
                cycleOutcomeMessage = "ICE 启动后立即停止：ICE 当前模式、区域或职业没有可执行任务。请检查 ICE 的模式与任务条件。";
                lastError = cycleOutcomeMessage;
                suppressAutoUntilUtc = now.AddMinutes(5);
                if (testMode) ipc.TrySetIceStopAfterCurrent(false);
                AddUiLog("错误", cycleOutcomeMessage);
                BeginRestore("ICE 无可执行任务，正在恢复 MissFisher");
            }
            else
            {
                cycleOutcomeMessage = "ICE 已自行停止";
                if (testMode) ipc.TrySetIceStopAfterCurrent(false);
                BeginRestore("ICE 已自行停止，正在恢复 MissFisher");
            }
            return;
        }
        if (!testMode && config.AutoSelectIceJob && activeIceJobId != 0)
        {
            var level = gearsets.GetJobLevel(activeIceJobId);
            if (level >= config.IceJobLevelCap)
            {
                rotatingIceJob = true;
                if (ice.CurrentMission != 0)
                {
                    if (!iceStopAfterCurrentOwned)
                    {
                        if (ipc.TrySetIceStopAfterCurrent(true))
                        {
                            iceStopAfterCurrentOwned = true;
                            AddUiLog("职业", $"{activeIceJobName} 已达到 Lv.{level}，等待任务 {ice.CurrentMission} 完成后换职业");
                        }
                        else
                        {
                            Transition(SchedulerState.StoppingIce, $"{activeIceJobName} 已满级，无法设置任务后停止，正在直接停止 ICE");
                            return;
                        }
                    }
                    status = $"{activeIceJobName} 已达到 Lv.{level}；等待当前 ICE 任务 {ice.CurrentMission} 完成后切换职业";
                    return;
                }
                Transition(SchedulerState.StoppingIce, $"{activeIceJobName} 已达到 Lv.{level}，正在切换下一个职业");
                return;
            }
        }
        if (!testMode && config.RepairIceGearBeforeStart
            && EquipmentRepairHelper.TryGetLowestEquippedPercent(out var lowestDurability)
            && lowestDurability <= config.IceGearRepairThresholdPercent)
        {
            repairIceGearAfterStop = true;
            // A damaged-but-usable set can finish the current mission safely.
            // At 0% Artisan cannot progress, so waiting for mission completion
            // would deadlock; stop ICE immediately and repair first.
            if (ice.CurrentMission != 0 && lowestDurability > 0.01f)
            {
                if (!iceStopAfterCurrentOwned)
                {
                    if (!ipc.TrySetIceStopAfterCurrent(true))
                    {
                        Transition(SchedulerState.StoppingIce,
                            $"装备最低耐久 {lowestDurability:F0}%，无法等待任务结束，正在停止 ICE 修理");
                        return;
                    }
                    iceStopAfterCurrentOwned = true;
                    AddUiLog("修理", $"装备最低耐久 {lowestDurability:F0}%，等待任务 {ice.CurrentMission} 完成后修理");
                }
                status = $"装备最低耐久 {lowestDurability:F0}%；等待当前 ICE 任务 {ice.CurrentMission} 完成后修理";
                return;
            }
            Transition(SchedulerState.StoppingIce, $"装备最低耐久 {lowestDurability:F0}%，正在停止 ICE 修理");
            return;
        }
        if (testMode && !testMissionObserved && now - iceRunStartedUtc > TimeSpan.FromSeconds(Math.Clamp(config.TestMissionStartTimeoutSeconds, 15, 300)))
        {
            cycleOutcomeMessage = $"完整测试失败：ICE 已启动，但一直没有领取任务。请检查 ICE 的 Agenda、{GetRegionLabel(config.IceTerritoryId)} 区域和当前职业任务配置。";
            lastError = cycleOutcomeMessage;
            AddUiLog("错误", cycleOutcomeMessage);
            Transition(SchedulerState.StoppingIce, "完整测试：等待任务超时，正在停止 ICE");
            return;
        }
        status = testMode
            ? testMissionObserved
                ? $"完整测试：ICE 正在执行任务 {observedMissionId}（{ice.State}），完成后自动恢复捕鱼"
                : $"完整测试：ICE 已运行（{ice.State}），等待领取任务"
            : $"ICE 正在运行：{ice.State}；任务 {(ice.CurrentMission == 0 ? "尚未领取" : ice.CurrentMission)}；钓鱼窗口剩余 {Format(Remaining(now))}";
    }

    private void TickStoppingIce(DateTime now)
    {
        if (!actionSent || now >= nextIceStopRetryUtc)
        {
            var firstRequest = !actionSent;
            var iceStopSent = ipc.TryDisableIce();
            if (ipc.TryGetArtisan(out var artisanBeforeStop))
            {
                if (!artisanBeforeStop.StopRequested && ipc.TryStopArtisan())
                    artisanStopRequestOwned = true;
            }
            else if (!artisanStopRequestOwned && ipc.TryStopArtisan())
            {
                artisanStopRequestOwned = true;
            }
            actionSent = true;
            nextIceStopRetryUtc = now.AddSeconds(2);
            status = "已请求 ICE 与 Artisan 停止，等待制作完全结束";
            if (firstRequest)
                AddUiLog("停止", $"已发送 ICE 停止={iceStopSent}、Artisan 停止={artisanStopRequestOwned}");
        }

        var iceKnown = ipc.TryGetIce(out var ice);
        var artisanKnown = ipc.TryGetArtisan(out var artisan);
        var iceStopped = iceKnown && !ice.IsRunning;
        var activelyCrafting = condition[ConditionFlag.Crafting]
            || condition[ConditionFlag.ExecutingCraftingAction]
            || condition[ConditionFlag.PreparingToCraft];
        var staleArtisanBusy = artisanKnown && artisan.IsBusy && artisan.StopRequested
            && !activelyCrafting && Elapsed(now) >= TimeSpan.FromSeconds(3);
        var artisanStopped = artisanKnown && (!artisan.IsBusy || staleArtisanBusy);
        if (iceStopped && artisanStopped)
        {
            if (staleArtisanBusy && stopConfirmedSinceUtc is null)
                AddUiLog("停止", "Artisan 返回忙碌，但停止请求已生效且角色不在制作；按残留假忙处理");
            stopConfirmedSinceUtc ??= now;
            if (now - stopConfirmedSinceUtc < TimeSpan.FromSeconds(1.5))
            {
                status = "ICE 与 Artisan 已停止，正在确认状态稳定";
                return;
            }
            if (artisanStopRequestOwned)
            {
                // Leave Artisan idle but usable after this scheduler-owned stop.
                // Clearing the stop flag may resume the old mode, so the helper
                // immediately keeps Endurance disabled.
                if (!ipc.TryPrepareArtisanForIce())
                {
                    status = "ICE 与 Artisan 已停止，正在解除 Artisan 外部停止锁";
                    return;
                }
                artisanStopRequestOwned = false;
            }
            if (rotatingIceJob)
            {
                RotateIceJobOrRestore(now, $"{activeIceJobName} 已达到等级上限");
                return;
            }
            if (repairIceGearAfterStop)
            {
                iceOwned = false;
                ClearOwnedIceStopAfterCurrent();
                Transition(SchedulerState.RepairingIceGear, "ICE 与 Artisan 已停止，正在修理当前装备");
                return;
            }
            if (testMode) ipc.TrySetIceStopAfterCurrent(false);
            BeginRestore("ICE 与 Artisan 已完全停止，正在恢复捕鱼职业");
            return;
        }

        stopConfirmedSinceUtc = null;
        status = $"正在停止：ICE={(iceKnown ? ice.State : "IPC 不可用")}，Artisan={(artisanKnown ? artisan.IsBusy ? "仍在制作" : "已空闲" : "IPC 不可用")}";
        if (!stopTimeoutLogged && Elapsed(now) > TimeSpan.FromSeconds(config.IceStopTimeoutSeconds))
        {
            stopTimeoutLogged = true;
            lastError = "ICE/Artisan 停止等待超时；为避免与 MissFisher 冲突，将继续重试而不提前恢复捕鱼";
            AddUiLog("警告", lastError);
        }
    }

    private void RotateIceJobOrRestore(DateTime now, string reason, bool unavailableInRegion = false)
    {
        iceOwned = false;
        rotatingIceJob = false;
        repairIceGearAfterStop = false;
        ClearOwnedIceStopAfterCurrent();
        if (Remaining(now) <= config.RecoveryReserveMinutes * 60d + 60)
        {
            cycleOutcomeMessage = $"{reason}；钓鱼窗口临近，不再切换职业";
            BeginRestore("钓鱼窗口临近，正在恢复 MissFisher");
            return;
        }
        if (!TrySelectNextIceJob(out var error))
        {
            cycleOutcomeMessage = unavailableInRegion
                ? $"当前区域没有其他可用练级职业：{error}"
                : $"生产/采集职业轮换完成：{error}";
            lastError = cycleOutcomeMessage;
            suppressAutoUntilUtc = now.AddMinutes(30);
            AddUiLog("职业", cycleOutcomeMessage);
            BeginRestore("没有其他可用 ICE 练级职业，正在恢复 MissFisher");
            return;
        }
        observedMissionId = 0;
        testMissionObserved = false;
        AddUiLog("职业", $"{reason}；准备切换到 {activeIceJobName}");
        Transition(SchedulerState.EquippingIceJob, $"{reason}；准备切换到 {activeIceJobName}");
    }

    private void ClearOwnedIceStopAfterCurrent()
    {
        if (!iceStopAfterCurrentOwned) return;
        ipc.TrySetIceStopAfterCurrent(false);
        iceStopAfterCurrentOwned = false;
    }

    private void BeginRestore(string message)
    {
        iceOwned = false;
        rotatingIceJob = false;
        ClearOwnedIceStopAfterCurrent();
        Transition(SchedulerState.RestoringFisher, message);
    }

    private void TickRestoring(DateTime now)
    {
        if (gearsets.CurrentJobId == FisherJobId)
        {
            Transition(SchedulerState.ResumingFisher, "捕鱼职业已恢复");
            return;
        }
        if (fisherGearsetId is not null && IsSafe() && gearsets.Equip(fisherGearsetId.Value))
        {
            status = "已发送捕鱼套装切换请求";
            return;
        }
        if (Elapsed(now) > TimeSpan.FromSeconds(30)) Fail("无法恢复原捕鱼套装");
    }

    private void TickResuming(DateTime now, MissFisherSnapshot fisher)
    {
        if (!fisher.IsRunning)
        {
            if (Elapsed(now) < TimeSpan.FromSeconds(2)) return;
            if (!targetReader.TryStartResumeTarget(cycleResumeKind, cycleChecklistId, cycleChecklistName, out var error))
            {
                Fail($"无法重新启动 MissFisher“{cycleChecklistName}”：{error}");
                return;
            }
            Transition(SchedulerState.RestartingFisher, $"正在重新启动“{cycleChecklistName}”");
            return;
        }
        if (!fisher.IsPaused) { Complete($"已恢复 MissFisher“{cycleChecklistName}”"); return; }
        if (Elapsed(now) >= TimeSpan.FromSeconds(2) && !resumeSent)
        {
            commands.ProcessCommand("/mf pause");
            resumeSent = true;
            status = "正在继续 MissFisher";
        }
        if (Elapsed(now) > TimeSpan.FromSeconds(20)) Fail("MissFisher 继续运行超时");
    }

    private void TickRestarting(DateTime now, MissFisherSnapshot fisher)
    {
        if (fisher.IsRunning && !fisher.IsPaused)
        {
            var message = !string.IsNullOrWhiteSpace(cycleOutcomeMessage)
                ? $"{cycleOutcomeMessage}；已重新启动 MissFisher“{cycleChecklistName}”"
                : $"已重新启动 MissFisher“{cycleChecklistName}”";
            Complete(message);
            return;
        }
        status = $"等待 MissFisher 启动“{cycleChecklistName}”";
        if (Elapsed(now) > TimeSpan.FromSeconds(30)) Fail("无法确认 MissFisher 已重新启动");
    }

    private bool TryGetRemaining(DateTime now, MissFisherSnapshot fisher, out double seconds, out string source)
    {
        if (targetReader.TryGetCurrentTargetWindowStart(out var start))
        {
            seconds = Math.Max(0, (start.UtcDateTime - now).TotalSeconds);
            source = "当前目标";
            return true;
        }
        seconds = fisher.SecondsUntilNextWindow;
        source = "MissFisher IPC";
        return double.IsFinite(seconds) && seconds > 0;
    }

    private string DescribeIdle(MissFisherSnapshot fisher, bool valid, double remaining, string source)
    {
        if (!fisher.IsRunning) return "MissFisher 未运行";
        if (fisher.IsPaused) return "MissFisher 已暂停";
        if (fisher.IsWindowActive || fisher.IsInWindow || fisher.IsAutoPreparing) return "MissFisher 正在处理钓鱼窗口";
        if (!fisher.IsWaiting) return "等待 MissFisher 进入等待状态";
        if (!valid) return "等待有效的下一目标时间";
        return $"下一目标还有 {Format(remaining)}（{source}），暂不启动 ICE";
    }

    private bool IsSafe() => playerState.IsLoaded && !condition[ConditionFlag.InCombat]
        && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51]
        && !condition[ConditionFlag.Occupied] && !condition[ConditionFlag.Casting];

    private double Remaining(DateTime now) => Math.Max(0, (windowStartUtc.GetValueOrDefault(now) - now).TotalSeconds);
    private TimeSpan Elapsed(DateTime now) => now - stateSinceUtc;
    private static string Format(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)) is var t
        ? t.TotalHours >= 1 ? $"{(int)t.TotalHours}小时{t.Minutes}分" : $"{Math.Max(0, t.Minutes)}分{t.Seconds}秒" : string.Empty;

    private void Transition(SchedulerState next, string message)
    {
        state = next;
        stateSinceUtc = DateTime.UtcNow;
        actionSent = false;
        resumeSent = false;
        if (next == SchedulerState.StoppingIce)
        {
            nextIceStopRetryUtc = DateTime.MinValue;
            stopConfirmedSinceUtc = null;
            stopTimeoutLogged = false;
        }
        if (next == SchedulerState.OptimizingIceGear)
        {
            iceGearOptimizationStep = 0;
            nextIceGearActionUtc = DateTime.MinValue;
        }
        if (next == SchedulerState.RepairingIceGear)
            equipmentRepair.Reset();
        status = message;
        SaveCheckpoint();
        log.Information("State -> {State}: {Message}", next, message);
        AddUiLog("状态", $"{next}: {message}");
    }

    private void Complete(string message)
    {
        state = SchedulerState.Idle;
        status = message;
        if (string.IsNullOrWhiteSpace(cycleOutcomeMessage) || cycleOutcomeMessage.StartsWith("完整测试成功", StringComparison.Ordinal))
            lastError = string.Empty;
        windowStartUtc = null;
        fisherGearsetId = null;
        iceOwned = false;
        testMode = false;
        testMissionObserved = false;
        observedMissionId = 0;
        activeIceGearsetId = -1;
        activeIceJobId = 0;
        activeIceJobName = string.Empty;
        failedIceJobsThisCycle.Clear();
        rotatingIceJob = false;
        repairIceGearAfterStop = false;
        ClearOwnedIceStopAfterCurrent();
        cycleOutcomeMessage = string.Empty;
        config.Checkpoint = new CycleCheckpoint();
        Save();
        AddUiLog("完成", message);
    }

    private void Fail(string message)
    {
        lastError = message;
        log.Error("{Message}", message);
        AddUiLog("错误", message);
        if (testMode) ipc.TrySetIceStopAfterCurrent(false);
        ClearOwnedIceStopAfterCurrent();
        if (iceOwned) ipc.TryDisableIce();
        if (fisherGearsetId is not null)
        {
            iceOwned = false;
            Transition(SchedulerState.RestoringFisher, $"发生错误，正在恢复捕鱼：{message}");
            return;
        }
        state = SchedulerState.Faulted;
        status = message;
    }

    private void SaveCheckpoint()
    {
        config.Checkpoint.Active = state is not SchedulerState.Idle and not SchedulerState.Faulted;
        config.Checkpoint.Phase = state.ToString();
        config.Checkpoint.WindowStartUtc = windowStartUtc;
        config.Checkpoint.FisherGearsetId = fisherGearsetId;
        config.Checkpoint.IceOwned = iceOwned;
        config.Checkpoint.TestMode = testMode;
        config.Checkpoint.ActiveIceGearsetId = activeIceGearsetId;
        config.Checkpoint.ActiveIceJobId = activeIceJobId;
        config.Checkpoint.FailedIceJobIds = failedIceJobsThisCycle.ToList();
        config.Checkpoint.ChecklistId = cycleChecklistId;
        config.Checkpoint.ChecklistName = cycleChecklistName;
        config.Checkpoint.ResumeKind = cycleResumeKind;
        Save();
    }

    private void ReconcileCheckpoint()
    {
        var cp = config.Checkpoint;
        if (!cp.Active) return;
        windowStartUtc = cp.WindowStartUtc;
        fisherGearsetId = cp.FisherGearsetId;
        iceOwned = cp.IceOwned;
        testMode = cp.TestMode;
        activeIceGearsetId = cp.ActiveIceGearsetId;
        activeIceJobId = cp.ActiveIceJobId;
        failedIceJobsThisCycle.Clear();
        failedIceJobsThisCycle.UnionWith(cp.FailedIceJobIds ?? []);
        activeIceJobName = gearsets.GetIceJobs().FirstOrDefault(x => x.JobId == activeIceJobId).Name ?? string.Empty;
        cycleChecklistId = cp.ChecklistId;
        cycleChecklistName = cp.ChecklistName;
        cycleResumeKind = cp.ResumeKind;
        state = iceOwned ? SchedulerState.RunningIce : SchedulerState.RestoringFisher;
        status = "已恢复未完成周期，正在核对 ICE 与捕鱼状态";
        AddUiLog("恢复", status);
    }

    private void AddUiLog(string level, string message)
    {
        uiLogs.Enqueue($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        while (uiLogs.Count > 300) uiLogs.Dequeue();
    }

    private void StartFullTest()
    {
        if (state is not SchedulerState.Idle and not SchedulerState.Faulted)
        {
            lastError = "当前已有调度周期运行，不能开始测试";
            AddUiLog("错误", lastError);
            return;
        }
        if (!playerState.IsLoaded || playerState.ClassJob.RowId != FisherJobId)
        {
            lastError = "完整测试必须从已登录且当前为捕鱼人的状态开始";
            AddUiLog("错误", lastError);
            return;
        }
        if (!IsSafe())
        {
            lastError = "角色正在战斗、传送、咏唱或被界面占用，请空闲后再测试";
            AddUiLog("错误", lastError);
            return;
        }
        if (!ipc.TryGetFisher(out var fisher) || !fisher.IsRunning)
        {
            lastError = "完整测试前需要先启动要恢复的 MissFisher 目标";
            AddUiLog("错误", lastError);
            return;
        }
        if (!ipc.TryGetIce(out var ice) || ice.IsRunning)
        {
            lastError = ice.IsRunning ? "ICE 已在运行，请先停止后再测试" : "ICE IPC 不可用";
            AddUiLog("错误", lastError);
            return;
        }
        state = SchedulerState.Idle;
        lastError = string.Empty;
        suppressAutoUntilUtc = DateTime.MinValue;
        StartCycle(DateTime.UtcNow, 0, true);
    }

    private void Save() => pluginInterface.SavePluginConfig(config);
    private void Open() => windowOpen = true;

    private void OnCommand(string _, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "enable": config.Enabled = true; status = config.DryRun ? "调度已启用（只观察）" : "调度已启用"; Save(); break;
            case "disable": StopScheduler(false); break;
            case "abort": StopScheduler(true); break;
            case "reset":
                if (testMode) ipc.TrySetIceStopAfterCurrent(false);
                config.Checkpoint = new CycleCheckpoint();
                state = SchedulerState.Idle;
                testMode = false;
                lastError = string.Empty;
                status = "状态已重置";
                Save();
                AddUiLog("操作", status);
                break;
            case "status": log.Information("State={State}, Status={Status}, Error={Error}", state, status, lastError); windowOpen = true; break;
            default: windowOpen = true; break;
        }
    }

    private void StopScheduler(bool emergency)
    {
        config.Enabled = false;
        Save();
        AddUiLog("操作", emergency ? "用户请求停止并恢复" : "用户关闭调度");
        if (state is SchedulerState.Idle or SchedulerState.Faulted)
        {
            state = SchedulerState.Idle;
            status = "调度已停止";
            return;
        }
        cycleOutcomeMessage = emergency ? "调度已由用户停止" : cycleOutcomeMessage;
        rotatingIceJob = false;
        repairIceGearAfterStop = false;
        ClearOwnedIceStopAfterCurrent();
        if (state is SchedulerState.RunningIce or SchedulerState.StartingIce or SchedulerState.StoppingIce)
        {
            Transition(SchedulerState.StoppingIce, "调度已停止，正在停止 ICE");
            return;
        }
        if (iceOwned) ipc.TryDisableIce();
        if (fisherGearsetId is not null) BeginRestore("调度已停止，正在恢复捕鱼");
        else Complete("调度已停止");
    }

    private void Draw()
    {
        if (!windowOpen) return;
        ImGui.SetNextWindowSize(new Vector2(680, 560), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("MissFisher ICE Scheduler", ref windowOpen)) { ImGui.End(); return; }

        if (ImGui.BeginTabBar("MainTabs"))
        {
            if (ImGui.BeginTabItem("状态"))
            {
                DrawStatusTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("配置"))
            {
                DrawConfigTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("日志"))
            {
                DrawLogTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    private void DrawStatusTab()
    {
        ImGui.Spacing();
        if (ImGui.Button("启动调度", new Vector2(130, 36)))
        {
            config.Enabled = true;
            lastError = string.Empty;
            Save();
            status = config.DryRun ? "调度已启动（只观察模式）" : "调度已启动";
            AddUiLog("操作", status);
        }
        ImGui.SameLine();
        if (ImGui.Button("停止并恢复", new Vector2(130, 36))) StopScheduler(true);
        ImGui.SameLine();
        if (ImGui.Button("完整测试一次", new Vector2(150, 36))) StartFullTest();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted($"调度：{(config.Enabled ? (config.DryRun ? "已启动（只观察）" : "已启动") : "已停止")}");
        ImGui.TextUnformatted($"阶段：{state}{(testMode ? "（完整测试）" : string.Empty)}");
        ImGui.TextUnformatted($"目标区域：{GetRegionLabel(config.IceTerritoryId)}");
        ImGui.TextWrapped($"状态：{status}");
        if (!string.IsNullOrWhiteSpace(lastError)) ImGui.TextWrapped($"最近错误：{lastError}");
        if (ipc.TryGetIce(out var ice))
        {
            var mission = ice.CurrentMission == 0 ? "无" : ice.CurrentMission.ToString();
            ImGui.TextUnformatted($"ICE：{(ice.IsRunning ? ice.State : "空闲")}；当前任务：{mission}");
        }
        else ImGui.TextUnformatted("ICE：IPC 不可用");
        if (activeIceJobId != 0)
            ImGui.TextUnformatted($"ICE 职业：{activeIceJobName} Lv.{gearsets.GetJobLevel(activeIceJobId)} / {config.IceJobLevelCap}");
        if (ipc.TryGetFisher(out var fisher))
            ImGui.TextUnformatted($"MissFisher：{(fisher.IsRunning ? (fisher.IsPaused ? "已暂停" : "运行中") : "空闲")}");
        ImGui.TextWrapped($"MissFisher 适配：{targetReader.Status}");
        if (windowStartUtc is not null) ImGui.TextUnformatted($"冻结窗口剩余：{Format(Remaining(DateTime.UtcNow))}");
        if (suppressAutoUntilUtc > DateTime.UtcNow)
            ImGui.TextWrapped($"自动重试冷却至：{suppressAutoUntilUtc.ToLocalTime():HH:mm:ss}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped("“完整测试一次”会忽略钓鱼间隔阈值，完整执行：暂停当前 MissFisher → 进入目标区域 → ICE 完成一个任务 → 停止 ICE → 恢复原捕鱼套装和目标。测试前请先在 ICE 的 Agenda 中为目标区域与所选职业启用至少一个可执行任务。");
    }

    private void DrawConfigTab()
    {

        var enabled = config.Enabled;
        if (ImGui.Checkbox("启用调度", ref enabled)) { config.Enabled = enabled; Save(); }
        var dry = config.DryRun;
        if (ImGui.Checkbox("只观察", ref dry)) { config.DryRun = dry; Save(); }
        var threshold = config.WaitThresholdMinutes;
        if (ImGui.InputInt("等待阈值（分钟）", ref threshold)) { config.WaitThresholdMinutes = Math.Clamp(threshold, 10, 1440); Save(); }
        var run = config.MinimumIceMinutes;
        if (ImGui.InputInt("ICE 最短可运行时间（分钟）", ref run)) { config.MinimumIceMinutes = Math.Clamp(run, 5, 240); Save(); }
        var reserve = config.RecoveryReserveMinutes;
        if (ImGui.InputInt("恢复预留（分钟）", ref reserve)) { config.RecoveryReserveMinutes = Math.Clamp(reserve, 3, 60); Save(); }
        var testTimeout = config.TestMissionStartTimeoutSeconds;
        if (ImGui.InputInt("测试等待 ICE 领取任务（秒）", ref testTimeout)) { config.TestMissionStartTimeoutSeconds = Math.Clamp(testTimeout, 15, 300); Save(); }

        var territory = config.IceTerritoryId;
        var territoryLabel = GetRegionLabel(territory);
        if (ImGui.BeginCombo("ICE 目标区域", territoryLabel))
        {
            foreach (var option in new[]
            {
                (1237u, "憧憬湾（Sinus Ardorum）"),
                (1291u, "法恩娜（Phaenna）"),
                (1310u, "俄匊斯（Oizys）"),
                (1319u, "奥克塞西亚（Auxesia）"),
            })
                if (ImGui.Selectable(option.Item2, option.Item1 == territory)) { config.IceTerritoryId = option.Item1; Save(); }
            ImGui.EndCombo();
        }
        var autoJob = config.AutoSelectIceJob;
        if (ImGui.Checkbox("自动选择未满级生产/采集职业", ref autoJob)) { config.AutoSelectIceJob = autoJob; Save(); }
        var optimizeGear = config.EquipRecommendedGearOnSwitch;
        if (ImGui.Checkbox("切换职业后穿上最强装备（不保存套装）", ref optimizeGear))
        { config.EquipRecommendedGearOnSwitch = optimizeGear; Save(); }
        var repairGear = config.RepairIceGearBeforeStart;
        if (ImGui.Checkbox("启动 ICE 前修理当前装备", ref repairGear))
        { config.RepairIceGearBeforeStart = repairGear; Save(); }
        if (repairGear)
        {
            var repairThreshold = config.IceGearRepairThresholdPercent;
            if (ImGui.SliderInt("装备修理阈值", ref repairThreshold, 1, 99, "%d%%"))
            { config.IceGearRepairThresholdPercent = repairThreshold; Save(); }
            ImGui.TextWrapped("只修理当前已装备物品，需要足够的暗物质及对应生产职业修理等级。启动前会检查；ICE 长时间运行中达到阈值时，会完成当前任务后停止、修理并重启 ICE。");
        }
        if (autoJob)
        {
            var levelCap = config.IceJobLevelCap;
            if (ImGui.InputInt("ICE 职业等级上限", ref levelCap)) { config.IceJobLevelCap = Math.Clamp(levelCap, 1, 100); Save(); }
            var includeFisher = config.IncludeFisherInIceRotation;
            if (ImGui.Checkbox("职业轮换包含捕鱼人", ref includeFisher)) { config.IncludeFisherInIceRotation = includeFisher; Save(); }
            var strategyLabel = config.IceJobSelectionStrategy == IceJobSelectionStrategy.LowestLevel ? "最低等级优先" : "职业顺序";
            if (ImGui.BeginCombo("职业选择策略", strategyLabel))
            {
                if (ImGui.Selectable("最低等级优先", config.IceJobSelectionStrategy == IceJobSelectionStrategy.LowestLevel))
                { config.IceJobSelectionStrategy = IceJobSelectionStrategy.LowestLevel; Save(); }
                if (ImGui.Selectable("职业顺序", config.IceJobSelectionStrategy == IceJobSelectionStrategy.JobOrder))
                { config.IceJobSelectionStrategy = IceJobSelectionStrategy.JobOrder; Save(); }
                ImGui.EndCombo();
            }
            ImGui.TextUnformatted("排除职业：");
            if (ImGui.BeginChild("ExcludedIceJobs", new Vector2(0, 115), true))
            {
                foreach (var job in gearsets.GetIceJobs())
                {
                    var excluded = config.ExcludedIceJobIds.Contains(job.JobId);
                    if (ImGui.Checkbox($"{job.Name}  Lv.{job.Level}##icejob{job.JobId}", ref excluded))
                    {
                        if (excluded) config.ExcludedIceJobIds.Add(job.JobId);
                        else config.ExcludedIceJobIds.RemoveAll(x => x == job.JobId);
                        config.ExcludedIceJobIds = config.ExcludedIceJobIds.Distinct().Order().ToList();
                        Save();
                    }
                }
            }
            ImGui.EndChild();
            ImGui.TextWrapped("职业达到上限后会等待当前 ICE 任务完成，再切换下一个有效套装；当前区域无法领取任务的职业会在本周期自动跳过。");
        }
        else
        {
            var iceGearsets = gearsets.GetIceGearsets();
            var selectedGearset = iceGearsets.FirstOrDefault(x => x.GearsetId == config.IceGearsetId);
            var gearsetLabel = config.IceGearsetId < 0 ? "保持当前职业（捕鱼人）" : selectedGearset.Name ?? $"套装 {config.IceGearsetId}";
            if (ImGui.BeginCombo("ICE 使用套装", gearsetLabel))
            {
                if (ImGui.Selectable("保持当前职业（捕鱼人）", config.IceGearsetId < 0)) { config.IceGearsetId = -1; Save(); }
                foreach (var option in iceGearsets)
                    if (ImGui.Selectable($"{option.Name}（职业 {option.JobId}）", option.GearsetId == config.IceGearsetId))
                    { config.IceGearsetId = option.GearsetId; Save(); }
                ImGui.EndCombo();
            }
        }
        var travel = config.IceTravelCommand;
        var builtInTravel = config.UseBuiltInIceTravel;
        if (ImGui.Checkbox("使用内置最佳兔威洞入口流程", ref builtInTravel)) { config.UseBuiltInIceTravel = builtInTravel; Save(); }
        if (builtInTravel)
        {
            ImGui.TextWrapped("自动传送到最佳兔威洞，使用 vnavmesh 前往驾行威并确认进入。无需快捷传送面板。");
            ImGui.TextWrapped("与驾行威交互后会打开专用目的地界面；调度器会切换到所选的四个宇宙探索区域之一，确认名称后点击“移动”。");
        }
        else
        {
            if (ImGui.InputText("跨区步骤命令（用 || 分隔）", ref travel, 2048)) { config.IceTravelCommand = travel; Save(); }
            ImGui.TextWrapped("备用外部命令模式：按顺序填写传送、寻路和入口交互命令，调度器会逐步发送并确认目标区域。");
        }

        if (targetReader.TryGetResumeOptions(out var options))
        {
            var current = options.FirstOrDefault(x => x.Kind == config.MissFisherResumeKind && x.Key == config.MissFisherChecklistId);
            if (ImGui.BeginCombo("恢复目标", current?.Label ?? config.MissFisherChecklistName))
            {
                foreach (var option in options)
                    if (ImGui.Selectable(option.Label, option == current))
                    {
                        config.MissFisherResumeKind = option.Kind;
                        config.MissFisherChecklistId = option.Key;
                        config.MissFisherChecklistName = option.Name;
                        Save();
                    }
                ImGui.EndCombo();
            }
        }

    }

    private void DrawLogTab()
    {
        if (ImGui.Button("复制全部")) ImGui.SetClipboardText(string.Join(Environment.NewLine, uiLogs));
        ImGui.SameLine();
        if (ImGui.Button("清空")) uiLogs.Clear();
        ImGui.SameLine();
        ImGui.Checkbox("自动滚动", ref logAutoScroll);
        ImGui.Separator();
        if (ImGui.BeginChild("SchedulerLog", new Vector2(0, 0), true))
        {
            foreach (var entry in uiLogs) ImGui.TextUnformatted(entry);
            if (logAutoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
                ImGui.SetScrollHereY(1f);
        }
        ImGui.EndChild();
    }

    private static string GetRegionLabel(uint territoryId) => territoryId switch
    {
        1237 => "憧憬湾（Sinus Ardorum）",
        1291 => "法恩娜（Phaenna）",
        1310 => "俄匊斯（Oizys）",
        1319 => "奥克塞西亚（Auxesia）",
        _ => $"区域 {territoryId}",
    };
}
