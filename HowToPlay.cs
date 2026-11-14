namespace TcgScanner;

/// <summary>A beginner's guide to one game: plain-language summaries, with a link to the official rules.</summary>
public record RulesGuide(string Title, string Intro, string OfficialUrl, string OfficialLabel, (string Heading, string[] Lines)[] Sections);

/// <summary>
/// How-to-play guides for each game. Lines starting with "• " are bullets; text between ** is bold.
/// These are summaries for learning; the official rules (linked) are the final word.
/// </summary>
public static class HowToPlay
{
    public static RulesGuide For(Game g) => g switch
    {
        Game.Magic => Magic,
        Game.Pokemon => Pokemon,
        Game.YuGiOh => YuGiOh,
        Game.Cyberpunk => Cyberpunk,
        _ => Gundam,
    };

    static readonly RulesGuide Gundam = new(
        "Gundam Card Game",
        "Command mobile suits and their pilots from across the Gundam series. Break through your opponent's shields and land the final hit.",
        "https://www.gundam-gcg.com/en/welcome/playguide.php", "Official play guide (Bandai)",
        new[]
        {
            ("How to win", new[]
            {
                "• Deal **battle damage** to your opponent when they have **no cards left in their shield area**.",
                "• Or your opponent has no cards left in their deck.",
            }),
            ("What you need", new[]
            {
                "• A **50-card deck** of Units, Pilots, Commands and Bases, using **1 or 2 colors**, with at most **4 copies** of the same card number.",
                "• A separate **10-card Resource Deck**.",
                "• Token cards (EX Base and EX Resource) and something to track damage, such as dice.",
            }),
            ("Setting up", new[]
            {
                "• Decide who goes first (for example rock paper scissors). The winner picks before looking at their hand.",
                "• Shuffle and draw **5 cards**. Each player may redraw once: put the whole hand on the bottom of the deck, draw 5 new cards, then shuffle.",
                "• Put the top **6 cards** of your deck face down as your **shields**.",
                "• Each player places an **EX Base** (0 AP, 3 HP) in their shield area. The player going second also gets **1 EX Resource** to make up for going second.",
            }),
            ("Card types", new[]
            {
                "• **Unit**: mobile suits that fight. Up to 6 in your battle area. They can't attack the turn they're deployed.",
                "• **Pilot**: placed under a Unit (one per Unit). The Unit gains the Pilot's AP, HP and ability. If the Pilot matches the Unit's **Link** requirement, it becomes a **Link Unit** and can attack right away.",
                "• **Command**: a one-time effect. Some can be used during your opponent's turn, and some can be played as a Pilot instead.",
                "• **Base**: goes in your shield area to protect your shields. Only one Base at a time.",
                "• **Resource**: from your Resource Deck. Resources pay for your cards.",
            }),
            ("Playing cards", new[]
            {
                "• You can play a card when your number of Resources is **at least its Lv.** (level).",
                "• To pay, **rest** (turn sideways) Resources equal to its **cost**. For example, a Lv.4, cost-2 card needs 4 Resources in play and rests 2 of them.",
            }),
            ("Your turn, phase by phase", new[]
            {
                "• **Start phase**: set all your rested cards active (upright) again.",
                "• **Draw phase**: draw 1 card.",
                "• **Resource phase**: put the top card of your Resource Deck into your Resource Area.",
                "• **Main phase**: play cards, use 【Activate･Main】 abilities, and attack with Units, in any order.",
                "• **End phase**: if you have 11 or more cards in hand, discard down to 10. Then your opponent's turn begins.",
            }),
            ("Attacking", new[]
            {
                "• Rest the attacking Unit and attack your opponent or one of their rested Units. Your opponent may block with an active Unit that has **<Blocker>**.",
                "• **Unit vs Unit**: each deals damage equal to its **AP** to the other at the same time. A Unit is destroyed when its damage reaches its **HP**. A Unit with <First Strike> hits first.",
                "• **Attacking the player**: the attack hits their **Base** first. With no Base, it destroys their **top shield**. With no shields, the player takes the hit, and that wins the game.",
                "• A destroyed shield is revealed. If it has a **【Burst】** effect, its owner may use it for free before it goes to the trash.",
            }),
            ("Common keywords", new[]
            {
                "• **<Blocker>**: rest this Unit to redirect an enemy attack to it.",
                "• **<Repair>**: at the end of your turn, this Unit recovers HP.",
                "• **<Breach>**: when this Unit's attack destroys an enemy Unit, it also deals damage to the shield area.",
                "• **<First Strike>**: deals its battle damage before the enemy Unit.",
                "• **【Deploy】**, **【When Paired】**, **【Destroyed】**: effects that happen at those moments.",
            }),
            ("How the app helps", new[]
            {
                "• The optimizer picks your best 2 colors and builds a 50-card deck of about 28 Units, 10 Pilots, 10 Commands and 2 Bases, plus the 10-card Resource Deck.",
                "• It favors Units whose Link Pilots you own, so more of your Units can attack the turn they arrive.",
                "• Analyze checks the 50 + 10 deck sizes, the 2-color limit and the 4-copy limit, which counts alternate arts by their shared card number.",
            }),
        });

