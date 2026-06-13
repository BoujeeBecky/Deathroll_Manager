using System;

namespace DeathrollManager.Helpers;

/// <summary>
/// Plays FFXIV's own UI sound effects (same sounds as chat &lt;se.N&gt; tags,
/// which map to effect id 36 + N). Wrapped in try/catch — if the game function
/// signature ever shifts, sound silently degrades instead of crashing draws.
/// </summary>
internal static class SoundCue
{
    public const uint Death    = 44; // <se.8>  — heavy dramatic hit
    public const uint Champion = 52; // <se.16> — triumphant chime

    public static void Play(uint id)
    {
        try
        {
            unsafe
            {
                FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlaySoundEffect(id);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[DeathrollManager] Sound cue {id} failed: {ex.Message}");
        }
    }
}
