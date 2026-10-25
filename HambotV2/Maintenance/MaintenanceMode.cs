namespace HambotV2.Maintenance
{
    internal interface IMaintenanceMode
    {
        /// <summary>True while a backup restore is replacing the live database (FR-98): commands decline, scheduled deliveries defer.</summary>
        bool IsActive { get; }

        /// <summary>Activates maintenance mode (the restore command; FR-98).</summary>
        void Enter();

        /// <summary>Ends maintenance mode (the restore command; FR-98).</summary>
        void Exit();
    }

    /// <summary>
    /// The singleton maintenance-mode flag (FR-98), set around an in-bot backup restore. While
    /// active the command pipeline replies with a maintenance notice (except the restore command
    /// itself) and the scheduled delivery engine skips its passes entirely, so a restore can
    /// never exhaust a reminder's retry budget and mark it failed. MCP tools that touch database
    /// state defer as well once that surface exists (§3.11).
    /// </summary>
    internal sealed class MaintenanceMode : IMaintenanceMode
    {
        private int _active;

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public void Enter() => Volatile.Write(ref _active, 1);

        public void Exit() => Volatile.Write(ref _active, 0);
    }
}
