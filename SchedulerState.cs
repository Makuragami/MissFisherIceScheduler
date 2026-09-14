namespace MissFisherIceScheduler;

internal enum SchedulerState
{
    Idle,
    PausingFisher,
    WaitingForFisherTravel,
    TravellingToIce,
    EquippingIceJob,
    OptimizingIceGear,
    RepairingIceGear,
    StartingIce,
    RunningIce,
    StoppingIce,
    RestoringFisher,
    ResumingFisher,
    RestartingFisher,
    Faulted,
}
