namespace ManyMoreMobs
{
    /// <summary>
    /// The mod's operating mode, chosen in <see cref="ManyMoreMobsConfig"/>.
    /// <list type="bullet">
    /// <item><b>Default</b> — vanilla 200-NPC engine limit, with NO engine IL-patching. Maximum safety and
    /// mod compatibility; the spawn-rate, category-budget and event tuning still apply within 200.</item>
    /// <item><b>Expanded</b> — raises the engine cap to <see cref="ManyMoreMobsConfig.MaxNPCTotal"/> (~750) and
    /// enables the full bonus-zone experience (combat reach, event scaling, etc.). Single-player only for now.</item>
    /// </list>
    /// Changing this is <c>[ReloadRequired]</c>: the engine patches and array growth happen once at load.
    /// </summary>
    public enum NpcCapMode
    {
        Default,
        Expanded
    }
}
