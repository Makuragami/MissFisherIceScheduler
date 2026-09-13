using Dalamud.Configuration;
using System.Numerics;

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
    public int TestMissionStartTimeoutSeconds { get; set; } = 60;
    public uint IceTerritoryId { get; set; } = 1319;
    public int IceGearsetId { get; set; } = -1;
    public bool UseBuiltInIceTravel { get; set; } = true;
    public uint IceEntranceAetheryteId { get; set; } = 175;
    public string IceEntranceAetheryteName { get; set; } = "最佳兔威洞";
    public Vector3 IceEntrancePosition { get; set; } = new(25.680908f, -137.41669f, -411.30695f);
    public string IceEntranceNpcName { get; set; } = "驾行威";
    public int IceEntranceOptionIndex { get; set; }
    public int PlanetMoveEventType { get; set; } = -1;
    public int PlanetMoveEventParam { get; set; } = -1;
    public uint PlanetMoveEventNodeId { get; set; }
    // Multiple external travel/interaction commands can be chained with "||".
    public string IceTravelCommand { get; set; } = string.Empty;
    public int IceTravelStepDelaySeconds { get; set; } = 4;
    public int IceTravelTimeoutSeconds { get; set; } = 180;
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
    public bool TestMode { get; set; }
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
