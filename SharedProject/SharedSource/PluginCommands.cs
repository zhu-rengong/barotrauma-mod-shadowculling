namespace ShadowCulling;

public partial class Plugin
{
    private void RegisterCommands()
    {
        DebugConsole.RegisterCommand(
             command: "shadowcullingdebugonce",
            helpMessage: "Performs a single debug culling operation",
            flags: CommandFlags.DoNotRelayToServer,
            onCommandExecuted: (string[] args) =>
            {
                TryClearAll();
                PerformEntityCulling(debug: true);
            }
        );

        DebugConsole.RegisterCommand(
             command: "shadowcullingtoggle",
            helpMessage: "Toggles shadow culling on/off",
            flags: CommandFlags.DoNotRelayToServer,
            onCommandExecuted: (string[] args) =>
            {
                CullingEnabled = !CullingEnabled;
                if (!CullingEnabled)
                {
                    TryClearAll();
                }
            });

        DebugConsole.RegisterCommand(
             command: "shadowcullingdebugdraw",
            helpMessage: "Toggles debug drawing on/off",
            flags: CommandFlags.DoNotRelayToServer,
            onCommandExecuted: (string[] args) =>
            {
                DebugDrawingEnabled = !DebugDrawingEnabled;
            });
    }
}