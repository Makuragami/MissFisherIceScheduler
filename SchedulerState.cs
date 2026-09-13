namespace MissFisherIceScheduler;

internal enum SchedulerState
{
    Idle,
    PausingFisher,
    WaitingForFisherTravel,
    TravellingToIce,
    EquippingIceJob,
    OptimizingIceGear,
    StartingIce,
    RunningIce,
    StoppingIce,
    RestoringFisher,
    ResumingFisher,
    RestartingFisher,
    Faulted,
}
