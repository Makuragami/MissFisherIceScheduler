using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace MissFisherIceScheduler;

internal sealed class PluginIpc
{
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<bool> fisherRunning;
    private readonly ICallGateSubscriber<bool> fisherWindowActive;
    private readonly ICallGateSubscriber<bool> fisherPaused;
    private readonly ICallGateSubscriber<bool> fisherWaiting;
    private readonly ICallGateSubscriber<bool> fisherPreparing;
    private readonly ICallGateSubscriber<bool> fisherInWindow;
    private readonly ICallGateSubscriber<double> fisherNextWindow;
    private readonly ICallGateSubscriber<bool> iceRunning;
    private readonly ICallGateSubscriber<string> iceState;
    private readonly ICallGateSubscriber<uint> iceCurrentMission;
    private readonly ICallGateSubscriber<object> iceEnable;
    private readonly ICallGateSubscriber<object> iceDisable;
    private readonly ICallGateSubscriber<string, bool, object> iceChangeSetting;
    private readonly ICallGateSubscriber<bool> artisanBusy;
    private readonly ICallGateSubscriber<bool> artisanStopRequest;
    private readonly ICallGateSubscriber<bool, object> artisanSetStopRequest;
    private readonly ICallGateSubscriber<bool, object> artisanSetEndurance;
    private DateTime nextWarningUtc;

    public PluginIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;
        fisherRunning = pi.GetIpcSubscriber<bool>("MissFisher.Manager.IsRunning");
        fisherWindowActive = pi.GetIpcSubscriber<bool>("MissFisher.Manager.IsWindowActive");
        fisherPaused = pi.GetIpcSubscriber<bool>("MissFisher.Manager.AngleWindowState.IsPaused");
        fisherWaiting = pi.GetIpcSubscriber<bool>("MissFisher.Manager.AngleWindowState.IsWaiting");
        fisherPreparing = pi.GetIpcSubscriber<bool>("MissFisher.Manager.AngleWindowState.IsAutoPreparing");
        fisherInWindow = pi.GetIpcSubscriber<bool>("MissFisher.Manager.AngleWindowState.IsInWindow");
        fisherNextWindow = pi.GetIpcSubscriber<double>("MissFisher.Manager.SecondsUntilNextWindow");
        iceRunning = pi.GetIpcSubscriber<bool>("ICE.IsRunning");
        iceState = pi.GetIpcSubscriber<string>("ICE.CurrentState");
        iceCurrentMission = pi.GetIpcSubscriber<uint>("ICE.CurrentMission");
        iceEnable = pi.GetIpcSubscriber<object>("ICE.Enable");
        iceDisable = pi.GetIpcSubscriber<object>("ICE.Disable");
        iceChangeSetting = pi.GetIpcSubscriber<string, bool, object>("ICE.ChangeSetting");
        artisanBusy = pi.GetIpcSubscriber<bool>("Artisan.IsBusy");
        artisanStopRequest = pi.GetIpcSubscriber<bool>("Artisan.GetStopRequest");
        artisanSetStopRequest = pi.GetIpcSubscriber<bool, object>("Artisan.SetStopRequest");
        artisanSetEndurance = pi.GetIpcSubscriber<bool, object>("Artisan.SetEnduranceStatus");
    }

    public bool TryGetFisher(out MissFisherSnapshot value)
    {
        try
        {
            value = new(fisherRunning.InvokeFunc(), fisherWindowActive.InvokeFunc(), fisherPaused.InvokeFunc(),
                fisherWaiting.InvokeFunc(), fisherPreparing.InvokeFunc(), fisherInWindow.InvokeFunc(), fisherNextWindow.InvokeFunc());
            return true;
        }
        catch (Exception ex) { Warn(ex, "MissFisher IPC unavailable"); value = default; return false; }
    }

    public bool TryGetIce(out IceSnapshot value)
    {
        try { value = new(iceRunning.InvokeFunc(), iceState.InvokeFunc(), iceCurrentMission.InvokeFunc()); return true; }
        catch (Exception ex) { Warn(ex, "ICE IPC unavailable"); value = default; return false; }
    }

    public bool TryEnableIce()
    {
        try { iceEnable.InvokeAction(); return true; }
        catch (Exception ex) { log.Error(ex, "ICE Enable IPC failed"); return false; }
    }

    public bool TryDisableIce()
    {
        try { iceDisable.InvokeAction(); return true; }
        catch (Exception ex) { log.Error(ex, "ICE Disable IPC failed"); return false; }
    }

    public bool TrySetIceStopAfterCurrent(bool enabled)
    {
        try { iceChangeSetting.InvokeAction("StopAfterCurrent", enabled); return true; }
        catch (Exception ex) { log.Error(ex, "ICE ChangeSetting IPC failed"); return false; }
    }

    public bool TryGetArtisan(out ArtisanSnapshot value)
    {
        try
        {
            value = new(artisanBusy.InvokeFunc(), artisanStopRequest.InvokeFunc());
            return true;
        }
        catch (Exception ex) { Warn(ex, "Artisan IPC unavailable"); value = default; return false; }
    }

    public bool TryStopArtisan()
    {
        try { artisanSetStopRequest.InvokeAction(true); return true; }
        catch (Exception ex) { log.Error(ex, "Artisan stop IPC failed"); return false; }
    }

    public bool TryPrepareArtisanForIce()
    {
        try
        {
            // Clearing the stop request may resume Artisan's old Endurance mode.
            // Disable it immediately; ICE will enable it with the new recipe.
            artisanSetStopRequest.InvokeAction(false);
            artisanSetEndurance.InvokeAction(false);
            return true;
        }
        catch (Exception ex) { log.Error(ex, "Artisan prepare IPC failed"); return false; }
    }

    private void Warn(Exception ex, string message)
    {
        if (DateTime.UtcNow < nextWarningUtc) return;
        log.Warning(ex, "{Message}; further warnings throttled for 30 seconds", message);
        nextWarningUtc = DateTime.UtcNow.AddSeconds(30);
    }
}

internal readonly record struct MissFisherSnapshot(bool IsRunning, bool IsWindowActive, bool IsPaused,
    bool IsWaiting, bool IsAutoPreparing, bool IsInWindow, double SecondsUntilNextWindow);
internal readonly record struct IceSnapshot(bool IsRunning, string State, uint CurrentMission);
internal readonly record struct ArtisanSnapshot(bool IsBusy, bool StopRequested);
