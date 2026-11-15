# TCG deck scanner

A Windows and Android app that scans trading cards hands-free with a camera, keeps track of your collection and its value, and builds or fixes a deck for Magic: The Gathering, Pokémon TCG, Yu-Gi-Oh!, the Cyberpunk TCG, or the Gundam Card Game.

## Download (easiest)

Go to the [latest release](https://github.com/snk3x/TCGDeckScanner/releases/latest):

- **Windows:** download the zip, unzip it, and double-click **TCG deck scanner.exe**. Nothing else to install. Windows may show "Windows protected your PC" because the app isn't signed with a paid certificate; click **More info**, then **Run anyway**.
- **Android (6.0 or newer):** download the `.apk` on your phone and open it. Android asks to allow installing apps from your browser or Files app the first time; allow it, then tap **Install**.

## Install from source

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) (Windows 10 or 11).
2. Download this project: click **Code → Download ZIP** on GitHub and unzip it, or run `git clone`.
3. Double-click **Install or update.cmd**.

## Run it

Open **TCG deck scanner** from the shortcut on your Desktop or from the Start menu.

To update it after the code changes, double-click **Install or update.cmd** in this folder again. It builds the app, installs it to `%LOCALAPPDATA%\Programs\TCG deck scanner`, refreshes the shortcuts, and opens the app. Your collection and settings are kept.

Needs Windows 10 or 11, the .NET 10 runtime, an internet connection, and English in Windows language settings (for text recognition).

## Android app

The Android app has the same features as the Windows app, in five tabs: Scan, Collection, Deck, Learn and Settings. Open the Scan tab and point the camera at a card: it reads the card on the phone with Google ML Kit (no internet needed for that part), beeps and vibrates, and adds it. Then show the next card. You can also take or choose a photo, or type a name.

To build it yourself you need the .NET 10 SDK with the `maui-android` workload:

```
dotnet workload install maui-android
dotnet build Android\TcgMobile.csproj -c Release
```

## Project layout

| Folder | What's in it |
|---|---|
| `Core` | Shared by both apps: card databases, prices, deck rules and optimizer, starter decks, how-to-play guides, reading card text |
| `Windows` | The Windows app: window, webcam, Windows text recognition |
| `Android` | The Android app: phone screens, camera photos, ML Kit text recognition |

## Use it

1. **Scan cards.** Click Start camera and hold one card flat inside the green frame. With **Auto scan** on (the default), the card is added on its own: the frame turns gold while checking, then flashes green with a sound. Show the next card. To add another copy of the same card, take it away for a second and show it again. You can also press F5, load a photo, or type a name. The game is detected automatically.
2. **Collection.** Every scanned card lands here. Edit Owned or In deck directly in the yellow columns.
   - **Loaner cards** are cards you could borrow or want to buy. Tick **Scan as loaner** on the Scan tab (or type a count in the Loaner column) and add a note like "from Alex". Loaners never count as owned. Use **Show: Loaners only** to see them with the cost to buy, and **Mark loaner as bought** when you get one.
3. **Deck.** Pick the format at the top, then:
   - **Analyze** checks the deck against the format's rules and gives a health score.
   - **Optimize from my collection** rebuilds the deck using only cards you own, plus free basic lands or basic Energy.
   - **Budget swaps** looks at deck cards above a max price you set and suggests a cheaper printing of the same card, or (Magic, Gundam, Cyberpunk) a cheaper card that does a similar job. Tick the swaps you want; swapped-in cards you don't own become loaners, so the loaner list doubles as a shopping list.
   - **Loaner picks** compares the best deck from your owned cards with the best deck using your loaners too, and lists which loaners would help and what they'd replace. Nothing changes unless you choose to use that deck.
   - **Undo optimize** puts the previous deck back (also after budget swaps and loaner picks).
   - **Import list** and **Export list** handle plain text decklists.
   - **Starter decks** (Gundam only) lists starter decks ST01 to ST14 and shows how many of each deck's cards you've scanned, highlighting the likely match. Pick yours to build its 50-card deck and 10-card Resource Deck in one click, and optionally add the box's cards to your collection. Then scan your booster cards and press Optimize to upgrade it.
4. **How to play.** A beginner's guide to the game picked at the top: how to win, setup, turn steps, card types, combat and key terms, with a link to the official rules.

## Exact printings

The same card is often printed in many sets at very different prices. The scanner reads the small print on the card to find your exact version:

| Game | What it reads |
|---|---|
| Magic | Set code and collector number, bottom left (for example `M21 • EN` and `0157`) |
| Pokémon | Card number and set size, bottom (for example `025/165`), or promo codes like `SWSH234` |
| Yu-Gi-Oh! | Set code under the artwork (for example `LOB-EN001`) |
| Cyberpunk | Card name and subtitle (each card currently has one printing) |
| Gundam | Card number at the top (for example `GD01-001`); alternate arts share the number, so use Pick printing for those |