    static readonly RulesGuide Magic = new(
        "Magic: The Gathering",
        "Two players are rival wizards. You cast spells and summon creatures using mana from your lands, and try to defeat your opponent before they defeat you.",
        "https://magic.wizards.com/en/how-to-play", "Official Magic rules (Wizards of the Coast)",
        new[]
        {
            ("How to win", new[]
            {
                "• Bring your opponent from **20 life** to 0.",
                "• Or your opponent has to draw a card from an empty deck.",
                "• Or your opponent gets **10 poison counters**.",
                "• In **Commander**, players start at 40 life, and 21 combat damage from a single commander also wins.",
            }),
            ("Setting up", new[]
            {
                "• Each player shuffles their deck (60 cards or more; Commander is exactly 100) and starts at 20 life.",
                "• Draw **7 cards**. If you don't like your hand, you can mulligan: shuffle it back, draw 7 again, then put one card on the bottom of your deck for each mulligan you've taken.",
                "• Decide who goes first. The first player skips their draw on turn one.",
            }),
            ("Card types", new[]
            {
                "• **Land**: play one per turn. Tap it (turn it sideways) to make mana.",
                "• **Creature**: fights for you. A creature can't attack or use tap abilities the turn it arrives (\"summoning sickness\") unless it has **haste**.",
                "• **Instant**: a one-time effect you can cast at almost any time, even on your opponent's turn.",
                "• **Sorcery**: a one-time effect you can cast only during your own main phase.",
                "• **Artifact** and **Enchantment**: stay on the battlefield and give lasting effects.",
                "• **Planeswalker**: a powerful ally with loyalty abilities; use one ability per turn. Opponents can attack it.",
            }),
            ("Paying for spells", new[]
            {
                "• The cost is in the top right. A number means mana of any color; a colored symbol needs mana of that color.",
                "• The total is the card's **mana value**. The app's mana curve chart counts cards by mana value.",
            }),
            ("Your turn, step by step", new[]
            {
                "• **Beginning**: untap all your cards, handle \"at the beginning of your upkeep\" effects, then draw a card.",
                "• **First main phase**: play a land, cast creatures, sorceries and other spells.",
                "• **Combat**: choose attacking creatures (they tap). Your opponent chooses blockers. Then damage happens.",
                "• **Second main phase**: play more spells or your land if you haven't yet.",
                "• **End**: \"at the end of turn\" effects happen; if you have more than 7 cards, discard down to 7. Damage on creatures wears off.",
            }),
            ("Combat", new[]
            {
                "• Each attacking creature can be blocked by one or more of the defender's untapped creatures.",
                "• Creatures deal damage equal to their power (first number) at the same time. A creature dies when the damage it takes reaches its toughness (second number).",
                "• Unblocked attackers deal their damage to the player (or to a planeswalker they attacked).",
            }),
            ("The stack", new[]
            {
                "• When a spell or ability is cast, both players can respond. Responses resolve first: last in, first out.",
            }),
            ("Common keywords", new[]
            {
                "• **Flying**: can only be blocked by creatures with flying or reach.",
                "• **Trample**: extra damage beyond what kills the blocker goes through to the player.",
                "• **Deathtouch**: any damage it deals destroys a creature.",
                "• **Lifelink**: damage it deals also gains you that much life.",
                "• **First strike**: deals combat damage before creatures without it.",
                "• **Vigilance**: attacking doesn't tap it, so it can still block.",
            }),
            ("How the app helps", new[]
            {
                "• A typical 60-card deck runs about 24 lands; the optimizer sets the exact number from your deck's average mana value.",
                "• Analyze warns when too many spells are expensive, when a color lacks enough lands, and when a card is banned in your format.",
            }),
        });

