namespace MissFisherIceScheduler;

internal enum SchedulerState
{
    Idle,
    PausingFisher,
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
