namespace HambotV2.Maintenance
{
    internal interface IMaintenanceMode
    {
        /// <summary>True while a backup restore is replacing the live database: commands decline, scheduled deliveries defer.</summary>
        bool IsActive { get; }

        /// <summary>Activates maintenance mode (the restore command).</summary>
        void Enter();

        /// <summary>Ends maintenance mode (the restore command).</summary>
        void Exit();
    }

    /// <summary>
    /// The singleton maintenance-mode flag, set around an in-bot backup restore. While
    /// active the command pipeline replies with a maintenance notice (except the restore command
    /// itself) and the scheduled delivery engine skips its passes entirely, so a restore can
    /// never exhaust a reminder's retry budget and mark it failed.
    /// </summary>
    internal sealed class MaintenanceMode : IMaintenanceMode
    {
        private int _active;

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public void Enter() => Volatile.Write(ref _active, 1);

        public void Exit() => Volatile.Write(ref _active, 0);
    }
}