    static readonly RulesGuide Pokemon = new(
        "Pokémon TCG",
        "You are a Pokémon Trainer. Your Pokémon battle your opponent's, powered by Energy cards and helped by Trainer cards.",
        "https://www.pokemon.com/us/pokemon-tcg/rules", "Official Pokémon TCG rulebook (Pokemon.com)",
        new[]
        {
            ("How to win", new[]
            {
                "• Take all **6 of your Prize cards**. You take Prize cards by Knocking Out your opponent's Pokémon: usually 1 each, 2 for Pokémon ex and V, and 3 for VMAX.",
                "• Or your opponent has no Pokémon left in play.",
                "• Or your opponent can't draw a card at the start of their turn.",
            }),
            ("Setting up", new[]
            {
                "• Shuffle your 60-card deck and draw **7 cards**.",
                "• You need a **Basic Pokémon** in your hand. If you have none, show your hand, shuffle it back and draw 7 again; your opponent may draw an extra card for each time you did this.",
                "• Put one Basic Pokémon face down as your **Active Pokémon**, and up to 5 more on your **Bench**.",
                "• Set aside the top **6 cards** of your deck face down as your Prize cards.",
                "• Flip a coin to see who goes first, then turn your Pokémon face up. The first player can't attack or play a Supporter on their first turn.",
            }),
            ("Card types", new[]
            {
                "• **Pokémon**: Basic Pokémon can be played directly. **Stage 1** evolves from a Basic, and **Stage 2** evolves from a Stage 1.",
                "• **Trainer** cards: **Items** (play as many as you like), **Supporters** (one per turn), **Stadiums** (one in play, shared by both players) and **Pokémon Tools** (attach to a Pokémon).",
                "• **Energy**: attach to Pokémon to pay for attacks and retreat. Basic Energy has no copy limit in your deck.",
            }),
            ("Your turn, step by step", new[]
            {
                "• **Draw** a card.",
                "• Then, in any order:",
                "• Put Basic Pokémon onto your Bench (5 at most).",
                "• **Evolve** Pokémon by placing the evolution on top. You can't evolve a Pokémon the turn it was played or on your first turn.",
                "• Attach **one Energy** card per turn.",
                "• Play Trainer cards (any Items, one Supporter, one Stadium).",
                "• **Retreat** your Active Pokémon once by discarding Energy equal to its retreat cost, then swap in a Benched Pokémon.",
                "• Use Abilities.",
                "• Finally, **attack** with your Active Pokémon. Attacking ends your turn.",
            }),
            ("Attacking", new[]
            {
                "• The attacking Pokémon needs Energy attached that matches the attack's cost. Colorless symbols can be paid with any Energy.",
                "• Put damage counters on the defending Pokémon. **Weakness** usually doubles the damage; **Resistance** usually reduces it by 30.",
                "• When a Pokémon's damage reaches its HP, it's **Knocked Out**: it goes to the discard pile, the attacker takes Prize cards, and the defender moves a Benched Pokémon up to Active.",
            }),
            ("Special Conditions", new[]
            {
                "• **Asleep**, **Burned**, **Confused**, **Paralyzed** and **Poisoned** affect the Active Pokémon. They are checked between turns, and retreating or evolving removes them.",
            }),
            ("How the app helps", new[]
            {
                "• The optimizer keeps evolution lines complete, so a Stage 2 always comes with the Pokémon it evolves from.",
                "• It balances Pokémon, Trainers and Energy (roughly 16 / 33 / 11) and adds the Energy types your attacks need.",
                "• Analyze checks the 60-card size, the 4-copy limit, and the one ACE SPEC and one Radiant Pokémon limits.",
            }),
        });

