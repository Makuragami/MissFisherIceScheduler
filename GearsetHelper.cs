using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace MissFisherIceScheduler;

internal sealed class GearsetHelper(IPlayerState playerState, IDataManager dataManager)
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

    public unsafe bool SetupRecommendedGear(uint jobId)
    {
        var module = RecommendEquipModule.Instance();
        if (module == null || jobId is 0 or > byte.MaxValue) return false;
        module->SetupForClassJob((byte)jobId);
        return true;
    }

    public unsafe bool RecommendedGearIsUpdating()
    {
        var module = RecommendEquipModule.Instance();
        return module != null && module->IsUpdating;
    }

    public unsafe bool EquipRecommendedGear()
    {
        var module = RecommendEquipModule.Instance();
        if (module == null || module->IsUpdating) return false;
        module->EquipRecommendedGear();
        return true;
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

    public int GetJobLevel(uint jobId)
    {
        if (!playerState.IsLoaded || jobId is < 8 or > 18) return 0;
        return playerState.GetClassJobLevel(dataManager.GetExcelSheet<ClassJob>().GetRow(jobId));
    }

    public IReadOnlyList<IceJobCandidate> GetIceJobCandidates(int levelCap, ISet<uint> excludedJobs,
        bool includeFisher, IceJobSelectionStrategy strategy)
    {
        var jobs = dataManager.GetExcelSheet<ClassJob>();
        var candidates = GetIceGearsets()
            .Where(x => includeFisher || x.JobId != 18)
            .Where(x => !excludedJobs.Contains(x.JobId))
            .Select(x =>
            {
                var job = jobs.GetRow(x.JobId);
                return new IceJobCandidate(x.GearsetId, x.JobId, playerState.GetClassJobLevel(job),
                    x.Name, job.Name.ToString());
            })
            .Where(x => x.Level is > 0 && x.Level < levelCap)
            .GroupBy(x => x.JobId)
            .Select(group => group.OrderBy(x => x.GearsetId).First())
            .ToArray();

        return strategy == IceJobSelectionStrategy.LowestLevel
            ? candidates.OrderBy(x => x.Level).ThenBy(x => x.JobId).ToArray()
            : candidates.OrderBy(x => x.JobId).ToArray();
    }

    public IReadOnlyList<IceJobInfo> GetIceJobs()
    {
        var jobs = dataManager.GetExcelSheet<ClassJob>();
        return Enumerable.Range(8, 11)
            .Select(id => jobs.GetRow((uint)id))
            .Select(job => new IceJobInfo(job.RowId, job.Name.ToString(), playerState.IsLoaded
                ? playerState.GetClassJobLevel(job) : 0))
            .ToArray();
    }
}

internal readonly record struct GearsetOption(int GearsetId, uint JobId, string Name);
internal readonly record struct IceJobCandidate(int GearsetId, uint JobId, int Level, string GearsetName, string JobName);
internal readonly record struct IceJobInfo(uint JobId, string Name, int Level);
