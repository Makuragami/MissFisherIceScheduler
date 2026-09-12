using Dalamud.Configuration;

namespace MissFisherIceScheduler;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public int WaitThresholdMinutes { get; set; } = 40;
    public int MinimumIceMinutes { get; set; } = 20;
    public int RecoveryReserveMinutes { get; set; } = 10;
    public int IceStopTimeoutSeconds { get; set; } = 60;
    public uint IceTerritoryId { get; set; } = 1319;
    public int IceGearsetId { get; set; } = -1;
    public string IceTravelCommand { get; set; } = string.Empty;
    public string MissFisherChecklistId { get; set; } = "ef950191-84e7-40ff-87ab-8d56f9d29572";
    public string MissFisherChecklistName { get; set; } = "新合集";
    public MissFisherResumeKind MissFisherResumeKind { get; set; } = MissFisherResumeKind.Collection;
    public CycleCheckpoint Checkpoint { get; set; } = new();
}

public sealed class CycleCheckpoint
{
    public bool Active { get; set; }
    public string Phase { get; set; } = string.Empty;
    public DateTime? WindowStartUtc { get; set; }
    public int? FisherGearsetId { get; set; }
    public bool IceOwned { get; set; }
    public string ChecklistId { get; set; } = string.Empty;
    public string ChecklistName { get; set; } = string.Empty;
    public MissFisherResumeKind ResumeKind { get; set; } = MissFisherResumeKind.Collection;
}

public enum MissFisherResumeKind
{
    FishLog,
    Album,
    Collection,
}