    static readonly RulesGuide YuGiOh = new(
        "Yu-Gi-Oh!",
        "Duelists summon monsters and use Spell and Trap cards to wipe out the other player's Life Points.",
        "https://www.yugioh-card.com/en/rulebook/", "Official Yu-Gi-Oh! rulebook (Konami)",
        new[]
        {
            ("How to win", new[]
            {
                "• Bring your opponent from **8000 Life Points** to 0.",
                "• Or your opponent has to draw with no cards left in their deck.",
                "• Or a card's own special win condition (like holding all five pieces of Exodia).",
            }),
            ("Setting up", new[]
            {
                "• Shuffle your **Main Deck** (40 to 60 cards). Your **Extra Deck** (up to 15 Fusion, Synchro, Xyz and Link Monsters) stays face down to the side.",
                "• Each player draws **5 cards**. The player who goes first doesn't draw on their first turn and can't attack that turn.",
            }),
            ("Card types", new[]
            {
                "• **Monsters**: have ATK (attack) and DEF (defense). Level 1 to 4 can be Normal Summoned freely; Level 5 and 6 need one Tribute (send a monster you control to the Graveyard), Level 7 and higher need two.",
                "• **Spells**: Normal, Quick-Play, Continuous, Equip, Field and Ritual. Most are played on your turn; Quick-Play Spells can also be used on your opponent's turn if you Set them first.",
                "• **Traps**: must be Set face down first and can be activated starting on the next turn, including during your opponent's turn.",
            }),
            ("Your turn, phase by phase", new[]
            {
                "• **Draw Phase**: draw a card.",
                "• **Standby Phase**: some card effects happen here.",
                "• **Main Phase 1**: Normal Summon or Set **one monster per turn**, Special Summon as card effects allow, change monsters' battle positions, play and Set Spells and Traps.",
                "• **Battle Phase**: attack with your monsters in Attack Position.",
                "• **Main Phase 2**: the same actions as Main Phase 1, for things you didn't do yet.",
                "• **End Phase**: end your turn. If you have more than 6 cards in hand, discard down to 6.",
            }),
            ("Battles", new[]
            {
                "• **Attack vs Attack Position**: the monster with lower ATK is destroyed, and its controller loses the difference in Life Points. On a tie, both are destroyed.",
                "• **Attack vs Defense Position**: if ATK is higher than DEF, the defender is destroyed, but no damage is dealt. If ATK is lower, the attacking player loses the difference.",
                "• If the opponent has no monsters, you can attack their Life Points directly.",
            }),
            ("Extra Deck summons", new[]
            {
                "• **Fusion**: usually uses a card like Polymerization to combine the listed monsters.",
                "• **Synchro**: send a **Tuner** plus other monsters whose Levels add up to the Synchro Monster's Level.",
                "• **Xyz**: overlay two or more monsters of the same Level; the materials are then used up to pay for its effects.",
                "• **Link**: send monsters equal to its Link Rating. Its arrows point to zones where you can summon more Extra Deck monsters.",
            }),
            ("Chains", new[]
            {
                "• When a card is activated, either player can respond with a faster card. The chain then resolves backwards, from the last card to the first.",
            }),
            ("How the app helps", new[]
            {
                "• The optimizer builds a 40-card Main Deck (the most consistent size) with about 20 monsters, 14 Spells and 6 Traps, and up to 15 Extra Deck cards.",
                "• Analyze applies the current Forbidden, Limited and Semi-limited list, so you never bring an illegal deck.",
            }),
        });

