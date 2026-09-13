using Terraria.ModLoader;

namespace ManyMoreMobs
{
	public class ManyMoreMobs : Mod
	{
		public ManyMoreMobs()
		{
			// tModLoader constructs every Mod instance BEFORE it loads any mod's content, which is the only
			// moment at which we can raise Main.maxNPCs early enough for other mods to size their own
			// NPC-indexed arrays to fit it. See EarlyCapRaise for why that ordering is the whole point.
			//
			// Belt and braces around an already-defensive method: an exception escaping a Mod constructor
			// fails the entire mod load, and a cap raise is never worth that. Worst case we start at 200 and
			// MaxNpcCapRaise does what it always did.
			try { EarlyCapRaise.Apply(); }
			catch { /* no logger exists yet; MaxNpcCapRaise reports the resulting cap either way */ }
		}

		public override void Load()
		{
			// Set up our dedicated debug/state log files (separate from client.log) as early as possible.
			MmmLog.Init(this);
			// Mod.Logger did not exist yet during construction, so the early raise queued its output for here.
			EarlyCapRaise.FlushLog(this);
			// NOTE: config pages are listed in the in-game UI sorted by their DisplayName (see tML's
			// UIModConfig), NOT by load/registration order — so the page order is controlled purely by the
			// numeric "1. / 2. / ..." prefixes on each DisplayName in the en-US localization file.
		}

		public override void PostSetupContent()
		{
			// Every mod has finished loading by now, so this is the first moment we can see whether any of
			// them occupies the projectile-vs-NPC hit hooks. Kept out of MaxNpcCapRaise's own
			// PostSetupContent deliberately: that method has several early returns, and this answer needs to
			// be recorded whether or not the cap raise itself went ahead.
			try
			{
				HostileHitScan.DetectModHooks(this);
				Logger.Info(HostileHitScan.LoadSummary());
			}
			catch (System.Exception e)
			{
				// A detector that fails must fail CLOSED — leave the conflict list as-is (non-empty from the
				// partial scan, or empty meaning "safe") only if we know it is right. We cannot, so force the
				// full vanilla scan and say why.
				HostileHitScan.ForceUnsafe($"hook detection failed: {e.GetType().Name}");
				Logger.Warn("[MMM] Could not inspect other mods for projectile-vs-NPC hit hooks; keeping the " +
				            $"full (vanilla-cost) hit scan. {e}");
			}
		}
	}
}
