namespace TcgScanner;

/// <summary>A preconstructed starter deck: card numbers with copy counts (50 cards), plus its 10 Resource cards.</summary>
public record StarterDeck(string Code, string Name, Dictionary<string, int> Cards, string Note = "")
{
    public int Total => Cards.Values.Sum();
    public override string ToString() => $"{Code} {Name}";
}

/// <summary>
/// Gundam Card Game starter decklists. Bandai's card lists show which cards are in each box but not the copy counts,
/// so counts come from community decklists, each checked to total exactly 50.
/// </summary>
public static class StarterDecks
{
    static Dictionary<string, int> Parse(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split(':'))
            .ToDictionary(p => p[0].Trim(), p => int.Parse(p[1]));

    public static readonly List<StarterDeck> Gundam = new()
    {
        new("ST01", "Heroic Beginnings", Parse(
            "ST01-001:2,ST01-002:4,ST01-003:4,ST01-004:3,ST01-005:3,ST01-006:2,ST01-007:4,ST01-008:3," +
            "ST01-009:3,ST01-010:4,ST01-011:4,ST01-012:3,ST01-013:3,ST01-014:2,ST01-015:3,ST01-016:3")),
        new("ST02", "Wings of Advance", Parse(
            "ST02-001:2,ST02-002:4,ST02-003:4,ST02-004:3,ST02-005:3,ST02-006:2,ST02-007:4,ST02-008:3," +
            "ST02-009:3,ST02-010:4,ST02-011:4,ST02-012:3,ST02-013:3,ST02-014:2,ST02-015:3,ST02-016:3")),
        new("ST03", "Zeon's Rush", Parse(
            "ST03-001:2,ST03-002:4,ST03-003:4,ST03-004:3,ST03-005:3,ST03-006:2,ST03-007:3,ST03-008:4," +
            "ST03-009:3,ST03-010:4,ST03-011:4,ST03-012:3,ST03-013:3,ST03-014:2,ST03-015:3,ST03-016:3")),
        new("ST04", "SEED Strike", Parse(
            "ST04-001:2,ST04-002:4,ST04-003:4,ST04-004:3,ST04-005:3,ST04-006:2,ST04-007:4,ST04-008:3," +
            "ST04-009:3,ST04-010:4,ST04-011:4,ST04-012:3,ST04-013:3,ST04-014:2,ST04-015:3,ST04-016:3")),
        new("ST05", "Iron Bloom", Parse(
            "ST05-001:2,ST05-002:4,ST05-003:4,ST05-004:4,ST05-005:4,ST05-006:3,ST05-007:2,ST05-008:4," +
            "ST05-009:3,ST05-010:4,ST05-011:3,ST05-012:3,ST05-013:2,ST05-014:2,ST05-015:4,ST01-014:2")),
        new("ST06", "Clan Unity", Parse(
            "ST06-001:2,ST06-002:4,ST06-003:4,ST06-004:4,ST06-005:2,ST06-006:4,ST06-007:4,ST06-008:4," +
            "ST06-009:4,ST06-010:4,ST06-011:2,ST06-012:4,ST06-013:2,ST06-014:2,ST06-015:2,ST03-013:2")),
        new("ST07", "Celestial Drive", Parse(
            "ST07-001:2,ST07-002:4,ST07-003:4,ST07-004:3,ST07-005:2,ST07-006:4,ST07-007:3,ST07-008:4," +
            "ST07-009:3,ST07-010:3,ST07-011:3,ST07-012:3,ST07-013:2,ST07-014:4,ST07-015:4,ST05-014:2")),
        new("ST08", "Flash of Radiance", Parse(
            "ST08-001:2,ST08-002:4,ST08-003:3,ST08-004:4,ST08-005:3,ST08-006:2,ST08-007:4,ST08-008:4," +
            "ST08-009:3,ST08-010:4,ST08-011:4,ST08-012:3,ST08-013:3,ST08-014:3,ST08-015:2,ST02-014:2")),
        new("ST09", "Destiny Ignition", Parse(
            "ST09-001:2,ST09-002:2,ST09-003:2,ST09-005:4,ST09-006:4,ST09-007:4,ST09-008:4,ST09-009:2," +
            "ST09-010:4,GD01-054:4,GD01-049:3,ST04-007:4,ST04-011:4,GD01-111:3,GD02-110:2,ST04-016:2"),
            "This list comes from a single community source. Compare it with the cards in your box."),
        new("ST10", "Generation Pulse", Parse(
            "ST10-001:2,ST10-002:3,ST10-003:4,ST10-004:3,ST10-005:4,ST10-006:2,ST10-007:3,ST10-008:4," +
            "ST10-009:4,ST10-010:3,ST10-011:4,ST10-012:4,ST10-013:2,ST10-014:2,ST10-015:2,ST10-016:4")),
        new("ST11", "Aquatic Assault", Parse(
            "ST11-001:2,ST11-002:3,ST11-003:3,ST11-004:4,ST11-005:3,ST11-006:2,ST11-007:3,ST11-008:4," +
            "ST11-009:4,ST11-010:3,ST11-011:4,ST11-012:4,ST11-013:2,ST11-014:3,ST11-015:2,ST11-016:4")),
        new("ST12", "Raging Onslaught", Parse(
            "ST12-001:2,ST12-002:2,ST12-003:4,ST12-004:3,ST12-005:4,ST12-006:2,ST12-007:4,ST12-008:3," +
            "ST12-009:4,ST12-010:3,ST12-011:4,ST12-012:4,ST12-013:2,ST12-014:3,ST12-015:2,ST12-016:4")),
        new("ST13", "Silent Barrage", Parse(
            "ST13-001:2,ST13-002:3,ST13-003:4,ST13-004:3,ST13-005:3,ST13-006:2,ST13-007:2,ST13-008:3," +
            "ST13-009:4,ST13-010:4,ST13-011:4,ST13-012:4,ST13-013:3,ST13-014:2,ST13-015:3,ST13-016:4")),
        new("ST14", "Heavy Dominion", Parse(
            "ST14-001:2,ST14-002:3,ST14-003:3,ST14-004:4,ST14-005:2,ST14-006:2,ST14-007:4,ST14-008:3," +
            "ST14-009:4,ST14-010:3,ST14-011:4,ST14-012:4,ST14-013:3,ST14-014:3,ST14-015:2,ST14-016:4")),
        // ST15 Unicorn Awakening releases in April 2027; its list will be added once it's out.
    };
}
