using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace ManyMoreMobs
{
    /// <summary>
    /// Auto-diagnostics at the crash-prone moments. When <see cref="ManyMoreMobsConfig.DebugMode"/> is on,
    /// runs a full validation right as the world is saved (where high-slot NPCs previously crashed the save)
    /// and writes it to the state log, so anomalies are captured proactively instead of via a crash dump.
    /// </summary>
    public class MmmDebugSystem : ModSystem
    {
        public override void SaveWorldData(TagCompound tag)
        {
            if (ModContent.GetInstance<ManyMoreMobsConfig>()?.DebugMode != true)
                return;

            string report = EngineDiagnostics.BuildValidationReport(out int anomalies);
            MmmLog.Dump($"pre-save validation ({anomalies} anomalies)", report);
        }

        // Required: SaveWorldData and LoadWorldData must be overridden together. We persist nothing.
        public override void LoadWorldData(TagCompound tag) { }
    }
}
