using System.Drawing.Drawing2D;
using System.Text;
using System.Text.RegularExpressions;

namespace TcgScanner;

public class MainForm : Form
{
    readonly AppData _data = Storage.Load();
    readonly ScryfallSource _mtg = new();
    readonly TcgdexSource _pkm = new();
    readonly YgoSource _ygo = new();
    readonly CyberpunkSource _cyber = new();
    readonly GundamSource _gundam = new();
    readonly CameraService _camera = new();
    readonly Dictionary<Game, DeckRules> _rules;

    ComboBox _gameBox, _formatBox, _cameraBox;
    CheckBox _autoDetect, _addToDeck, _autoScan;
    readonly AutoScanSession _auto = new();
    readonly System.Windows.Forms.Timer _autoTimer = new() { Interval = 700 };
    bool _autoBusy;
    DateTime _flashUntil;
    PictureBox _cameraView, _lastCardPic;
    Label _lastCardLabel, _status, _deckSummary, _collectionSummary;
    int _sessionCount;
    double _sessionValue;
    ListBox _scanLog;
    TextBox _manualName, _filterBox, _reportBox;
    DataGridView _collectionGrid, _deckGrid;
    Button _camBtn, _scanBtn, _optimizeBtn, _starterBtn;
    SplitContainer _scanSplit, _deckSplit;

    LibraryEntry _lastAdded;
    bool _lastAddedToDeck, _lastAddedAsLoaner;
    CheckBox _asLoaner;
    ComboBox _showBox;
    LibrarySnapshot _undo; // the collection before the last deck action, so Undo can put it back exactly
    int _frameBusy;
    bool _busy, _refreshing;

    Game CurrentGame => (Game)Math.Max(0, _gameBox.SelectedIndex);
    Library Lib => _data.Get(CurrentGame);
    DeckRules Rules => _rules[CurrentGame];
    string Format => _formatBox.SelectedItem as string ?? Rules.Formats[0];

    ICardSource Source(Game g) => g switch
    {
        Game.Magic => _mtg,
        Game.Pokemon => _pkm,
        Game.YuGiOh => _ygo,
        Game.Cyberpunk => _cyber,
        _ => _gundam
    };

    public MainForm()
    {
        _rules = new()
        {
            [Game.Magic] = new MagicRules(_mtg),
            [Game.Pokemon] = new PokemonRules(_pkm),
            [Game.YuGiOh] = new YugiohRules(),
            [Game.Cyberpunk] = new CyberpunkRules(),
            [Game.Gundam] = new GundamRules(),
        };
        Text = "TCG deck scanner";
        Width = 1300;
        Height = 840;
        MinimumSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        BuildUi();
        _camera.FrameReady += OnFrame;
        _autoTimer.Tick += async (s, e) => await AutoScanTick();
        _autoTimer.Start();
        _gameBox.SelectedIndex = (int)_data.LastGame;
        Shown += (s, e) =>
        {
            _scanSplit.SplitterDistance = (int)(_scanSplit.Width * 0.6);
            _deckSplit.SplitterDistance = (int)(_deckSplit.Width * 0.5);
        };
    }

    // ------------------------------------------------------------------ layout

