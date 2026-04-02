namespace ShadowCulling;

public partial class Plugin
{
    private void RegisterCommands()
    {
        ConsoleCommandsService.RegisterCommand(
            name: "shadowcullingdebugonce",
            help: "Performs a single debug culling operation",
            onExecute: (string[] args) =>
            {
                TryClearAll();
                PerformEntityCulling(debug: true);
            }
        );

        ConsoleCommandsService.RegisterCommand(
            name: "shadowcullingtoggle",
            help: "Toggles shadow culling on/off",
            onExecute: (string[] args) =>
            {
                CullingEnabled = !CullingEnabled;
            });

        ConsoleCommandsService.RegisterCommand(
            name: "shadowcullingdebugdraw",
            help: "Toggles debug drawing on/off",
            onExecute: (string[] args) =>
            {
                DebugDrawingEnabled = !DebugDrawingEnabled;
            });
    }
}