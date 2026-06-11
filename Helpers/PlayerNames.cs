using System;

namespace DeathrollManager.Helpers;

/// <summary>
/// Lenient player-name comparison shared by game tracking and roll-off detection.
/// Chat always reports full "First Last" names, but hosts often enter first names
/// (or nicknames) into the bracket — so a first-token match counts as a match.
/// </summary>
public static class PlayerNames
{
    public static bool Match(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        var aFirst = a.TrimStart().Split(' ')[0];
        var bFirst = b.TrimStart().Split(' ')[0];
        return string.Equals(aFirst, bFirst, StringComparison.OrdinalIgnoreCase);
    }
}