    void BuildUi()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8, 6, 8, 0), WrapContents = false };
        top.Controls.Add(Lbl("Game"));
        _gameBox = Combo(200, "Magic: The Gathering", "Pokémon TCG", "Yu-Gi-Oh!", "Cyberpunk TCG", "Gundam Card Game");
        top.Controls.Add(_gameBox);
        top.Controls.Add(Lbl("Format"));
        _formatBox = Combo(150);
        top.Controls.Add(_formatBox);
        var settings = Btn("Settings...", async (s, e) => await ShowSettings());
        settings.Margin = new Padding(16, 0, 3, 0);
        top.Controls.Add(settings);
        _gameBox.SelectedIndexChanged += (s, e) => OnGameChanged();
        _formatBox.SelectedIndexChanged += (s, e) =>
        {
            if (_refreshing) return;
            _data.Formats[CurrentGame] = Format;
            Save();
            RefreshAll();
        };

        _status = new Label { Dock = DockStyle.Bottom, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), Text = "Ready.", BackColor = SystemColors.ControlLight };

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildScanTab());
        tabs.TabPages.Add(BuildCollectionTab());
        tabs.TabPages.Add(BuildDeckTab());
        tabs.TabPages.Add(BuildHowToPlayTab());

        Controls.Add(tabs);
        Controls.Add(top);
        Controls.Add(_status);
    }

    TabPage BuildScanTab()
    {
        var page = new TabPage("Scan cards");
        _scanSplit = new SplitContainer { Dock = DockStyle.Fill };

        _cameraView = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(28, 28, 28) };
        _cameraView.Paint += DrawGuide;
        var camBar = Flow();
        _cameraBox = Combo(110, "Camera 1", "Camera 2", "Camera 3", "Camera 4");
        _cameraBox.SelectedIndex = 0;
        _camBtn = Btn("Start camera", async (s, e) => await ToggleCamera());
        _scanBtn = Btn("Scan card (F5)", async (s, e) => await ScanFromCamera());
        _scanBtn.Enabled = false;
        camBar.Controls.AddRange(new Control[]
        {
            _cameraBox, _camBtn, _scanBtn,
            Btn("Load photo...", async (s, e) => await ScanFromFile()),
            Btn("Undo last add", (s, e) => UndoLast()),
            Btn("Pick printing...", async (s, e) => await ChangePrinting(_lastAdded)),
        });
        _scanSplit.Panel1.Controls.Add(_cameraView);
        _scanSplit.Panel1.Controls.Add(camBar);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(6) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 40));

        var manual = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        _manualName = new TextBox { Width = 260, PlaceholderText = "Or type a card name" };
        _manualName.KeyDown += async (s, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await AddByName();
        };
        manual.Controls.Add(_manualName);
        manual.Controls.Add(Btn("Add by name", async (s, e) => await AddByName()));

        var checks = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
        _autoDetect = new CheckBox { Text = "Detect the game automatically", AutoSize = true, Checked = true };
        _addToDeck = new CheckBox { Text = "Also add scanned cards to the deck", AutoSize = true };
        _autoScan = new CheckBox { Text = "Auto scan: add cards as soon as the camera sees them", AutoSize = true, Checked = true };
        _autoScan.CheckedChanged += (s, e) =>
        {
            _auto.Reset();
            if (_camera.Running) SetStatus(_autoScan.Checked ? "Auto scan on. Hold a card inside the green frame." : "Auto scan off. Press Scan card (F5) for each card.");
        };
        _asLoaner = new CheckBox { Text = "Scan as loaner (borrowed or to buy, not owned)", AutoSize = true, ForeColor = Color.DarkGoldenrod };
        checks.Controls.Add(_autoScan);
        checks.Controls.Add(_autoDetect);
        checks.Controls.Add(_addToDeck);
        checks.Controls.Add(_asLoaner);

        _lastCardPic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
        _lastCardLabel = new Label { AutoSize = true, MaximumSize = new Size(460, 0), Text = "No card scanned yet.", Padding = new Padding(0, 4, 0, 4) };
        var logTitle = new Label { AutoSize = true, Text = "Scan history", Font = new Font(Font, FontStyle.Bold) };
        _scanLog = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };

        right.Controls.Add(manual, 0, 0);
        right.Controls.Add(checks, 0, 1);
        right.Controls.Add(_lastCardPic, 0, 2);
        right.Controls.Add(_lastCardLabel, 0, 3);
        right.Controls.Add(logTitle, 0, 4);
        right.Controls.Add(_scanLog, 0, 5);
        _scanSplit.Panel2.Controls.Add(right);

        page.Controls.Add(_scanSplit);
        return page;
    }

    TabPage BuildCollectionTab()
    {
        var page = new TabPage("Collection");
        var bar = Flow();
        _filterBox = new TextBox { Width = 240, PlaceholderText = "Filter by name or type", Margin = new Padding(3, 6, 3, 3) };
        _filterBox.TextChanged += (s, e) => RefreshCollection();
        _showBox = Combo(120, "All cards", "Owned only", "Loaners only");
        _showBox.Margin = new Padding(3, 6, 3, 3);
        _showBox.SelectedIndex = 0;
        _showBox.SelectedIndexChanged += (s, e) => RefreshCollection();
        bar.WrapContents = true;
        bar.AutoSize = true;
        bar.Controls.AddRange(new Control[]
        {
            _filterBox,
            _showBox,
            Btn("Mark loaner as bought", (s, e) => MarkBought()),
            Btn("Add 1 to deck", (s, e) => ChangeSelected(_collectionGrid, +1)),
            Btn("Take 1 out of deck", (s, e) => ChangeSelected(_collectionGrid, -1)),
            Btn("Delete from collection", (s, e) => DeleteSelected()),
            Btn("Pick printing...", async (s, e) => await ChangePrinting(
                _collectionGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<LibraryEntry>().FirstOrDefault())),
            Btn("Refresh prices", async (s, e) => await RefreshPrices()),
            Btn("Export to CSV...", (s, e) => ExportCollectionCsv()),
        });
        _collectionSummary = new Label { Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 5, 0, 0), Font = new Font(Font, FontStyle.Bold) };

        _collectionGrid = Grid();
        AddCol(_collectionGrid, "Name", "Name", true, 220);
        AddCol(_collectionGrid, "Type", "Type", true, 180);
        AddCol(_collectionGrid, "Cost", "Cost", true, 55);
        AddCol(_collectionGrid, "Owned", "Owned", false, 55);
        AddCol(_collectionGrid, "InDeck", "In deck", false, 55);
        AddCol(_collectionGrid, "Loaned", "Loaner", false, 55);
        AddCol(_collectionGrid, "LoanNote", "Loaner note", false, 130);
        _collectionGrid.Columns["Loaned"].ToolTipText = "Cards you borrowed or want to buy. They don't count as owned.";
        AddCol(_collectionGrid, "Legal", "Legal", true, 85);
        AddCol(_collectionGrid, "Rating", "Rating", true, 55);
        AddMoneyCol(_collectionGrid, "Price", "Price each", 75);
        AddMoneyCol(_collectionGrid, "Total", "Value owned", 80);
        _collectionGrid.CellValueChanged += OnGridEdit;

        page.Controls.Add(_collectionGrid);
        page.Controls.Add(_collectionSummary);
        page.Controls.Add(bar);
        return page;
    }

    TabPage BuildDeckTab()
    {
        var page = new TabPage("Deck");
        var bar = Flow();
        _optimizeBtn = Btn("Optimize from my collection", async (s, e) => await Optimize());
        _optimizeBtn.Font = new Font(Font, FontStyle.Bold);
        _deckSummary = new Label { AutoSize = true, Margin = new Padding(12, 10, 3, 3) };
        _starterBtn = Btn("Starter decks...", async (s, e) => await ShowStarterDecks());
        bar.WrapContents = true;
        bar.AutoSize = true;
        bar.Controls.AddRange(new Control[]
        {
            Btn("Analyze", (s, e) => Analyze()),
            _optimizeBtn,
            Btn("Budget swaps...", async (s, e) => await ShowBudgetSwaps()),
            Btn("Loaner picks", async (s, e) => await ShowLoanerPicks()),
            _starterBtn,
            Btn("Undo optimize", (s, e) => UndoOptimize()),
            Btn("Import list...", async (s, e) => await ImportList()),
            Btn("Export list...", (s, e) => ExportList()),
            Btn("Clear deck", (s, e) => ClearDeck()),
            _deckSummary,
        });

        _deckSplit = new SplitContainer { Dock = DockStyle.Fill };
        _deckGrid = Grid();
        AddCol(_deckGrid, "InDeck", "Qty", false, 45);
        AddCol(_deckGrid, "Name", "Name", true, 220);
        AddCol(_deckGrid, "Type", "Type", true, 180);
        AddCol(_deckGrid, "Cost", "Cost", true, 60);
        AddMoneyCol(_deckGrid, "Price", "Price each", 70);
        _deckGrid.CellValueChanged += OnGridEdit;
        _reportBox = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 10f), BackColor = SystemColors.Window,
            Text = "Press Analyze to check the deck, or Optimize to build the best deck from the cards you scanned."
        };
        _deckSplit.Panel1.Controls.Add(_deckGrid);
        _deckSplit.Panel2.Controls.Add(_reportBox);

        page.Controls.Add(_deckSplit);
        page.Controls.Add(bar);
        return page;
    }

    RichTextBox _rulesBox;
    LinkLabel _rulesLink;

    TabPage BuildHowToPlayTab()
    {
        var page = new TabPage("How to play");
        var bar = Flow();
        bar.Controls.Add(new Label { Text = "Rules for the game picked at the top.", AutoSize = true, Margin = new Padding(8, 9, 12, 0), ForeColor = Color.DimGray });
        _rulesLink = new LinkLabel { AutoSize = true, Margin = new Padding(3, 9, 3, 0) };
        _rulesLink.LinkClicked += (s, e) =>
        {
            var url = HowToPlay.For(CurrentGame).OfficialUrl;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        };
        bar.Controls.Add(_rulesLink);

        var holder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 12, 12, 12), BackColor = SystemColors.Window };
        _rulesBox = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window,
            ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false, Cursor = Cursors.Arrow,
        };
        holder.Controls.Add(_rulesBox);
        page.Controls.Add(holder);
        page.Controls.Add(bar);
        return page;
    }

    void ShowRules()
    {
        var guide = HowToPlay.For(CurrentGame);
        _rulesLink.Text = guide.OfficialLabel;
        using var font = new Font("Segoe UI", 10.5f);
        HowToPlayView.Render(_rulesBox, guide, font);
    }

    static Label Lbl(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(8, 7, 4, 0) };

    static ComboBox Combo(int width, params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };
        c.Items.AddRange(items);
        return c;
    }

    static Button Btn(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 30, Padding = new Padding(6, 0, 6, 0) };
        b.Click += onClick;
        return b;
    }

    static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Top, Height = 44, Padding = new Padding(4, 5, 4, 0), WrapContents = false };

    static DataGridView Grid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None, EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
        };
        g.DataError += (s, e) => e.ThrowException = false;
        return g;
    }

    static void AddCol(DataGridView g, string name, string header, bool readOnly, int weight)
    {
        int i = g.Columns.Add(name, header);
        g.Columns[i].ReadOnly = readOnly;
        g.Columns[i].FillWeight = weight;
        if (!readOnly) g.Columns[i].DefaultCellStyle.BackColor = Color.FromArgb(255, 252, 230);
    }

    static void AddMoneyCol(DataGridView g, string name, string header, int weight)
    {
        AddCol(g, name, header, true, weight);
        var style = g.Columns[name].DefaultCellStyle;
        style.Format = "$#,##0.00";
        style.FormatProvider = System.Globalization.CultureInfo.InvariantCulture;
        style.Alignment = DataGridViewContentAlignment.MiddleRight;
        g.Columns[name].ValueType = typeof(double);
    }

    /// <summary>Price as a cell value: blank when unknown so it sorts and displays cleanly.</summary>
    static object Price(double usd) => usd > 0 ? usd : null;

    static void KeepSort(DataGridView g, Action fill)
    {
        var col = g.SortedColumn;
        var dir = g.SortOrder == SortOrder.Descending ? System.ComponentModel.ListSortDirection.Descending
                                                      : System.ComponentModel.ListSortDirection.Ascending;
        fill();
        if (col != null) g.Sort(col, dir);
    }

    // ------------------------------------------------------------------ refresh

    void OnGameChanged()
    {
        _refreshing = true;
        _formatBox.Items.Clear();
        _formatBox.Items.AddRange(Rules.Formats);
        int idx = Array.IndexOf(Rules.Formats, _data.Formats.GetValueOrDefault(CurrentGame));
        _formatBox.SelectedIndex = idx < 0 ? 0 : idx;
        _refreshing = false;
        _data.LastGame = CurrentGame;
        _undo = null;
        _starterBtn.Visible = CurrentGame == Game.Gundam;
        Save();
        RefreshAll();
        ShowRules();
        _reportBox.Text = "Press Analyze to check the deck, or Optimize to build the best deck from the cards you scanned.";
    }

    void RefreshAll()
    {
        RefreshCollection();
        RefreshDeck();
    }

    void RefreshCollection()
    {
        _refreshing = true;
        try
        {
            var g = _collectionGrid;
            g.Columns["Cost"].HeaderText = Rules.CostHeader;
            string f = _filterBox.Text.Trim();
            int show = _showBox.SelectedIndex;
            var items = Lib.Entries.Values
                .Where(e => f == "" || e.Card.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                            e.Card.TypeLine.Contains(f, StringComparison.OrdinalIgnoreCase))
                .Where(e => show == 0 || (show == 1 ? e.Owned > 0 : e.Loaned > 0))
                .OrderBy(e => e.Card.Name);
            KeepSort(g, () =>
            {
                g.Rows.Clear();
                foreach (var e in items)
                {
                    int i = g.Rows.Add(e.Card.Name, e.Card.TypeLine, Rules.CostText(e.Card), e.Owned, e.InDeck,
                                       e.Loaned, e.LoanNote, Rules.LegalityLabel(e.Card, Format), (int)Math.Round(e.Card.Quality * 100),
                                       Price(e.Card.PriceUsd), Price(e.Card.PriceUsd * e.Owned));
                    g.Rows[i].Tag = e;
                    if (!string.IsNullOrEmpty(e.Card.PriceSource))
                        g.Rows[i].Cells["Price"].ToolTipText = $"{e.Card.PriceSource}, updated {e.Card.PriceUpdated:d}";
                    if (Rules.LegalityLabel(e.Card, Format) is "Not legal" or "Banned" or "Forbidden")
                        g.Rows[i].Cells["Legal"].Style.ForeColor = Color.Firebrick;
                    if (e.Loaned > 0 && e.Owned == 0) g.Rows[i].DefaultCellStyle.ForeColor = Color.DarkGoldenrod;
                }
            });

            var loaners = Lib.Entries.Values.Where(e => e.Loaned > 0).ToList();
            double toBuy = loaners.Sum(e => e.Loaned * e.Card.PriceUsd);
            string loanerText = loaners.Count == 0 ? ""
                : $"   ·   Loaners {loaners.Sum(e => e.Loaned)}" + (toBuy > 0 ? $", about {Money.Format(toBuy)} to buy" : "");

            var owned = Lib.Entries.Values.Where(e => e.Owned > 0).ToList();
            double value = owned.Sum(e => e.Owned * e.Card.PriceUsd);
            int unpriced = owned.Count(e => e.Card.PriceUsd <= 0 && !e.Card.UnlimitedCopies);
            _collectionSummary.Text = $"{owned.Sum(e => e.Owned)} cards owned   ·   Collection value {(value > 0 ? Money.Format(value) : "$0.00")}" + loanerText +
                                      (JustTcg.Games.ContainsKey(CurrentGame) && !JustTcg.HasKey
                                          ? $"   ·   Add a free JustTCG key in Settings to see {GameInfo.Short(CurrentGame)} prices"
                                          : unpriced > 0 ? $"   ·   {unpriced} without a price (press Refresh prices)" : "");
        }
        finally { _refreshing = false; }
    }

    void RefreshDeck()
    {
        _refreshing = true;
        try
        {
            var g = _deckGrid;
            g.Columns["Cost"].HeaderText = Rules.CostHeader;
            KeepSort(g, () =>
            {
                g.Rows.Clear();
                foreach (var e in Lib.Deck.OrderBy(e => e.Card.IsExtraDeck).ThenBy(e => e.Card.Category)
                                          .ThenBy(e => e.Card.ManaValue).ThenBy(e => e.Card.Name))
                {
                    bool loaner = e.InDeck > e.Owned && e.Loaned > 0;
                    int i = g.Rows.Add(e.InDeck, e.Card.Name + (loaner ? "  (loaner)" : ""), e.Card.TypeLine, Rules.CostText(e.Card), Price(e.Card.PriceUsd));
                    g.Rows[i].Tag = e;
                    if (loaner)
                    {
                        g.Rows[i].DefaultCellStyle.ForeColor = Color.DarkGoldenrod;
                        g.Rows[i].Cells["Name"].ToolTipText = $"{e.InDeck - e.Owned} not owned" + (e.LoanNote != "" ? $": {e.LoanNote}" : "");
                    }
                }
            });
            var report = Rules.Analyze(Lib, Format);
            double deckValue = Lib.Deck.Sum(e => e.InDeck * e.Card.PriceUsd);
            _deckSummary.Text = $"{Lib.DeckCount} cards   ·   Deck value {(deckValue > 0 ? Money.Format(deckValue) : "$0.00")}   ·   Deck health {report.Health}/100";
            _deckSummary.ForeColor = report.Health >= 80 ? Color.DarkGreen : report.Health >= 50 ? Color.DarkGoldenrod : Color.Firebrick;
        }
        finally { _refreshing = false; }
    }

    void OnGridEdit(object sender, DataGridViewCellEventArgs e)
    {
        if (_refreshing || e.RowIndex < 0) return;
        var grid = (DataGridView)sender;
        if (grid.Rows[e.RowIndex].Tag is not LibraryEntry entry) return;
        string col = grid.Columns[e.ColumnIndex].Name;
        if (col == "LoanNote")
        {
            entry.LoanNote = Convert.ToString(grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value)?.Trim() ?? "";
            Save();
            return;
        }
        if (!int.TryParse(Convert.ToString(grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value), out int v) || v < 0) v = 0;
        _undo = null; // edited by hand, so an older deck Undo no longer applies
        if (col == "Owned") entry.Owned = v;
        else if (col == "InDeck") entry.InDeck = v;
        else if (col == "Loaned") entry.Loaned = v;
        else return;
        Save();
        BeginInvoke(new Action(RefreshAll));
    }

    // ------------------------------------------------------------------ camera

    void OnFrame(Bitmap bmp)
    {
        if (IsDisposed || !IsHandleCreated || Interlocked.Exchange(ref _frameBusy, 1) == 1)
        {
            bmp.Dispose();
            return;
        }
        try
        {
            BeginInvoke(new Action(() =>
            {
                var old = _cameraView.Image;
                _cameraView.Image = bmp;
                old?.Dispose();
                Interlocked.Exchange(ref _frameBusy, 0);
            }));
        }
        catch
        {
            bmp.Dispose();
            Interlocked.Exchange(ref _frameBusy, 0);
        }
    }

    async Task ToggleCamera()
    {
        if (_camera.Running)
        {
            _camera.Stop();
            _camBtn.Text = "Start camera";
            _scanBtn.Enabled = false;
            var old = _cameraView.Image;
            _cameraView.Image = null;
            old?.Dispose();
            SetStatus("Camera off.");
            return;
        }
        int idx = _cameraBox.SelectedIndex;
        SetStatus("Opening camera...");
        _camBtn.Enabled = false;
        try
        {
            await Task.Run(() => _camera.Start(idx));
            _camBtn.Text = "Stop camera";
            _scanBtn.Enabled = !_busy;
            SetStatus(_autoScan.Checked
                ? "Camera on. Hold a card flat inside the green frame; it's added automatically. Swap in the next card when you hear the sound."
                : "Camera on. Hold the card flat inside the green frame, then press Scan card (F5).");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _camBtn.Enabled = true; }
    }

    static Rectangle GuideRect(Size frame)
    {
        int h = (int)(frame.Height * 0.9);
        int w = (int)(h * 63.0 / 88.0);
        if (w > frame.Width * 0.9)
        {
            w = (int)(frame.Width * 0.9);
            h = (int)(w * 88.0 / 63.0);
        }
        return new Rectangle((frame.Width - w) / 2, (frame.Height - h) / 2, w, h);
    }

    void DrawGuide(object sender, PaintEventArgs e)
    {
        var img = _cameraView.Image;
        if (img == null || !_camera.Running) return;
        var r = GuideRect(img.Size);
        float scale = Math.Min((float)_cameraView.Width / img.Width, (float)_cameraView.Height / img.Height);
        float ox = (_cameraView.Width - img.Width * scale) / 2, oy = (_cameraView.Height - img.Height * scale) / 2;
        var rc = new RectangleF(ox + r.X * scale, oy + r.Y * scale, r.Width * scale, r.Height * scale);
        // Green normally, gold while auto scan is checking a card, a thick bright flash when one is added.
        bool flash = DateTime.Now < _flashUntil;
        bool checking = _autoScan.Checked && _auto.Status.StartsWith("Checking");
        var color = flash ? Color.FromArgb(0, 255, 120) : checking ? Color.Gold : Color.LimeGreen;
        using var pen = new Pen(color, flash ? 8 : 3);
        e.Graphics.DrawRectangle(pen, rc.X, rc.Y, rc.Width, rc.Height);
        using var dash = new Pen(Color.FromArgb(170, color), 1) { DashStyle = DashStyle.Dash };
        float nameLine = rc.Y + rc.Height * 0.2f;
        e.Graphics.DrawLine(dash, rc.X, nameLine, rc.Right, nameLine);
        using var brush = new SolidBrush(color);
        e.Graphics.DrawString(_autoScan.Checked ? _auto.Status : "Card name goes above this line", Font, brush, rc.X + 6, nameLine + 4);
    }

    // ------------------------------------------------------------------ scanning

    async Task ScanFromCamera()
    {
        if (!_camera.Running || _busy) return;
        using var frame = _camera.Snapshot();
        if (frame == null) { SetStatus("No camera image yet."); return; }
        using var crop = frame.Clone(GuideRect(frame.Size), frame.PixelFormat);
        await ScanBitmapAsync(crop);
    }

    async Task ScanFromFile()
    {
        if (_busy) return;
        using var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff|All files|*.*", Title = "Choose a photo of one card" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var img = new Bitmap(dlg.FileName);
            await ScanBitmapAsync(img);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    /// <summary>Finds the card a scan shows, trying the likely games in order.</summary>
    async Task<Card> FindCardAsync(ScanResult res, bool showProgress)
    {
        var games = !_autoDetect.Checked ? new List<Game> { CurrentGame }
                  : res.GameConfident ? res.GameGuesses.Take(1).ToList()
                  : new[] { CurrentGame }.Concat(res.GameGuesses.Where(g => g != CurrentGame)).ToList();
        foreach (var g in games)
        {
            var game = g;
            var found = await CardMatcher.FindBestAsync(Source(g), res, g,
                showProgress ? cand => SetStatus($"Looking up \"{cand}\" in {GameInfo.Short(game)}...") : null);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Auto scan: reads the camera every 0.7 s and adds a card once it has been seen twice in a row.</summary>
    async Task AutoScanTick()
    {
        if (!_camera.Running || !_autoScan.Checked || _busy || _autoBusy) return;
        _autoBusy = true;
        try
        {
            using var frame = _camera.Snapshot();
            if (frame == null) return;
            using var crop = frame.Clone(GuideRect(frame.Size), frame.PixelFormat);
            var scan = await CardOcr.ReadAsync(crop);
            var card = await _auto.ProcessAsync(scan, s => FindCardAsync(s, showProgress: false), DateTime.Now);
            if (card != null)
            {
                AddCard(card);
                System.Media.SystemSounds.Asterisk.Play();
                _flashUntil = DateTime.Now.AddSeconds(0.8);
            }
            else SetStatus("Auto scan: " + _auto.Status);
            _cameraView.Invalidate();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _autoBusy = false; }
    }

    async Task ScanBitmapAsync(Bitmap card)
    {
        if (_busy) return;
        SetBusy(true, "Reading the card...");
        try
        {
            var res = await CardOcr.ReadAsync(card);
            if (res.NameCandidates.Count == 0)
            {
                SetStatus("No text found. Try more light, less glare, and fill the frame with the card.");
                return;
            }

            var found = await FindCardAsync(res, showProgress: true);

            if (found == null)
            {
                Log("No match. Read: " + string.Join(" | ", res.NameCandidates.Take(4)));
                SetStatus("No matching card. Try again with better lighting, or type the name.");
                return;
            }
            AddCard(found);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    async Task AddByName()
    {
        string name = _manualName.Text.Trim();
        if (name.Length < 2 || _busy) return;
        SetBusy(true, $"Looking up \"{name}\"...");
        try
        {
            var card = await Source(CurrentGame).FindAsync(name, null);
            if (card == null) { SetStatus($"No {GameInfo.Short(CurrentGame)} card found for \"{name}\"."); return; }
            AddCard(card);
            _manualName.Clear();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    void AddCard(Card c)
    {
        if (c.Game != CurrentGame) _gameBox.SelectedIndex = (int)c.Game;
        bool toDeck = _addToDeck.Checked, asLoaner = _asLoaner.Checked;
        var e = Lib.Add(c, asLoaner ? 0 : 1, toDeck ? 1 : 0);
        if (asLoaner) e.Loaned++;
        _undo = null; // a deck Undo after this would also remove the card just scanned
        _lastAdded = e;
        _lastAddedToDeck = toDeck;
        _lastAddedAsLoaner = asLoaner;
        _sessionCount++;
        _sessionValue += c.PriceUsd;
        Save();
        RefreshAll();
        ShowCard(e);
        string price = c.PriceUsd > 0 ? Money.Format(c.PriceUsd) : "no price";
        string printing = c.ExactPrinting ? "printing matched" : "printing guessed";
        Log($"{DateTime.Now:t}   {c.Name}   ({c.SetName} {c.Number}, {printing})   {price}   " +
            (asLoaner ? $"loaner {e.Loaned}" : $"owned {e.Owned}") + (toDeck ? $", in deck {e.InDeck}" : ""));
        SetStatus(c.ExactPrinting
            ? $"Added {c.Name} ({price}). Wrong card? Press Undo last add."
            : $"Added {c.Name} ({price}). Couldn't read the set code, so press Pick printing if this isn't your version.");
    }

    void ShowCard(LibraryEntry e)
    {
        var c = e.Card;
        var oldImage = _lastCardPic.Image;
        _lastCardPic.Image = null;
        oldImage?.Dispose();
        if (!string.IsNullOrEmpty(c.ImageUrl)) _ = LoadCardImageAsync(c.ImageUrl);
        string value = c.PriceUsd > 0 ? $"Value: {Money.Format(c.PriceUsd)}  ({c.PriceSource})" : "Value: no price listed";
        string printing = c.ExactPrinting ? "matched from the card" : "best guess, press Pick printing to change";
        _lastCardLabel.Text = $"{c.Name}\n{c.TypeLine}\n{c.SetName} {(c.Number != "" ? "#" + c.Number : "")}  ({printing})\n" +
                              $"{value}\n" +
                              $"{Format}: {Rules.LegalityLabel(c, Format)}   ·   Owned: {e.Owned}\n\n" +
                              SessionText();
    }

    string _imageUrl;

    async Task LoadCardImageAsync(string url)
    {
        _imageUrl = url;
        try
        {
            var bmp = await CardImage.LoadAsync(url);
            if (_imageUrl != url || IsDisposed) { bmp.Dispose(); return; } // a newer card was shown meanwhile
            var old = _lastCardPic.Image;
            _lastCardPic.Image = bmp;
            old?.Dispose();
        }
        catch { /* the picture is optional */ }
    }

    string SessionText() =>
        $"This session: {_sessionCount} card{(_sessionCount == 1 ? "" : "s")} scanned, worth {(_sessionValue > 0 ? Money.Format(_sessionValue) : "$0.00")}";

    void UndoLast()
    {
        _auto.Reset();
        var e = _lastAdded;
        if (e == null || !_data.Libraries.Values.Any(l => l.Entries.ContainsValue(e)))
        {
            SetStatus("Nothing to undo.");
            return;
        }
        var lib = _data.Libraries.Values.First(l => l.Entries.ContainsValue(e));
        if (_lastAddedAsLoaner) e.Loaned = Math.Max(0, e.Loaned - 1);
        else e.Owned = Math.Max(0, e.Owned - 1);
        if (_lastAddedToDeck) e.InDeck = Math.Max(0, e.InDeck - 1);
        if (e.Owned == 0 && e.InDeck == 0 && e.Loaned == 0) lib.Remove(e);
        Log($"Undid {e.Card.Name}");
        _lastAdded = null;
        _sessionCount = Math.Max(0, _sessionCount - 1);
        _sessionValue = Math.Max(0, _sessionValue - e.Card.PriceUsd);
        _lastCardPic.Image = null;
        _lastCardLabel.Text = "Last add undone.\n\n" + SessionText();
        Save();
        RefreshAll();
        SetStatus($"Removed {e.Card.Name}.");
    }

    // ------------------------------------------------------------------ collection and deck actions

    void ChangeSelected(DataGridView grid, int delta)
    {
        foreach (DataGridViewRow row in grid.SelectedRows)
            if (row.Tag is LibraryEntry e) e.InDeck = Math.Max(0, e.InDeck + delta);
        _undo = null;
        Save();
        RefreshAll();
    }

    void DeleteSelected()
    {
        var entries = _collectionGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<LibraryEntry>().ToList();
        if (entries.Count == 0) return;
        string what = entries.Count == 1 ? entries[0].Card.Name : $"{entries.Count} cards";
        if (MessageBox.Show(this, $"Delete {what} from your collection? This cannot be undone.", "Delete cards",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        foreach (var e in entries) Lib.Remove(e);
        _undo = null;
        Save();
        RefreshAll();
    }

    // ------------------------------------------------------------------ Gundam starter decks

    async Task ShowStarterDecks()
    {
        if (_busy) return;
        // How much of each starter is already in the collection, to suggest which one the user has.
        var ownedNumbers = Lib.Entries.Values.Where(e => e.Owned > 0).GroupBy(e => e.Card.Number)
                              .ToDictionary(g => g.Key, g => g.Sum(e => e.Owned));
        int Matched(StarterDeck d) => d.Cards.Keys.Count(ownedNumbers.ContainsKey);
        var best = StarterDecks.Gundam.OrderByDescending(Matched).First();

        using var f = new Form
        {
            Text = "Gundam starter decks", Width = 640, Height = 520, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font
        };
        var intro = new Label
        {
            Dock = DockStyle.Top, Height = 54, Padding = new Padding(8, 8, 8, 0),
            Text = "Pick the starter deck you have. The app builds its 50-card deck and 10-card Resource Deck for you. " +
                   (Matched(best) > 0 ? $"Based on your scanned cards, it looks like {best.Code} {best.Name}." : "")
        };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        list.Columns.Add("Deck", 90);
        list.Columns.Add("Name", 230);
        list.Columns.Add("Cards", 60);
        list.Columns.Add("You've scanned", 150);
        foreach (var d in StarterDecks.Gundam)
        {
            int m = Matched(d);
            var item = new ListViewItem(new[] { d.Code, d.Name, d.Total.ToString(), m == 0 ? "" : $"{m} of {d.Cards.Count} different cards" }) { Tag = d };
            if (d == best && m > 0) item.Font = new Font(list.Font, FontStyle.Bold);
            list.Items.Add(item);
        }
        var pick = list.Items.Cast<ListViewItem>().First(i => ReferenceEquals(i.Tag, best));
        pick.Selected = true;
        pick.EnsureVisible();

        var options = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.TopDown, Padding = new Padding(8, 6, 8, 0) };
        var own = new CheckBox { Text = "I own this deck: add its cards to my collection", AutoSize = true, Checked = true };
        var replace = new CheckBox { Text = "Replace my current deck (otherwise the cards are only added)", AutoSize = true, Checked = true };
        options.Controls.Add(own);
        options.Controls.Add(replace);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "Build this deck", DialogResult = DialogResult.OK, AutoSize = true };
        bar.Controls.Add(cancel);
        bar.Controls.Add(ok);
        list.DoubleClick += (s, e) => { if (list.SelectedItems.Count > 0) f.DialogResult = DialogResult.OK; };
        f.Controls.Add(list);
        f.Controls.Add(intro);
        f.Controls.Add(options);
        f.Controls.Add(bar);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK || list.SelectedItems.Count == 0) return;

        await BuildStarterDeck((StarterDeck)list.SelectedItems[0].Tag, own.Checked, replace.Checked);
    }

    async Task BuildStarterDeck(StarterDeck deck, bool addToCollection, bool replaceDeck)
    {
        SetBusy(true, $"Building {deck.Code} {deck.Name}...");
        try
        {
            _undo = new LibrarySnapshot(Lib);
            string summary = await CollectionActions.BuildStarterAsync(Lib, deck, addToCollection, replaceDeck);
            Save();
            RefreshAll();
            var report = Rules.Analyze(Lib, Format).ToString();
            _reportBox.Text = Crlf(summary + "\nUndo optimize puts your previous deck back.\n\n" + report);
            SetStatus($"Built {deck.Code} {deck.Name}.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    async Task ShowSettings()
    {
        using var f = new Form
        {
            Text = "Settings", Width = 620, Height = 380, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1 };
        var title = new Label { Text = "Cyberpunk and Gundam prices (JustTCG)", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        var info = new Label
        {
            AutoSize = true, MaximumSize = new Size(570, 0),
            Text = "Magic, Pokémon and Yu-Gi-Oh! prices need no setup. Cyberpunk and Gundam prices come from JustTCG, which needs your own free API key. " +
                   "The free plan is for personal use and allows 100 requests a day. Cyberpunk prices take about 8 requests a day; " +
                   "Gundam uses 1 request per new card number, remembered for a day."
        };
        var link = new LinkLabel { Text = "Get a free key at justtcg.com", AutoSize = true };
        link.LinkClicked += (s, e) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://justtcg.com/pricing") { UseShellExecute = true }); } catch { }
        };
        var keyBox = new TextBox
        {
            Width = 560, UseSystemPasswordChar = true,
            PlaceholderText = JustTcg.HasKey ? "A key is saved. Paste a new one to replace it." : "Paste your API key (starts with tcg_)"
        };
        var result = new Label { AutoSize = true, MaximumSize = new Size(570, 0), ForeColor = Color.DimGray };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var save = new Button { Text = "Test and save", AutoSize = true };
        var remove = new Button { Text = "Remove key", AutoSize = true, Enabled = JustTcg.HasKey };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { save, remove, close });

        save.Click += async (s, e) =>
        {
            string key = keyBox.Text.Trim();
            if (key.Length < 8) { result.Text = "Paste your JustTCG API key first."; return; }
            save.Enabled = false;
            result.Text = "Checking the key...";
            try
            {
                result.Text = await JustTcg.TestKeyAsync(key);
                JustTcg.SaveKey(key);
                keyBox.Clear();
                keyBox.PlaceholderText = "A key is saved. Paste a new one to replace it.";
                remove.Enabled = true;
                result.Text += " Saved. Press Refresh prices on the Collection tab to price your Cyberpunk cards.";
            }
            catch (Exception ex)
            {
                result.Text = ex is HttpRequestException or TaskCanceledException
                    ? "Could not reach JustTCG. Check your internet connection."
                    : ex.Message;
            }
            finally { save.Enabled = true; }
        };
        remove.Click += (s, e) =>
        {
            JustTcg.RemoveKey();
            remove.Enabled = false;
            keyBox.PlaceholderText = "Paste your API key (starts with tcg_)";
            result.Text = "Key removed.";
        };

        layout.Controls.AddRange(new Control[] { title, info, link, keyBox, buttons, result });
        f.Controls.Add(layout);
        f.CancelButton = close;
        f.ShowDialog(this);
        RefreshAll();
        await Task.CompletedTask;
    }

    async Task ChangePrinting(LibraryEntry entry)
    {
        if (_busy) return;
        if (entry == null || !Lib.Entries.ContainsValue(entry))
        {
            SetStatus("Select a card first (scan one, or pick a row in the Collection tab).");
            return;
        }
        var source = Source(entry.Card.Game);
        SetBusy(true, $"Loading printings of {entry.Card.Name}...");
        List<Card> prints;
        try { prints = await source.PrintingsAsync(entry.Card); }
        catch (Exception ex) { ShowError(ex); SetBusy(false); return; }
        SetBusy(false, "");
        if (prints.Count == 0) { SetStatus($"No other printings found for {entry.Card.Name}."); return; }

        var pick = PickPrinting(entry.Card, prints);
        if (pick == null) return;

        SetBusy(true, "Loading that printing...");
        try
        {
            var full = await source.RefreshAsync(pick) ?? pick;
            double oldPrice = entry.Card.PriceUsd;
            bool wasLast = entry == _lastAdded;
            var moved = CollectionActions.ChangePrinting(Lib, entry, full);
            _undo = null; // the collection changed, so an older deck Undo no longer applies
            if (wasLast)
            {
                _lastAdded = moved;
                _sessionValue = Math.Max(0, _sessionValue - oldPrice + full.PriceUsd);
            }
            Save();
            RefreshAll();
            ShowCard(moved);
            Log($"Changed {full.Name} to {full.SetName} {full.Number}   {(full.PriceUsd > 0 ? Money.Format(full.PriceUsd) : "no price")}");
            SetStatus($"{full.Name} is now the {full.SetName} printing.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    Card PickPrinting(Card current, List<Card> prints)
    {
        using var f = new Form
        {
            Text = $"Pick printing of {current.Name}", Width = 720, Height = 560, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font
        };
        var filter = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Filter by set name, code or number" };
        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 34, Padding = new Padding(4, 8, 4, 0),
            Text = current.Game == Game.Pokemon
                ? "Match the set and the number printed at the bottom of your card. The price loads after you choose."
                : "Match the set code and number printed on your card."
        };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        list.Columns.Add("Set", 300);
        list.Columns.Add("Code", 90);
        list.Columns.Add("Number", 90);
        list.Columns.Add(current.Game == Game.Pokemon ? "Card number" : "Rarity", 110);
        list.Columns.Add("Price", 80, HorizontalAlignment.Right);

        void Fill()
        {
            string q = filter.Text.Trim();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var p in prints.Where(p => q == "" || $"{p.SetName} {p.SetCode} {p.Number}".Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                var item = new ListViewItem(new[] { p.SetName, p.SetCode, p.Number, p.Rarity, Money.Format(p.PriceUsd) }) { Tag = p };
                if (p.Id == current.Id && p.Number == current.Number && p.Rarity == current.Rarity)
                {
                    item.Font = new Font(list.Font, FontStyle.Bold);
                    item.Selected = true;
                }
                list.Items.Add(item);
            }
            list.EndUpdate();
            if (list.SelectedItems.Count > 0) list.SelectedItems[0].EnsureVisible();
        }
        filter.TextChanged += (s, e) => Fill();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "Use this printing", DialogResult = DialogResult.OK, AutoSize = true };
        bar.Controls.Add(cancel);
        bar.Controls.Add(ok);
        list.DoubleClick += (s, e) => { if (list.SelectedItems.Count > 0) f.DialogResult = DialogResult.OK; };

        f.Controls.Add(list);
        f.Controls.Add(filter);
        f.Controls.Add(hint);
        f.Controls.Add(bar);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        Fill();
        return f.ShowDialog(this) == DialogResult.OK && list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as Card : null;
    }

    async Task RefreshPrices()
    {
        if (_busy) return;
        var entries = Lib.Entries.Values.ToList();
        if (entries.Count == 0) { SetStatus("Scan some cards first."); return; }
        SetBusy(true);
        int done = 0, failed = 0;
        try
        {
            if (CurrentGame == Game.Cyberpunk && JustTcg.HasKey)
            {
                SetStatus("Downloading Cyberpunk prices from JustTCG...");
                await JustTcg.PricesAsync(Game.Cyberpunk, force: true);
            }
            if (CurrentGame == Game.Gundam && JustTcg.HasKey) JustTcg.ExpireGundamPrices();
            if (CurrentGame == Game.Gundam) await GundamSource.CatalogueAsync(force: true);
            foreach (var e in entries)
            {
                SetStatus($"Updating prices ({++done} of {entries.Count}): {e.Card.Name}");
                var fresh = await Source(CurrentGame).RefreshAsync(e.Card);
                if (fresh == null) { failed++; continue; }
                e.Card = fresh;
            }
            Save();
            RefreshAll();
            SetStatus($"Prices updated for {done - failed} cards" + (failed > 0 ? $"; {failed} could not be found." : "."));
        }
        catch (Exception ex)
        {
            Save();
            RefreshAll();
            ShowError(ex);
        }
        finally { SetBusy(false); }
    }

    void ExportCollectionCsv()
    {
        if (!Lib.Entries.Values.Any(e => e.Owned > 0 || e.InDeck > 0)) { SetStatus("The collection is empty."); return; }
        using var dlg = new SaveFileDialog { Filter = "CSV file|*.csv", FileName = $"{GameInfo.Short(CurrentGame)} collection.csv", Title = "Export collection" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, DeckList.Csv(Lib), new UTF8Encoding(true));
        SetStatus($"Saved {Path.GetFileName(dlg.FileName)}.");
    }

    void Analyze() => _reportBox.Text = Crlf(Rules.Analyze(Lib, Format).ToString());

    async Task Optimize()
    {
        if (_busy) return;
        if (!Lib.Entries.Values.Any(e => e.Owned > 0 || e.InDeck > 0))
        {
            SetStatus("Scan some cards first. The optimizer builds from your collection.");
            return;
        }
        SetBusy(true, "Optimizing the deck...");
        try
        {
            _undo = new LibrarySnapshot(Lib);
            var log = await Rules.OptimizeAsync(Lib, Format, CancellationToken.None);
            Save();
            RefreshAll();
            var report = Rules.Analyze(Lib, Format);
            _reportBox.Text = Crlf("Optimizer changes\n" + string.Join("\n", log.Select(l => "  " + l)) + "\n\n" + report);
            SetStatus("Deck optimized. Press Undo optimize to go back.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    void MarkBought()
    {
        var rows = _collectionGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<LibraryEntry>()
                                  .Where(e => e.Loaned > 0).ToList();
        if (rows.Count == 0) { SetStatus("Select a loaner card first."); return; }
        foreach (var e in rows)
        {
            e.Owned += e.Loaned;
            e.Loaned = 0;
            e.LoanNote = "";
        }
        _undo = null;
        Save();
        RefreshAll();
        SetStatus(rows.Count == 1 ? $"{rows[0].Card.Name} is now owned." : $"{rows.Count} loaner cards are now owned.");
    }

    async Task ShowBudgetSwaps()
    {
        if (_busy) return;
        if (Lib.DeckCount == 0) { SetStatus("Build a deck first. Budget swaps look at the cards in your deck."); return; }

        using var dlg = new Form
        {
            Text = "Budget swaps", Width = 860, Height = 560, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, ShowIcon = false, Font = Font,
        };
        var top = Flow();
        var max = new NumericUpDown { Minimum = 0.25m, Maximum = 1000, DecimalPlaces = 2, Increment = 1, Width = 80,
                                      Value = (decimal)Math.Clamp(_data.BudgetLimit, 0.25, 1000), Margin = new Padding(3, 7, 3, 3) };
        var info = new Label { AutoSize = true, Margin = new Padding(12, 10, 3, 3), ForeColor = Color.DimGray };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, GridLines = true };
        list.Columns.Add("Replace", 190);
        list.Columns.Add("Price", 70, HorizontalAlignment.Right);
        list.Columns.Add("With", 190);
        list.Columns.Add("Price", 70, HorizontalAlignment.Right);
        list.Columns.Add("Why", 300);
        // One choice per card: checking an option unchecks the other options for the same card.
        list.ItemChecked += (s, e) =>
        {
            if (!e.Item.Checked || e.Item.Tag is not (BudgetSwap swap, SwapOption)) return;
            foreach (ListViewItem other in list.Items)
                if (other != e.Item && other.Checked && other.Tag is (BudgetSwap os, SwapOption) && os == swap) other.Checked = false;
        };
        var find = new Button { Text = "Find swaps", AutoSize = true, Height = 30 };
        top.Controls.AddRange(new Control[] { Lbl("Max price per card $"), max, find, info });

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
        var apply = new Button { Text = "Apply checked swaps", AutoSize = true, Height = 30, Enabled = false };
        var close = new Button { Text = "Close", AutoSize = true, Height = 30, DialogResult = DialogResult.Cancel };
        bottom.Controls.Add(close);
        bottom.Controls.Add(apply);
        bottom.Controls.Add(new Label { AutoSize = true, Margin = new Padding(3, 9, 12, 0), ForeColor = Color.DimGray,
                                        Text = "Nothing changes until you apply. Swapped-in cards you don't own become loaners." });
        dlg.Controls.Add(list);
        dlg.Controls.Add(bottom);
        dlg.Controls.Add(top);
        dlg.CancelButton = close;

        var game = CurrentGame;
        var lib = Lib;
        string format = Format;
        var rules = Rules;
        List<BudgetSwap> swaps = null;
        var cts = new CancellationTokenSource();
        dlg.FormClosed += (s, e) => cts.Cancel();

        find.Click += async (s, e) =>
        {
            find.Enabled = apply.Enabled = false;
            list.Items.Clear();
            _data.BudgetLimit = (double)max.Value;
            Save();
            try
            {
                swaps = await BudgetAdvisor.SuggestAsync(game, lib, format, rules, Source(game), (double)max.Value,
                    name => { if (!dlg.IsDisposed) dlg.BeginInvoke(() => info.Text = $"Checking {name}..."); }, cts.Token);
                if (dlg.IsDisposed) return;
                list.BeginUpdate();
                foreach (var swap in swaps.OrderByDescending(x => x.BestSaving))
                {
                    if (swap.Options.Count == 0)
                    {
                        var none = list.Items.Add($"{swap.Copies}x {swap.Current.Name}");
                        none.SubItems.Add(Money.Format(swap.Current.PriceUsd));
                        none.SubItems.Add("No cheaper option found");
                        none.SubItems.Add("");
                        none.SubItems.Add("");
                        none.ForeColor = Color.DimGray;
                        continue;
                    }
                    bool first = true;
                    foreach (var o in swap.Options)
                    {
                        var item = list.Items.Add(first ? $"{swap.Copies}x {swap.Current.Name}" : "");
                        item.SubItems.Add(first ? Money.Format(swap.Current.PriceUsd) : "");
                        item.SubItems.Add(o.Card.Name + (o.Card.SetName != "" ? $" ({o.Card.SetName})" : ""));
                        item.SubItems.Add(o.Price > 0 ? Money.Format(o.Price) : "unknown");
                        item.SubItems.Add($"{o.Kind}: {o.Why}");
                        item.Tag = (swap, o);
                        item.ToolTipText = o.Why;
                        first = false;
                    }
                }
                list.EndUpdate();
                int count = swaps.Count(x => x.Options.Count > 0);
                info.Text = swaps.Count == 0 ? $"Every card in the deck is {Money.Format((double)max.Value)} or less, or has no price yet."
                          : $"{count} of {swaps.Count} cards over {Money.Format((double)max.Value)} have cheaper options. Check one option per card.";
                apply.Enabled = count > 0;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!dlg.IsDisposed) info.Text = ex is RateLimitedException ? ex.Message : "Couldn't finish: " + ex.Message; }
            finally { if (!dlg.IsDisposed) find.Enabled = true; }
        };

        apply.Click += (s, e) =>
        {
            var picks = list.CheckedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<(BudgetSwap swap, SwapOption option)>().ToList();
            if (picks.Count == 0) { info.Text = "Check the swaps you want first."; return; }
            _undo = new LibrarySnapshot(lib);
            double before = lib.Deck.Sum(x => x.InDeck * x.Card.PriceUsd);
            var lines = new List<string>();
            foreach (var (swap, option) in picks)
            {
                BudgetAdvisor.ApplySwap(lib, swap, option);
                lines.Add($"  {swap.Copies}x {swap.Current.Name}  ->  {option.Card.Name}" + (option.Price > 0 ? $" ({Money.Format(option.Price)})" : ""));
            }
            double after = lib.Deck.Sum(x => x.InDeck * x.Card.PriceUsd);
            Save();
            RefreshAll();
            var report = rules.Analyze(lib, format);
            _reportBox.Text = Crlf($"Budget swaps applied\n{string.Join("\n", lines)}\n\nDeck value {Money.Format(before)} -> {Money.Format(after)}\n" +
                                   "Cards you don't own were added as loaners (Collection tab, Loaners only).\n\n" + report);
            SetStatus("Budget swaps applied. Press Undo optimize to go back.");
            dlg.Close();
        };

        dlg.Shown += (s, e) => find.PerformClick();
        dlg.ShowDialog(this);
        await Task.CompletedTask;
    }

    async Task ShowLoanerPicks()
    {
        if (_busy) return;
        if (!Lib.Entries.Values.Any(e => e.Loaned > 0))
        {
            MessageBox.Show(this, "You have no loaner cards yet.\n\nTick \"Scan as loaner\" on the Scan tab and scan (or add by name) the cards " +
                                  "you could borrow or buy. Then press Loaner picks to see which ones would improve this deck.",
                            "Loaner picks", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SetBusy(true, "Checking which loaners would improve the deck...");
        LoanerReport report;
        try { report = await LoanerAdvisor.RecommendAsync(Lib, Format, Rules); }
        catch (Exception ex) { ShowError(ex); return; }
        finally { SetBusy(false); }

        _reportBox.Text = Crlf("Loaner picks\n\n" + LoanerAdvisor.Describe(report));
        RefreshAll();
        if (report.Picks.Count == 0) { SetStatus("None of your loaners would improve this deck."); return; }
        if (MessageBox.Show(this, $"Use the deck with these loaners? Deck health {report.HealthOwnedOnly} -> {report.HealthWithLoaners}.\n\n" +
                                  "Your owned cards stay the same. Undo optimize puts the current deck back.",
                            "Loaner picks", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            SetStatus("Loaner picks shown. Nothing was changed.");
            return;
        }
        SetBusy(true, "Building the deck with loaners...");
        try
        {
            _undo = new LibrarySnapshot(Lib);
            await LoanerAdvisor.UseDeckAsync(Lib, Format, Rules);
            Save();
            RefreshAll();
            SetStatus("Deck built with loaners (marked \"loaner\"). Press Undo optimize to go back.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    void UndoOptimize()
    {
        if (_undo == null || !_undo.IsFor(Lib)) { SetStatus("Nothing to undo."); return; }
        int removed = _undo.Restore();
        _undo = null;
        Save();
        RefreshAll();
        Analyze();
        SetStatus(removed > 0 ? $"Restored the previous deck and removed {removed} card(s) the last action had added to your collection." : "Restored the previous deck.");
    }

    void ClearDeck()
    {
        if (Lib.DeckCount == 0) return;
        if (MessageBox.Show(this, "Take every card out of the deck? Your collection stays as it is.", "Clear deck",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _undo = new LibrarySnapshot(Lib);
        foreach (var e in Lib.Entries.Values) e.InDeck = 0;
        Save();
        RefreshAll();
        SetStatus("Deck cleared. Undo optimize brings it back.");
    }

    async Task ImportList()
    {
        if (_busy) return;
        string text = Prompt("Import decklist",
            "Paste a decklist, one card per line. Examples: \"4 Lightning Bolt\", \"3 Arven SVI 166\", \"3x Ash Blossom & Joyous Spring\".");
        if (string.IsNullOrWhiteSpace(text)) return;
        var lines = DeckList.Parse(text);
        if (lines.Count == 0) { SetStatus("No cards found in that list."); return; }

        var choice = MessageBox.Show(this, "Replace the current deck with this list?\n\nYes replaces it. No adds the cards to it.",
                                     "Import decklist", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return;

        SetBusy(true, "Importing...");
        try
        {
            _undo = new LibrarySnapshot(Lib);
            if (choice == DialogResult.Yes) foreach (var e in Lib.Entries.Values) e.InDeck = 0;
            var missing = new List<string>();
            int i = 0;
            foreach (var (qty, name, number) in lines)
            {
                SetStatus($"Looking up {name} ({++i} of {lines.Count})...");
                var c = await Source(CurrentGame).FindAsync(name, number == null ? null : new PrintHint { Number = number });
                if (c == null) { missing.Add(name); continue; }
                Lib.Add(c, 0, qty);
            }
            Save();
            RefreshAll();
            var report = Rules.Analyze(Lib, Format).ToString();
            _reportBox.Text = Crlf((missing.Count > 0 ? "Not found: " + string.Join(", ", missing) + "\n\n" : "") + report);
            SetStatus($"Imported {lines.Count - missing.Count} of {lines.Count} lines. Imported cards count as not owned until you scan them.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    void ExportList()
    {
        if (Lib.DeckCount == 0) { SetStatus("The deck is empty."); return; }
        string text = DeckList.Format(Lib);
        try { Clipboard.SetText(text); } catch { }

        using var dlg = new SaveFileDialog { Filter = "Text file|*.txt", FileName = $"{GameInfo.Short(CurrentGame)} deck.txt", Title = "Save decklist" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            File.WriteAllText(dlg.FileName, text);
            SetStatus($"Saved {Path.GetFileName(dlg.FileName)} and copied the list to the clipboard.");
        }
        else SetStatus("Decklist copied to the clipboard.");
    }

    string Prompt(string title, string message)
    {
        using var f = new Form
        {
            Text = title, Width = 600, Height = 500, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font
        };
        var lbl = new Label { Text = message, Dock = DockStyle.Top, Height = 48, Padding = new Padding(8) };
        var box = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Font = new Font("Consolas", 10f) };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "Import", DialogResult = DialogResult.OK, AutoSize = true };
        bar.Controls.Add(cancel);
        bar.Controls.Add(ok);
        f.Controls.Add(box);
        f.Controls.Add(lbl);
        f.Controls.Add(bar);
        f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    // ------------------------------------------------------------------ helpers

    static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    void SetBusy(bool busy, string message = null)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _scanBtn.Enabled = !busy && _camera.Running;
        _optimizeBtn.Enabled = !busy;
        if (message != null) SetStatus(message);
    }

    void SetStatus(string s) => _status.Text = s;

    void Log(string s) => _scanLog.Items.Insert(0, s);

    void ShowError(Exception ex)
    {
        string msg = ex switch
        {
            RateLimitedException rl => rl.Message,
            HttpRequestException or TaskCanceledException => "Could not reach the card database. Check your internet connection and try again.",
            _ => ex.Message
        };
        SetStatus(msg);
        Log("Error: " + msg);
    }

    void Save()
    {
        try { Storage.Save(_data); }
        catch (Exception ex) { SetStatus("Could not save your collection: " + ex.Message); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F5)
        {
            e.Handled = true;
            _ = ScanFromCamera();
        }
        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _camera.FrameReady -= OnFrame;
        _camera.Dispose();
        Save();
        base.OnFormClosing(e);
    }
}
