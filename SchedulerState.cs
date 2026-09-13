namespace MissFisherIceScheduler;

internal enum SchedulerState
{
    Idle,
    PausingFisher,
    WaitingForFisherTravel,
    TravellingToIce,
    EquippingIceJob,
    StartingIce,
    RunningIce,
    StoppingIce,
    RestoringFisher,
    ResumingFisher,
    RestartingFisher,
    Faulted,
}