After each scan the app says whether the printing was **matched from the card** or is a **best guess**. If it's a guess or wrong, press **Pick printing** (Scan cards or Collection tab) and choose your version from the list of every printing.

## Card values

Every card shows its market price in US dollars:

- **Scan cards** shows the value of each card you scan, plus a running total for the session.
- **Collection** has Price each and Value owned columns, with the collection total at the top. Click a column header to sort, for example by value.
- **Deck** shows the total value of the deck.
- **Refresh prices** fetches current prices for every card in the collection. **Export to CSV** saves the collection with values for Excel.

| Game | Price source |
|---|---|
| Magic | TCGplayer market price for the exact printing (via Scryfall) |
| Pokémon | TCGplayer market price for the printing (via TCGdex) |
| Yu-Gi-Oh! | TCGplayer price for the matched set code and rarity (via YGOPRODeck), or the cheapest printing if no set code was read |
| Cyberpunk | TCGplayer near mint price via JustTCG (needs your free API key, see below) |
| Gundam | TCGplayer near mint price via JustTCG, if JustTCG lists the game (same key; Settings tells you) |

### Cyberpunk and Gundam prices

1. Sign up for the free plan at [justtcg.com](https://justtcg.com/pricing) and copy your API key.
2. In the app, click **Settings...**, paste the key, and press **Test and save**.
3. On the Collection tab, press **Refresh prices**.

The key is stored encrypted for your Windows account in `%APPDATA%\TCG deck scanner\JustTCG key.dat`. The free plan is for personal use and allows 100 requests a day. The app downloads all Cyberpunk prices in about 8 requests and reuses them for a day. Gundam has over 2,000 printings, so instead the app looks up each new card number once (1 request) and remembers it for a day.

Your collection is saved to `%APPDATA%\TCG deck scanner\Card library.json`.

## What the optimizer checks

| Game | Rules enforced | How cards are ranked |
|---|---|---|
| Magic | Deck size, 4-copy or singleton limit, banned and restricted lists, Commander color identity | EDHREC play rate, mana curve fit, land count from average mana value, basics split by colored mana symbols |
| Pokémon | 60 cards, 4 copies by name, ACE SPEC and Radiant limits, Basic Pokémon present, complete evolution lines | Strength heuristic (HP, abilities, ex/V), Supporter count, Energy matched to attack costs |
| Yu-Gi-Oh! | 40 to 60 Main, 15 Extra, Forbidden/Limited/Semi-limited list | YGOPRODeck popularity, monster/spell/trap balance, shared type and attribute |
| Cyberpunk | 3 Legends with different names, 40 to 50 card main deck, 3 copies per card, every card's RAM covered by the Legends' RAM in its color | Tries every trio of your Legends and keeps the one that unlocks the strongest deck; rarity and power-for-cost heuristic, unit/program/gear balance, cost curve |
| Gundam | Exactly 50 cards plus a 10-card Resource Deck, 1 or 2 colors, 4 copies per card number (alternate arts included), no tokens | Best 2 colors, about 28 Units / 10 Pilots / 10 Commands / 2 Bases, level curve, bonus for Units whose Link Pilot you own; rarity and stats-for-cost heuristic |

Card data comes from Scryfall, TCGdex, YGOPRODeck, CyberpunkDB (an unofficial fan database built on the official card data), and gcg-api. Legality and banlists are live from those services. Gundam data contains data from gcg-api (https://gcgapi.com), made available under the Open Database License (ODbL) v1.0.

## Credits

Thanks to Levi for field testing the app and suggesting improvements.

## Limits

- Pokémon, Cyberpunk and Gundam ratings are heuristics because those databases have no play-rate data.
- Gundam's official ban list isn't applied yet; check Bandai's site before a tournament.
- This is an unofficial fan project. It is not affiliated with or endorsed by Wizards of the Coast, The Pokémon Company, Konami, CD PROJEKT RED, WeirdCo, Bandai, or the card databases it uses. Card names, text and images belong to their owners; images are loaded from the official or database sites at run time and are not included in this repository.
- Bandai doesn't publish starter deck copy counts, so the starter lists come from community decklists (ExBurst, Cardsrealm, Gamepedia), each checked to total 50 cards. ST09 has only one source. ST15 isn't released yet (April 2027).
- The optimizer favors strong, popular, legal cards with a good curve. It does not simulate games or know combos.
- Full-art, Secret Lair, and foreign-language cards are harder to read. Type the name if a scan misses, and use Pick printing for the exact version.