    static readonly RulesGuide Cyberpunk = new(
        "Cyberpunk TCG",
        "You lead a crew in Night City, competing with your Rival for Gigs (jobs). Gigs are dice: whoever controls most of them becomes a Night City Legend. Besides your deck, you need six dice: d4, d6, d8, d10, d12 and d20.",
        "https://cyberpunktcg.com/gameplay-guide", "Official gameplay guide (WeirdCo)",
        new[]
        {
            ("How to win", new[]
            {
                "• **Start your turn with 7 or more Gig dice** in your Gig area.",
                "• **Overtime**: after the last player's 7th turn, the game becomes sudden death. As soon as a player holds the majority of Gig dice, they win.",
                "• If your Rival has to draw a card with an empty deck, you win.",
                "• Every die counts as one Gig, no matter what number it shows.",
            }),
            ("Setting up", new[]
            {
                "• Shuffle your deck. Place your **3 Legends** face down in random order.",
                "• Both players roll the d20; the higher roll chooses who goes first. The first player spends (turns sideways) their two leftmost Legends, and they don't ready on that first turn.",
                "• Draw **6 cards**. You may mulligan once: shuffle your hand back and draw 6 new cards.",
                "• All dice start in the **fixer** area.",
            }),
            ("Card types", new[]
            {
                "• **Legend**: your deck's star characters. They start face down; flip one at random with **Call a Legend**. Any Legend, face up or down, can also be spent as 1 €$.",
                "• **Unit**: crew members who fight rival Units and steal Gigs. They can't attack the turn they're played.",
                "• **Program**: a one-time effect; it goes to the trash after use.",
                "• **Gear**: equip to a Unit or Legend for a lasting bonus. Gear goes wherever the card it's equipped to goes.",
            }),
            ("Your turn: start phase", new[]
            {
                "• **Ready** all your spent cards (turn them upright).",
                "• **Draw** one card.",
                "• **Gain a Gig**: choose a die from the fixer area (the d20 always goes last), roll it, and put it in your Gig area.",
            }),
            ("Your turn: main phase (any order)", new[]
            {
                "• **Sell** (once per turn): reveal a card with a Sell tag in its top left corner and put it face down in your Eddies area. Each sold card is worth 1 €$ every turn.",
                "• **Play** cards by spending Eddies (and Legends) equal to their cost. New Units have **Lag** this turn: they can't attack.",
                "• **Call a Legend** (once per turn): pay 1 €$ to flip a random face-down Legend.",
                "• **Attack** with ready Units, one at a time.",
            }),
            ("Attacking", new[]
            {
                "• Spend (turn sideways) the attacking Unit and pick a target: a **spent** rival Unit, or your Rival's **Gig area**.",
                "• **Fight**: compare power. The higher power defeats the other; on a tie both are defeated.",
                "• **Steal**: take one of your Rival's Gig dice. A Unit steals one extra Gig for every 10 power (10+ power steals 2, 20+ steals 3). A Unit with 0 power steals nothing.",
                "• Before the attack lands, your Rival may react: Call a Legend, use **Quick** effects, or redirect the attack with a **Blocker** Unit. A blocked attack steals no Gigs.",
                "• **Ready Units can't be attacked**, so keeping a Unit back protects it, but only Blockers and Quick effects can protect your Gigs.",
            }),
            ("Key terms", new[]
            {
                "• **Eddies (€$)**: Night City money. Each face-down card in your Eddies area is 1 €$.",
                "• **Street Cred**: the total of the numbers showing on your Gig dice. Some cards need a certain amount.",
                "• **Go Solo**: pay a Legend's cost to play it as a ready Unit that can attack right away. If it leaves the field, it's removed from the game.",
                "• **Spend / ready**: sideways cards are spent; upright cards are ready.",
            }),
            ("Building a deck", new[]
            {
                "• Exactly **3 Legends** with different names, plus **40 to 50** other cards, with at most **3 copies** of any card.",
                "• **RAM**: each Legend gives RAM in its own color (usually 2). A card's RAM must be no higher than your Legends' total RAM in that card's color. Two Green Legends let you play Green cards up to 4 RAM.",
            }),
            ("How the app helps", new[]
            {
                "• The optimizer tries every trio of Legends you own and picks the one whose RAM unlocks your strongest 40-card deck.",
                "• Analyze flags any card your Legends don't have enough RAM for, and counts your cards with a Sell tag, since selling one each turn is how you earn Eddies.",
            }),
        });

    /// <summary>Writes a guide into a RichTextBox with headings, bullets and bold terms.</summary>
    public static void Render(RichTextBox box, RulesGuide guide, Font baseFont)
    {
        using var titleFont = new Font(baseFont.FontFamily, baseFont.Size + 7, FontStyle.Bold);
        using var headFont = new Font(baseFont.FontFamily, baseFont.Size + 2.5f, FontStyle.Bold);
        using var boldFont = new Font(baseFont, FontStyle.Bold);
        var accent = Color.FromArgb(40, 60, 140);

        box.SuspendLayout();
        box.Clear();

        void Write(string text, Font font, Color color, int indent = 0, int hanging = 0, bool bullet = false)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionIndent = indent;
            box.SelectionHangingIndent = hanging;
            box.SelectionBullet = bullet;
            box.SelectionFont = font;
            box.SelectionColor = color;
            box.AppendText(text);
        }

        void WriteRich(string line, int indent, bool bullet)
        {
            var parts = line.Split("**");
            for (int i = 0; i < parts.Length; i++)
                Write(parts[i], i % 2 == 1 ? boldFont : baseFont, SystemColors.WindowText, indent, 0, bullet);
            Write("\n", baseFont, SystemColors.WindowText, indent, 0, bullet);
        }

        Write(guide.Title + "\n", titleFont, accent);
        Write(guide.Intro + "\n", baseFont, SystemColors.WindowText);
        foreach (var (heading, lines) in guide.Sections)
        {
            Write("\n" + heading + "\n", headFont, accent);
            foreach (var line in lines)
            {
                bool bullet = line.StartsWith("• ");
                WriteRich(bullet ? line[2..] : line, bullet ? 12 : 0, bullet);
            }
        }
        Write("\nThis guide is a summary for learning. The official rules have the full details and the final word.\n",
              new Font(baseFont, FontStyle.Italic), Color.DimGray);
        box.SelectionStart = 0;
        box.ScrollToCaret();
        box.ResumeLayout();
    }
}
