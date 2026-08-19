using Dalamud.Utility;
using System.Collections.Generic;

namespace ZodiacBuddy.Stages.Animus;

/// <summary>
///     Define the relic item Id and their names.
/// </summary>
public static class AnimusRelic
{
    /// <summary>
    ///     List of Animus Zodiac weapons.
    /// </summary>
    public static readonly Dictionary<uint, string> Items = new()
    {
        {7834, GetItemName(7834)}, // Curtana Animus
        {7835, GetItemName(7835)}, // Sphairai Animus
        {7836, GetItemName(7836)}, // Bravura Animus
        {7837, GetItemName(7837)}, // Gae Bolg Animus
        {7838, GetItemName(7838)}, // Artemis Bow Animus
        {7839, GetItemName(7839)}, // Thyrus Animus
        {7840, GetItemName(7840)}, // Stardust Rod Animus
        {7841, GetItemName(7841)}, // The Veil of Wiyu Animus
        {7842, GetItemName(7842)}, // Omnilex Animus
        {7843, GetItemName(7843)}, // Holy Shield Animus
        {9252, GetItemName(9252)}, // Yoshimitsu Animus
    };

    private static string GetItemName(uint itemId)
        => ItemUtil.GetItemName(itemId, false).ToString();
}
