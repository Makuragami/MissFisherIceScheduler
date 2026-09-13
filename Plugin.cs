using Dalamud.Bindings.ImGui;
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
    private readonly PluginIpc ipc;
    private readonly GearsetHelper gearsets;
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

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework,
        ICondition condition, IClientState clientState, IPlayerState playerState, IDataManager dataManager,
        IGameGui gameGui, IObjectTable objects, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.framework = framework;
        this.condition = condition;
        this.clientState = clientState;
        this.playerState = playerState;
        this.log = log;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Checkpoint ??= new CycleCheckpoint();
        ipc = new PluginIpc(pluginInterface, log);
        iceTravel = new IceTravelHelper(pluginInterface, clientState, condition, dataManager, gameGui, objects, log);
        gearsets = new GearsetHelper(playerState);
        commands.AddHandler(Command, new CommandInfo(OnCommand) { HelpMessage = "/mfice - 打开配置；enable | disable | abort | reset | status" });
        framework.Update += OnUpdate;
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += Open;
        pluginInterface.UiBuilder.OpenMainUi += Open;
        ReconcileCheckpoint();
    }

    public string Name => "MissFisher ICE Scheduler";

    public void Dispose()
    {
        if (iceOwned) ipc.TryDisableIce();
        framework.Update -= OnUpdate;
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= Open;
        pluginInterface.UiBuilder.OpenMainUi -= Open;
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
            case SchedulerState.TravellingToIce: TickTravelling(now); break;
            case SchedulerState.EquippingIceJob: TickEquippingIce(now); break;
            case SchedulerState.StartingIce: TickStartingIce(now); break;
            case SchedulerState.RunningIce: TickRunningIce(now); break;
            case SchedulerState.StoppingIce: TickStoppingIce(now); break;
            case SchedulerState.RestoringFisher: TickRestoring(now); break;
            case SchedulerState.ResumingFisher: TickResuming(now, fisher); break;
            case SchedulerState.RestartingFisher: TickRestarting(now, fisher); break;
        }
    }

    private void TickIdle(DateTime now, MissFisherSnapshot fisher)
    {
        if (!config.Enabled) { status = "调度已关闭"; return; }
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

        fisherGearsetId = gearsets.CurrentGearsetId;
        if (fisherGearsetId is null) { Fail("无法读取当前捕鱼套装"); return; }
        windowStartUtc = now.AddSeconds(remaining);
        cycleChecklistId = config.MissFisherChecklistId;
        cycleChecklistName = config.MissFisherChecklistName.Trim();
        cycleResumeKind = config.MissFisherResumeKind;
        iceOwned = false;
        actionSent = false;
        SaveCheckpoint();
        commands.ProcessCommand("/mf pause");
        Transition(SchedulerState.PausingFisher, "正在暂停 MissFisher");
    }

    private void TickPausing(DateTime now, MissFisherSnapshot fisher)
    {
        if (fisher.IsPaused || !fisher.IsRunning)
        {
            Transition(SchedulerState.TravellingToIce, "MissFisher 已暂停，准备进入 ICE 区域");
            return;
        }
        if (Elapsed(now) > TimeSpan.FromSeconds(20)) Fail("MissFisher 暂停超时");
    }

    private void TickTravelling(DateTime now)
    {
        if (IceTravelHelper.IsIceTerritory(clientState.TerritoryType))
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
        if (config.IceGearsetId < 0)
        {
            Transition(SchedulerState.StartingIce, "保持当前职业，准备启动 ICE");
            return;
        }
        var selected = gearsets.GetIceGearsets().FirstOrDefault(x => x.GearsetId == config.IceGearsetId);
        if (selected.GearsetId == config.IceGearsetId && gearsets.CurrentJobId == selected.JobId)
        {
            Transition(SchedulerState.StartingIce, $"已切换至 {selected.Name}，准备启动 ICE");
            return;
        }
        if (IsSafe() && gearsets.Equip(config.IceGearsetId))
        {
            status = $"正在切换 ICE 套装 {selected.Name}";
            return;
        }
        if (Elapsed(now) > TimeSpan.FromSeconds(30)) Fail("无法切换到配置的 ICE 套装");
    }

    private void TickStartingIce(DateTime now)
    {
        if (!actionSent)
        {
            if (!ipc.TryEnableIce()) { Fail("ICE Enable IPC 调用失败"); return; }
            iceOwned = true;
            actionSent = true;
            SaveCheckpoint();
            status = "已请求启动 ICE";
        }
        if (ipc.TryGetIce(out var ice) && ice.IsRunning)
        {
            Transition(SchedulerState.RunningIce, $"ICE 已启动：{ice.State}");
            return;
        }
        if (Elapsed(now) > TimeSpan.FromSeconds(20)) Fail("ICE 启动后未进入运行状态");
    }

    private void TickRunningIce(DateTime now)
    {
        var remaining = Remaining(now);
        if (!config.Enabled || remaining <= config.RecoveryReserveMinutes * 60d)
        {
            Transition(SchedulerState.StoppingIce, "接近钓鱼窗口，正在停止 ICE");
            return;
        }
        if (!ipc.TryGetIce(out var ice)) { status = $"ICE 运行中；IPC 暂不可用；窗口剩余 {Format(remaining)}"; return; }
        if (!ice.IsRunning)
        {
            BeginRestore("ICE 已自行停止，正在恢复 MissFisher");
            return;
        }
        status = $"ICE 正在运行：{ice.State}；钓鱼窗口剩余 {Format(remaining)}";
    }

    private void TickStoppingIce(DateTime now)
    {
        if (!actionSent)
        {
            if (iceOwned) ipc.TryDisableIce();
            actionSent = true;
            status = "已请求 ICE 停止";
        }
        if (ipc.TryGetIce(out var ice) && !ice.IsRunning)
        {
            BeginRestore("ICE 已停止，正在恢复捕鱼职业");
            return;
        }
        if (Elapsed(now) > TimeSpan.FromSeconds(config.IceStopTimeoutSeconds))
            BeginRestore("ICE 停止确认超时，继续恢复捕鱼职业");
    }

    private void BeginRestore(string message)
    {
        iceOwned = false;
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
        if (fisher.IsRunning && !fisher.IsPaused) { Complete($"已重新启动 MissFisher“{cycleChecklistName}”"); return; }
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
        status = message;
        SaveCheckpoint();
        log.Information("State -> {State}: {Message}", next, message);
    }

    private void Complete(string message)
    {
        state = SchedulerState.Idle;
        status = message;
        lastError = string.Empty;
        windowStartUtc = null;
        fisherGearsetId = null;
        iceOwned = false;
        config.Checkpoint = new CycleCheckpoint();
        Save();
    }

    private void Fail(string message)
    {
        lastError = message;
        log.Error("{Message}", message);
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
        cycleChecklistId = cp.ChecklistId;
        cycleChecklistName = cp.ChecklistName;
        cycleResumeKind = cp.ResumeKind;
        state = iceOwned ? SchedulerState.RunningIce : SchedulerState.RestoringFisher;
        status = "已恢复未完成周期，正在核对 ICE 与捕鱼状态";
    }

    private void Save() => pluginInterface.SavePluginConfig(config);
    private void Open() => windowOpen = true;

    private void OnCommand(string _, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "enable": config.Enabled = true; status = config.DryRun ? "调度已启用（只观察）" : "调度已启用"; Save(); break;
            case "disable": config.Enabled = false; Save(); if (state == SchedulerState.RunningIce) Transition(SchedulerState.StoppingIce, "调度已关闭，正在停止 ICE"); break;
            case "abort": config.Enabled = false; Save(); if (iceOwned) ipc.TryDisableIce(); BeginRestore("已紧急停止 ICE，正在恢复捕鱼"); break;
            case "reset": config.Checkpoint = new CycleCheckpoint(); state = SchedulerState.Idle; lastError = string.Empty; status = "状态已重置"; Save(); break;
            case "status": log.Information("State={State}, Status={Status}, Error={Error}", state, status, lastError); windowOpen = true; break;
            default: windowOpen = true; break;
        }
    }

    private void Draw()
    {
        if (!windowOpen) return;
        ImGui.SetNextWindowSize(new Vector2(590, 520), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("MissFisher ICE Scheduler", ref windowOpen)) { ImGui.End(); return; }

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

        var territory = config.IceTerritoryId;
        var territoryLabel = territory switch { 1237 => "Sinus Ardorum", 1291 => "Phaenna", 1310 => "Oizys", 1319 => "Auxesia", _ => $"区域 {territory}" };
        if (ImGui.BeginCombo("ICE 目标区域", territoryLabel))
        {
            foreach (var option in new[] { (1237u, "Sinus Ardorum"), (1291u, "Phaenna"), (1310u, "Oizys"), (1319u, "Auxesia") })
                if (ImGui.Selectable(option.Item2, option.Item1 == territory)) { config.IceTerritoryId = option.Item1; Save(); }
            ImGui.EndCombo();
        }
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
        var travel = config.IceTravelCommand;
        var builtInTravel = config.UseBuiltInIceTravel;
        if (ImGui.Checkbox("使用内置最佳兔威洞入口流程", ref builtInTravel)) { config.UseBuiltInIceTravel = builtInTravel; Save(); }
        if (builtInTravel)
        {
            ImGui.TextWrapped("自动传送到最佳兔威洞，使用 vnavmesh 前往驾行威并确认进入。无需快捷传送面板。");
            var optionIndex = config.IceEntranceOptionIndex;
            if (ImGui.InputInt("入口对话选项序号（从 0 开始）", ref optionIndex))
            { config.IceEntranceOptionIndex = Math.Clamp(optionIndex, 0, 10); Save(); }
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

        ImGui.Separator();
        ImGui.TextUnformatted($"状态：{state}");
        ImGui.TextWrapped(status);
        if (!string.IsNullOrWhiteSpace(lastError)) ImGui.TextWrapped($"最近错误：{lastError}");
        if (ipc.TryGetIce(out var ice)) ImGui.TextUnformatted($"ICE：{(ice.IsRunning ? ice.State : "空闲")}");
        else ImGui.TextUnformatted("ICE：IPC 不可用");
        ImGui.TextWrapped($"MissFisher 适配：{targetReader.Status}");
        if (windowStartUtc is not null) ImGui.TextUnformatted($"冻结窗口剩余：{Format(Remaining(DateTime.UtcNow))}");
        if (ImGui.Button("紧急停止 ICE 并恢复捕鱼")) OnCommand(Command, "abort");
        ImGui.End();
    }
}
