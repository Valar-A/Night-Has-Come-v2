using UnityEngine;

// Temporary data-only catalog for the lobby character picker.
//
// The project currently has one test character model, so the ten entries below use
// that same model with different colour tints. When the final character models and
// portraits arrive, this class can be replaced by ScriptableObject definitions
// without changing the networking contract: character IDs remain 0..9.
public static class CharacterSelectionCatalog
{
    public const int CharacterCount = 10;

    private static readonly string[] Names =
    {
        "Aiden", "Blake", "Carter", "Dylan", "Ethan",
        "Finn", "Grayson", "Hayes", "Isaac", "Jasper"
    };

    private static readonly Color[] Tints =
    {
        new Color32(236, 240, 241, 255),
        new Color32(231,  76,  60, 255),
        new Color32( 52, 152, 219, 255),
        new Color32( 46, 204, 113, 255),
        new Color32(241, 196,  15, 255),
        new Color32(155,  89, 182, 255),
        new Color32(230, 126,  34, 255),
        new Color32( 26, 188, 156, 255),
        new Color32(232,  67, 147, 255),
        new Color32(149, 165, 166, 255)
    };

    public static bool IsValid(int characterId)
    {
        return characterId >= 0 && characterId < CharacterCount;
    }

    public static string GetName(int characterId)
    {
        return IsValid(characterId) ? Names[characterId] : "No character selected";
    }

    public static Color GetTint(int characterId)
    {
        return IsValid(characterId) ? Tints[characterId] : Color.white;
    }
}
