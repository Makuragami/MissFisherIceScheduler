using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MissFisherIceScheduler;

internal sealed class EquipmentRepairHelper(IGameGui gameGui, ICondition condition)
{
    private DateTime startedUtc;
    private DateTime nextActionUtc;
    private bool repairRequested;

    public void Reset()
    {
        startedUtc = DateTime.MinValue;
        nextActionUtc = DateTime.MinValue;
        repairRequested = false;
    }

    public unsafe bool Tick(DateTime now, int thresholdPercent, int timeoutSeconds,
        out string status, out string error)
    {
        status = string.Empty;
        error = string.Empty;
        if (!TryGetLowestEquippedPercent(out var lowest))
        {
            error = "无法读取当前装备耐久";
            return false;
        }

        if (lowest > thresholdPercent)
        {
            CloseRepairWindow();
            status = repairRequested
                ? $"装备修理完成，最低耐久 {lowest:F0}%"
                : $"装备最低耐久 {lowest:F0}%，无需修理";
            return true;
        }

        startedUtc = startedUtc == DateTime.MinValue ? now : startedUtc;
        if (now - startedUtc > TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 20, 180)))
        {
            error = $"装备修理超时，最低耐久仍为 {lowest:F0}%；请检查暗物质数量和对应生产职业修理等级";
            return false;
        }

        status = $"正在修理装备，当前最低耐久 {lowest:F0}%";
        if (now < nextActionUtc) return false;

        var actionManager = ActionManager.Instance();
        if (condition[(ConditionFlag)4])
        {
            if (actionManager is not null)
                actionManager->UseAction(ActionType.GeneralAction, 9, 0xE0000000, 0, 0, 0, null);
            nextActionUtc = now.AddSeconds(1);
            status = "正在下坐骑以修理装备";
            return false;
        }

        var yesNo = (AtkUnitBase*)gameGui.GetAddonByName("SelectYesno", 1).Address;
        if (yesNo is not null && yesNo->IsVisible && yesNo->IsReady)
        {
            yesNo->FireCallbackInt(0);
            repairRequested = true;
            nextActionUtc = now.AddMilliseconds(500);
            return false;
        }

        var repair = (AtkUnitBase*)gameGui.GetAddonByName("Repair", 1).Address;
        if (repair is not null && repair->IsVisible && repair->IsReady)
        {
            if (!condition[(ConditionFlag)39])
            {
                // Callback 0 repairs currently equipped items; callback 1 repairs all gear.
                repair->FireCallbackInt(0);
                repairRequested = true;
            }
            nextActionUtc = now.AddSeconds(1);
            return false;
        }

        if (actionManager is not null)
            actionManager->UseAction(ActionType.GeneralAction, 6, 0xE0000000, 0, 0, 0, null);
        nextActionUtc = now.AddSeconds(1);
        return false;
    }

    private unsafe void CloseRepairWindow()
    {
        var repair = (AtkUnitBase*)gameGui.GetAddonByName("Repair", 1).Address;
        if (repair is not null && repair->IsVisible)
            repair->Close(true);
    }

    public static unsafe bool TryGetLowestEquippedPercent(out float percent)
    {
        percent = 0;
        var manager = InventoryManager.Instance();
        if (manager is null) return false;
        var container = manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (container is null || !container->IsLoaded) return false;

        var found = false;
        var lowest = 100f;
        for (var index = 0; index < container->Size; index++)
        {
            var item = container->GetInventorySlot(index);
            if (item is null || item->ItemId == 0) continue;
            found = true;
            lowest = Math.Min(lowest, item->Condition / 300f);
        }
        percent = lowest;
        return found;
    }
}
