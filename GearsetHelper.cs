using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace MissFisherIceScheduler;

internal sealed class GearsetHelper(IPlayerState playerState)
{
    public unsafe int? CurrentGearsetId
    {
        get
        {
            var module = RaptureGearsetModule.Instance();
            return module == null ? null : module->CurrentGearsetIndex;
        }
    }

    public uint CurrentJobId => playerState.IsLoaded ? playerState.ClassJob.RowId : 0;

    public unsafe bool Equip(int id)
    {
        var module = RaptureGearsetModule.Instance();
        return module != null && module->IsValidGearset(id) && module->EquipGearset(id) == 0;
    }

    public unsafe IReadOnlyList<GearsetOption> GetIceGearsets()
    {
        var result = new List<GearsetOption>();
        var module = RaptureGearsetModule.Instance();
        if (module == null) return result;
        for (var index = 0; index < module->NumGearsets; index++)
        {
            if (!module->IsValidGearset(index)) continue;
            var entry = module->GetGearset(index);
            if (entry == null || entry->ClassJob is < 8 or > 18) continue;
            result.Add(new(entry->Id, entry->ClassJob, entry->NameString));
        }
        return result.OrderBy(x => x.GearsetId).ToArray();
    }
}

internal readonly record struct GearsetOption(int GearsetId, uint JobId, string Name);
