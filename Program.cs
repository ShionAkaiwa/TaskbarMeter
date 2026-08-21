using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace TaskbarMeter;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 二重起動防止。すでに動いている場合は「出てきて」と伝えるだけで終了する。
        using var mutex = new Mutex(true, "TaskbarMeter_SingleInstance", out bool isNew);
        var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "TaskbarMeter_ShowRequest");
        if (!isNew)
        {
            showRequest.Set();
            return;
        }

        ApplicationConfiguration.Initialize();

        // 毎秒動く常駐なので、1 回の例外で既定のエラーダイアログが出ると、
        // 原因が続いているあいだ 1 秒ごとにダイアログが積み上がって操作できなくなる。
        // 同じ内容は 1 度だけ見せて、あとは動き続けるほうがましにする。
        string? lastReported = null;
        Application.ThreadException += (_, e) =>
        {
            string message = e.Exception.Message;
            if (message == lastReported) return;
            lastReported = message;
            MessageBox.Show($"エラーが起きましたが、動作は続けます。\n\n{message}",
                            "TaskbarMeter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        Application.Run(new MeterContext(showRequest));
    }
}

/// <summary>表示ルール。</summary>
internal enum DisplayMode
{
    Always = 0,   // 常に表示
    Auto = 1      // 高負荷のときだけ表示
}

/// <summary>アイコンの見た目。</summary>
internal enum IconStyle
{
    Number = 0,   // 数字だけ（シンプル）
    Face = 1,     // 内蔵のドット絵キャラ
    Image = 2     // 好きな画像から作ったドット絵
}

/// <summary>
/// 1回分の測定結果。Ratio は 0.0〜1.0（ゲージ用）、Value と Unit は
/// 記録・CSV 用の実値（% / GB / Mbps）。
/// </summary>
internal readonly record struct Sample(
    double Ratio, string Text, string Tooltip, double Value = 0, string Unit = "%");

/// <summary>
/// アイコンに乗せる「今の状況」。数値そのものではなく、まばたきの位相や
/// 他の指標との関係など、表情を決めるための材料をまとめて渡す。
/// </summary>
/// <param name="Tick">起動からの秒数。まばたきの位相に使う。</param>
/// <param name="Recording">計測中か。</param>
/// <param name="Stage">
/// 使う段階（0〜4）。負の値なら使用率から決める。
/// 段階が変わると体の形ごと変わるので、しきい値をまたいで往復する負荷で
/// 毎秒跳ねないよう、常駐側でヒステリシスを掛けた結果をここで渡す。
/// </param>
/// <param name="Phase">
/// まばたきの位相ずらし。指標ごとに変えて、並んだアイコンが一斉に目を閉じないようにする。
/// </param>
internal readonly record struct IconMood(int Tick = 0, bool Recording = false,
                                         int Stage = -1, int Phase = 0);

// ---------------------------------------------------------------------------
//  本体
// ---------------------------------------------------------------------------

internal sealed class MeterContext : ApplicationContext
{
    private const int CooldownSeconds = 5;         // 高負荷モードで消えるまでの猶予
    private const int PromoteRetrySeconds = 30;    // 「タスクバーに出す」を試し続ける秒数

    private sealed class Slot
    {
        public required Metric Metric { get; init; }
        public required NotifyIcon Notify { get; init; }
        public Icon? Current { get; set; }

        /// <summary>ユーザーが表示 ON にしているか。OFF のあいだは読み取りもしない。</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// いま描いている段階（0〜4）。しきい値をまたいで往復する負荷で
        /// 体の形が毎秒変わらないよう、前回の段階を覚えて引っぱる。-1 は未決定。
        /// </summary>
        public int Stage { get; set; } = -1;

        /// <summary>まばたきの位相ずらし。アイコンが一斉に目を閉じないようにするため。</summary>
        public int Phase { get; init; }

        /// <summary>
        /// 描くのに使う色。毎秒レジストリを読みに行かないよう、設定が変わったときだけ更新する。
        /// </summary>
        public Color Color { get; set; }
    }

    private readonly List<Metric> _metrics = MetricCatalog.CreateAll();
    private readonly Dictionary<string, Slot> _slots = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly HotkeyWindow _hotkey = new();
    private readonly EventWaitHandle _showRequest;
    private ContextMenuStrip _menu = new();

    private readonly ToolStripMenuItem _alwaysItem = new("常に表示");
    private readonly ToolStripMenuItem _autoItem = new("高負荷のときだけ表示");
    private readonly ToolStripMenuItem _recordItem = new("計測を開始");
    private ToolStripMenuItem? _thresholdItem;
    private PixelSkin? _skin = PixelSkin.LoadSaved();
    private bool _skinSmooth = Settings.SkinSmooth;

    private readonly SessionRecorder _recorder = new();
    private Dictionary<string, Sample> _lastSamples = new();
    private bool _gpuBusySeen;
    private int _gpuIdleSeconds;
    private bool _finishNotified;
    private int _vramHighSeconds;
    private bool _vramNotified;

    private DisplayMode _mode = Settings.Mode;
    private int _threshold = Settings.Threshold;
    private IconStyle _style = Settings.Style;

    // 設定画面で変えた内容をメニューのチェックに反映するための集約。
    // 反映中に CheckedChanged が走ると設定を上書きしてしまうので _syncingMenu で止める。
    private Action _syncMenu = () => { };
    private bool _syncingMenu;

    /// <summary>初回セットアップをまだ出していないか。最初のタイマー tick で開く。</summary>
    private bool _pendingSetup;

    /// <summary>exe の再起動で「設定を開いて」と頼まれたか。</summary>
    private bool _pendingSettings;

    /// <summary>設定画面を二重に開かないための目印。</summary>
    private bool _settingsOpen;

    /// <summary>「タスクバーに出す」を残り何回試すか。成功したら 0 にする。</summary>
    private int _promoteTries;

    private bool _forceShow;
    private bool _forceHide;
    private int _cooldown;
    private bool _iconsVisible = true;
    private int _hintCountdown;
    private int _tick;

    /// <summary>出せるようになるまで持ち越す知らせ。起動直後はアイコンがまだ出ていない。</summary>
    private string? _pendingNotice;

    public MeterContext(EventWaitHandle showRequest)
    {
        _showRequest = showRequest;
        BuildMenu();
        CreateSlots();

        _hotkey.Pressed += ToggleVisibility;

        _timer.Interval = 1000;
        _timer.Tick += (_, _) =>
        {
            // セットアップ／設定画面はここで開く。コンストラクタ（= Application.Run の前）で
            // モーダルを出すと動作が不安定なので、メッセージループが回り始めてからにする。
            //
            // Update() より先に見ているのが要点。初回起動では GPU のカウンタを組み立てるのに
            // 1 秒近くかかり、そのあいだ画面には何も出ない。先に案内画面を出しておけば、
            // 待たされている感じがなくなる。アイコンの更新はこの画面を開いたまま続く。
            if (_pendingSetup)
            {
                _pendingSetup = false;

                // 見失わないための保険は初回に先回りして作っておく。
                // 設定画面のチェックはこの結果をそのまま映すので、表示と実態がずれない。
                StartMenuShortcut.Create();

                // 初回はタスクバーへの表示も既定で有効にする。
                // ③ のボタンを押さないと一度も Promote が走らず、
                // アイコンはオーバーフロー（∧ の中）に入ったままになる。
                // 配布でいちばんつまずくのがここなので、押し忘れに任せない。
                Settings.PromoteTray = true;
                _promoteTries = PromoteRetrySeconds;

                ShowSetup(firstRun: true);
            }
            else if (_pendingSettings)
            {
                _pendingSettings = false;
                ShowSetup(firstRun: false);
            }
            else if (_promoteTries > 0 && _tick >= 2)
            {
                // 「タスクバーに出す」は Windows 側にエントリができてからでないと書けない。
                // エントリはアイコンを出してから数秒遅れて作られることがあり、
                // 一度きりで試すと早すぎて空振りし、そのまま二度と表に出てこない。
                // 成功するまで数十秒のあいだ試し続ける。
                if (TrayPromotion.Promote()) _promoteTries = 0;
                else _promoteTries--;
            }

            Update();
        };

        _pendingSetup = !Settings.SetupDone;

        // Windows 側の「タスクバーに出す」の記録は、exe を動かしたり explorer が
        // 作り直したりすると消える。希望が保存されているなら起動のたびに掛け直す。
        // 設定にはそう書いてあったが、実際には設定画面を開いたときしか掛かっていなかった。
        if (Settings.PromoteTray) _promoteTries = PromoteRetrySeconds;

        // exe を別の場所へ移すと、スタートメニューのショートカットも自動起動も
        // 消えた場所を指したまま残る。設定画面のチェックは付いたままなので、
        // 使う側からは「設定したのに効かない」としか見えない。黙って貼り直す。
        if (StartMenuShortcut.Exists && !StartMenuShortcut.PointsHere()) StartMenuShortcut.Create();
        if (AutoStart.IsEnabled && !AutoStart.PointsHere()) AutoStart.Set(true);

        _timer.Start();

        // 初回はここで計測を始めない。カウンタの組み立てに時間がかかり、
        // その間なにも出ないまま待たされるため、先に案内画面を出す。
        if (!_pendingSetup) Update();

        if (!_hotkey.Registered)
        {
            _autoItem.Enabled = false;
            if (_mode == DisplayMode.Auto) SetMode(DisplayMode.Always);

            // ここで直接出しても届かない。アイコンにまだ絵が入っておらず、
            // シェルに登録されていないのでバルーンは捨てられる。出せるまで持ち越す。
            _pendingNotice = "Ctrl+Alt+M が他のアプリと競合しており登録できませんでした。" +
                             "「高負荷のときだけ表示」は無効にしています。";
        }
    }

    // ----- メニュー -------------------------------------------------------

    private void BuildMenu()
    {
        _menu = new ContextMenuStrip();

        // 迷ったらここ、という入口を先頭に置く
        var setup = new ToolStripMenuItem("設定…")
        {
            Font = new Font(_menu.Font, FontStyle.Bold)
        };
        setup.Click += (_, _) => ShowSetup(firstRun: false);
        _menu.Items.Add(setup);
        _menu.Items.Add(new ToolStripSeparator());

        _alwaysItem.Click += (_, _) => SetMode(DisplayMode.Always);
        _autoItem.Click += (_, _) => SetMode(DisplayMode.Auto);
        _menu.Items.Add(_alwaysItem);
        _menu.Items.Add(_autoItem);
        _menu.Items.Add(BuildThresholdMenu());

        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(BuildMetricMenu());
        _menu.Items.Add(BuildColorMenu());
        _menu.Items.Add(BuildStyleMenu());

        _menu.Items.Add(new ToolStripSeparator());

        _recordItem.Click += (_, _) => ToggleRecording();
        _menu.Items.Add(_recordItem);

        _menu.Items.Add(new ToolStripSeparator());

        var hide = new ToolStripMenuItem("今すぐ隠す  (Ctrl+Alt+M)");
        hide.Click += (_, _) => ToggleVisibility();
        _menu.Items.Add(hide);

        var autoStart = new ToolStripMenuItem("Windows 起動時に自動実行")
        {
            CheckOnClick = true,
            Checked = AutoStart.IsEnabled
        };
        autoStart.CheckedChanged += (_, _) =>
        {
            if (_syncingMenu) return;
            AutoStart.Set(autoStart.Checked);
        };
        _syncMenu += () => autoStart.Checked = AutoStart.IsEnabled;
        _menu.Items.Add(autoStart);

        var taskManager = new ToolStripMenuItem("タスク マネージャーを開く");
        taskManager.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
            catch { /* 起動できなくても無視 */ }
        };
        _menu.Items.Add(taskManager);

        _menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("終了");
        exit.Click += (_, _) => ConfirmQuit();
        _menu.Items.Add(exit);

        RefreshModeChecks();
    }

    private ToolStripMenuItem BuildThresholdMenu()
    {
        var root = new ToolStripMenuItem("表示に切り替わる負荷");
        _thresholdItem = root;
        foreach (int value in new[] { 5, 10, 20, 30, 50 })
        {
            int captured = value;
            var item = new ToolStripMenuItem($"{value}% 以上") { Checked = _threshold == value };
            item.Click += (_, _) =>
            {
                _threshold = captured;
                Settings.Threshold = captured;
                foreach (ToolStripMenuItem sibling in root.DropDownItems)
                    sibling.Checked = sibling == item;
            };
            root.DropDownItems.Add(item);
        }
        return root;
    }

    private ToolStripMenuItem BuildMetricMenu()
    {
        var root = new ToolStripMenuItem("表示する項目");
        foreach (Metric metric in _metrics)
        {
            Metric captured = metric;
            var item = new ToolStripMenuItem(metric.Name)
            {
                CheckOnClick = true,
                Checked = Settings.MetricEnabled(metric.Id, metric.DefaultEnabled)
            };
            item.CheckedChanged += (_, _) =>
            {
                if (_syncingMenu) return;

                // 全部消すとメニューに触れなくなるので、最後の1つは外させない
                if (!item.Checked && EnabledCount <= 1)
                {
                    item.Checked = true;
                    Notify("最低 1 つは表示したままにしてください。", ToolTipIcon.Info);
                    return;
                }
                Settings.SetMetricEnabled(captured.Id, item.Checked);
                Reapply();
            };
            _syncMenu += () => item.Checked =
                Settings.MetricEnabled(captured.Id, captured.DefaultEnabled);
            root.DropDownItems.Add(item);
        }
        return root;
    }

    private ToolStripMenuItem BuildColorMenu()
    {
        var root = new ToolStripMenuItem("色");
        foreach (Metric metric in _metrics)
        {
            Metric captured = metric;
            var item = new ToolStripMenuItem(metric.Name);
            item.Click += (_, _) =>
            {
                using var dialog = new ColorDialog
                {
                    Color = Settings.MetricColor(captured.Id, captured.DefaultColor),
                    FullOpen = true,
                    AnyColor = true
                };
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    Settings.SetMetricColor(captured.Id, dialog.Color);
                    Reapply();
                }
            };
            root.DropDownItems.Add(item);
        }

        root.DropDownItems.Add(new ToolStripSeparator());
        var reset = new ToolStripMenuItem("すべて既定の色に戻す");
        reset.Click += (_, _) =>
        {
            foreach (Metric metric in _metrics) Settings.ClearMetricColor(metric.Id);
            Reapply();
        };
        root.DropDownItems.Add(reset);
        return root;
    }

    private ToolStripMenuItem BuildStyleMenu()
    {
        var root = new ToolStripMenuItem("見た目");
        var number = new ToolStripMenuItem("数字のみ");
        var face = new ToolStripMenuItem("ドット絵キャラ（内蔵）");
        var image = new ToolStripMenuItem("好きな画像のドット絵");
        var choose = new ToolStripMenuItem("画像を選んで作る…");

        void RefreshChecks()
        {
            number.Checked = _style == IconStyle.Number;
            face.Checked = _style == IconStyle.Face;
            image.Checked = _style == IconStyle.Image;
            image.Enabled = _skin is not null;
        }

        void Apply(IconStyle style)
        {
            if (style == IconStyle.Image && _skin is null)
            {
                ChooseImage();
                return;
            }
            _style = style;
            Settings.Style = style;
            RefreshChecks();
            Reapply();
        }

        number.Click += (_, _) => Apply(IconStyle.Number);
        face.Click += (_, _) => Apply(IconStyle.Face);
        image.Click += (_, _) => Apply(IconStyle.Image);
        choose.Click += (_, _) => { ChooseImage(); RefreshChecks(); };

        root.DropDownItems.Add(number);
        root.DropDownItems.Add(face);
        root.DropDownItems.Add(image);
        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add(choose);

        _syncMenu += RefreshChecks;
        RefreshChecks();
        return root;
    }

    // ----- 設定画面 -------------------------------------------------------

    /// <summary>
    /// セットアップ／設定画面を開く。変更はその場で保存されるので、
    /// 閉じたあとに常駐側の状態を設定から読み直して合わせる。
    /// </summary>
    private void ShowSetup(bool firstRun)
    {
        if (_settingsOpen) return;

        _settingsOpen = true;
        try
        {
            using var form = new SetupForm(_metrics, firstRun, ReloadFromSettings,
                                           () => _skin, ChooseImage);
            form.ShowDialog();
        }
        finally
        {
            _settingsOpen = false;
        }

        Settings.SetupDone = true;

        // exe をもう一度起動して「出てきて」を使ったときの強制表示を、ここで下ろす。
        // 下ろさないと「高負荷のときだけ表示」を選んでいても出っぱなしになり、
        // 設定が効いていないように見える。しばらくは _cooldown が出したままにしてくれる。
        _forceShow = false;
        _cooldown = CooldownSeconds;

        ReloadFromSettings();
    }

    /// <summary>設定画面で変わった内容を常駐側に反映する。</summary>
    private void ReloadFromSettings()
    {
        _mode = Settings.Mode;
        _threshold = Settings.Threshold;
        _style = Settings.Style;
        _skinSmooth = Settings.SkinSmooth;
        if (_style == IconStyle.Image && _skin is null) _skin = PixelSkin.LoadSaved();

        // 「タスクバーに出す」を押した直後はまだエントリが無いことがあるので、
        // ここでも試行回数を積み直しておく
        if (Settings.PromoteTray) _promoteTries = Math.Max(_promoteTries, PromoteRetrySeconds);

        RefreshModeChecks();

        _syncingMenu = true;
        try { _syncMenu(); }
        finally { _syncingMenu = false; }

        Reapply();
    }

    /// <summary>画像を選び、ドット絵に変換してプレビューし、採用されたら保存する。</summary>
    private void ChooseImage()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "ドット絵にする画像を選ぶ",
            // webp は載せないこと。GDI+ にデコーダが無く、選ばせておいて
            // 「パラメーターが正しくありません」で落ちる
            Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.gif|すべてのファイル|*.*"
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        try
        {
            using var preview = new SkinPreviewForm(dialog.FileName);
            if (preview.ShowDialog() != DialogResult.OK || preview.Result is null) return;

            preview.Result.SaveAsDefault();
            Settings.SkinSmooth = preview.Smooth;
            _skinSmooth = preview.Smooth;
            _skin?.Dispose();
            _skin = preview.Result;
            _style = IconStyle.Image;
            Settings.Style = IconStyle.Image;
            Reapply();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"画像を読み込めませんでした。\n{ex.Message}", "TaskbarMeter",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ----- 表示するアイコンの増減 ----------------------------------------

    /// <summary>
    /// トレイアイコンの器を、指標ぶん最初にまとめて作る。
    ///
    /// **表示の ON/OFF で NotifyIcon を作り直してはいけない。** Windows はトレイアイコンを
    /// 「exe のパス + 通し番号」で識別していて、WinForms はその番号を NotifyIcon を作った順に
    /// 振る。作り直すと番号がずれ、`HKCU\Control Panel\NotifyIconSettings` に書いた
    /// 「タスクバーに出す」(IsPromoted) が別のエントリに置き去りになって、
    /// アイコンがまたオーバーフローへ引っ込んでしまう。
    ///
    /// 最初に全部を同じ順で作っておけば、番号は指標ごとに毎回同じになり、設定が残り続ける。
    /// </summary>
    private void CreateSlots()
    {
        foreach (Metric metric in _metrics)
        {
            var notify = new NotifyIcon
            {
                ContextMenuStrip = _menu,
                Text = metric.Name
            };
            // 位相は指標ごとに固定でずらす。全部が同じ秒に目を閉じると機械に見える。
            _slots[metric.Id] = new Slot { Metric = metric, Notify = notify, Phase = _slots.Count * 3 };
        }
        SyncSlots();
    }

    /// <summary>どの指標を表示するかを設定から読み直す。器は作り直さず表示だけ切り替える。</summary>
    private void SyncSlots()
    {
        foreach (Slot slot in _slots.Values)
        {
            slot.Enabled = Settings.MetricEnabled(slot.Metric.Id, slot.Metric.DefaultEnabled);
            slot.Color = Settings.MetricColor(slot.Metric.Id, slot.Metric.DefaultColor);
            slot.Notify.Visible = slot.Enabled && _iconsVisible;
        }

        // 後から表示に加えた指標は、Windows 側のエントリがこのとき初めて作られる。
        // 作られたては「オーバーフローに入れる」状態なので、掛け直しを積んでおく。
        if (Settings.PromoteTray) _promoteTries = Math.Max(_promoteTries, PromoteRetrySeconds);
    }

    private IEnumerable<Slot> ActiveSlots => _slots.Values.Where(s => s.Enabled);

    private int EnabledCount => _slots.Values.Count(s => s.Enabled);

    // ----- 毎秒の更新 -----------------------------------------------------

    /// <summary>
    /// 1 秒ごとの本体。指標を読み、記録し、アイコンを描き直す。
    ///
    /// **タイマーからだけ呼ぶこと。** 設定を変えた直後の反映は Apply() を使う。
    /// 指標の多くは「前回読んだときからの差」で値を出しているので、短い間隔で
    /// 呼ぶと差が取れずに 0% になる。メニューを触っている間ずっと CPU が 0% に
    /// 見えていたのはこれが原因だった。まばたきの位相もここで進む。
    /// </summary>
    private void Update()
    {
        // 記録中は、表示を切った指標も読み続ける。読まないと記録に 0 が並び、
        // グラフと平均・最小が実際より低く出る。
        List<Slot> reading = _slots.Values
            .Where(slot => slot.Enabled ||
                           (_recorder.Recording && _recorder.Tracks(slot.Metric.Id)))
            .ToList();

        if (_tick++ % 5 == 0)
            foreach (Slot slot in reading) slot.Metric.Refresh();

        // exe をもう一度起動した場合の「出てきて」要求。
        // わざわざ起動し直したということは見失っている可能性が高いので、
        // アイコンを出すだけでなく設定画面も開いて「ここにいる」と分かるようにする。
        if (_showRequest.WaitOne(0))
        {
            _forceShow = true;
            _forceHide = false;
            _pendingSettings = true;
        }

        var samples = new Dictionary<string, Sample>();
        foreach (Slot slot in reading)
        {
            // 1 つの指標が投げても、他の指標とアプリ自体は生かす。
            // ここで落ちるとタイマーごと止まり、右クリックする先も無くなる。
            try { samples[slot.Metric.Id] = slot.Metric.Read(); }
            catch { samples[slot.Metric.Id] = new Sample(0, "--", $"{slot.Metric.Name} 取得不可"); }
        }

        double peak = 0;
        foreach (Slot slot in ActiveSlots)
            if (samples.TryGetValue(slot.Metric.Id, out Sample shown))
                peak = Math.Max(peak, shown.Ratio);

        _lastSamples = samples;
        WatchVramPressure(samples);
        if (_recorder.Recording)
        {
            _recorder.Add(DateTime.Now, samples);
            WatchForFinish(samples);
        }

        if (peak * 100 >= _threshold) _cooldown = CooldownSeconds;
        else if (_cooldown > 0) _cooldown--;

        SetVisible(ShouldShow);
        Redraw();

        if (_pendingNotice is not null && Notify(_pendingNotice, ToolTipIcon.Warning))
            _pendingNotice = null;
    }

    private bool ShouldShow => _forceShow
                               || (!_forceHide && (_mode == DisplayMode.Always || _cooldown > 0));

    /// <summary>
    /// 設定を読み直して、いまの値のままアイコンを出し直す。
    /// メニューや設定画面を操作した直後の反映はこちら。
    /// </summary>
    private void Reapply()
    {
        SyncSlots();
        SetVisible(ShouldShow);
        Redraw();
    }

    /// <summary>直前に読んだ値でアイコンを描き直す。指標は読み直さない。</summary>
    private void Redraw()
    {
        if (!_iconsVisible) return;

        foreach (Slot slot in ActiveSlots)
        {
            if (!_lastSamples.TryGetValue(slot.Metric.Id, out Sample sample)) continue;
            try { DrawSlot(slot, sample); }
            catch { /* 1 つ描けなくても残りは描く。常駐が止まらないことを優先する */ }
        }
    }

    private void DrawSlot(Slot slot, Sample sample)
    {
        int percent = (int)Math.Round(Math.Clamp(sample.Ratio, 0, 1) * 100);

        // 段階は前回の値から引っぱって決める。境目に張り付いた負荷で
        // 体の形が毎秒変わると、震えているように見えてしまう。
        slot.Stage = IconFactory.NextStage(slot.Stage, percent);

        IconStyle style = _style == IconStyle.Image && _skin is null ? IconStyle.Face : _style;
        var mood = new IconMood(_tick, _recorder.Recording, slot.Stage, slot.Phase);

        Icon fresh = IconFactory.Create(sample.Ratio, sample.Text, slot.Color, style,
                                        mood, _skin, _skinSmooth);
        slot.Notify.Icon = fresh;
        slot.Current?.Dispose();
        slot.Current = fresh;

        slot.Notify.Text = sample.Tooltip;
    }

    // ----- 計測セッション -------------------------------------------------

    private void ToggleRecording()
    {
        if (_recorder.Recording)
        {
            SessionResult result = _recorder.Stop();
            _recordItem.Text = "計測を開始";

            if (result.Times.Count < 2)
            {
                Notify("記録が短すぎたので結果は表示しません。", ToolTipIcon.Info);
                return;
            }
            new SessionResultForm(result).Show();
            return;
        }

        var tracked = ActiveSlots
            .Select(slot => new TrackedMetric(
                slot.Metric.Id,
                slot.Metric.Name,
                Settings.MetricColor(slot.Metric.Id, slot.Metric.DefaultColor),
                _lastSamples.TryGetValue(slot.Metric.Id, out Sample s) ? s.Unit : "%"))
            .ToList();

        _recorder.Start(tracked);
        _recordItem.Text = "計測を停止して結果を見る";
        _gpuBusySeen = false;
        _gpuIdleSeconds = 0;
        _finishNotified = false;
        Notify("計測を開始しました。停止するまで記録し続けます。", ToolTipIcon.Info);
    }

    /// <summary>
    /// VRAM が上限に近づいたら知らせる。
    /// 学習中の OOM は落ちてから気づくことが多く、数十秒でも前に分かれば
    /// バッチサイズを下げて回し直せる。VRAM を表示している人にだけ出す。
    ///
    /// 総容量が読めなかった環境では Ratio が「観測した最大値ぶんの現在値」になり、
    /// 更新のたびに 100% へ張り付いてしまうので、その場合は何もしない。
    /// </summary>
    private void WatchVramPressure(Dictionary<string, Sample> samples)
    {
        if (!samples.TryGetValue("vram", out Sample vram) ||
            !_slots.TryGetValue("vram", out Slot? slot) ||
            slot.Metric is not VramMetric { TotalKnown: true })
        {
            _vramHighSeconds = 0;
            return;
        }

        if (vram.Ratio >= 0.90)
        {
            // 一瞬の跳ねで鳴らさないよう、続いたことを確かめてから出す
            _vramHighSeconds++;
            if (_vramHighSeconds >= 5 && !_vramNotified)
            {
                // 出せなかったら知らせたことにしない。次の tick でまた試す。
                _vramNotified = Notify(
                    $"VRAM が {(int)Math.Round(vram.Ratio * 100)}% です。" +
                    "このまま増えると学習が落ちるかもしれません。",
                    ToolTipIcon.Warning, "VRAM がいっぱいです");
            }
            return;
        }

        _vramHighSeconds = 0;
        // 90% 付近で出たり入ったりするたびに鳴らさないよう、十分下がってから鳴り直す
        if (vram.Ratio < 0.80) _vramNotified = false;
    }

    /// <summary>
    /// 一度 GPU が回ったあと、しばらくほぼ 0% が続いたら「終わったかも」と知らせる。
    /// 席を外している間にジョブが終わった／落ちたのに気づけるようにするための見守り。
    /// </summary>
    private void WatchForFinish(Dictionary<string, Sample> samples)
    {
        double gpu = samples
            .Where(kv => kv.Key.StartsWith("gpu", StringComparison.Ordinal))
            .Select(kv => kv.Value.Ratio)
            .DefaultIfEmpty(0)
            .Max();

        if (gpu >= 0.30)
        {
            _gpuBusySeen = true;
            _gpuIdleSeconds = 0;
            return;
        }

        if (!_gpuBusySeen || gpu >= 0.05)
        {
            _gpuIdleSeconds = 0;
            return;
        }

        _gpuIdleSeconds++;
        if (_gpuIdleSeconds >= 180 && !_finishNotified)
        {
            _finishNotified = Notify(
                "GPU が 3 分間ほとんど動いていません。処理が終わったかもしれません。",
                ToolTipIcon.Info, "TaskbarMeter");
        }
    }

    // ----- 表示・非表示 ---------------------------------------------------

    private void SetMode(DisplayMode mode)
    {
        _mode = mode;
        Settings.Mode = mode;
        _forceShow = false;
        _forceHide = false;
        _cooldown = CooldownSeconds;
        RefreshModeChecks();
        Reapply();
    }

    private void RefreshModeChecks()
    {
        _alwaysItem.Checked = _mode == DisplayMode.Always;
        _autoItem.Checked = _mode == DisplayMode.Auto;

        // 「常に表示」中は関係のない設定なので触れないようにしておく
        if (_thresholdItem is not null) _thresholdItem.Enabled = _mode == DisplayMode.Auto;
    }

    private void ToggleVisibility()
    {
        // 「今見えているかどうか」を基準に反転する。隠れている最中に押せば必ず出る。
        if (_iconsVisible)
        {
            _forceShow = false;
            _forceHide = true;
        }
        else
        {
            _forceShow = true;
            _forceHide = false;
            _cooldown = CooldownSeconds;
        }
        Reapply();
    }

    private void SetVisible(bool visible)
    {
        if (visible == _iconsVisible && _hintCountdown == 0) return;

        if (!visible && !Settings.HintShown && _hintCountdown == 0)
        {
            // いきなり消えると戻し方が分からなくなるので、一度だけ案内してから隠す
            Notify("Ctrl+Alt+M でいつでも呼び戻せます。", ToolTipIcon.Info, "TaskbarMeter を隠します");
            _hintCountdown = 4;
            return;
        }

        if (!visible && _hintCountdown > 0)
        {
            _hintCountdown--;
            if (_hintCountdown > 0) return;
            Settings.HintShown = true;
        }

        _iconsVisible = visible;
        foreach (Slot slot in _slots.Values) slot.Notify.Visible = visible && slot.Enabled;
    }

    /// <summary>
    /// バルーンで知らせる。出せたときだけ true。
    ///
    /// アイコンが隠れている（Ctrl+Alt+M、または「高負荷のときだけ表示」で引っ込んでいる）
    /// あいだ、ShowBalloonTip は何も言わずに捨てられる。呼んだ側が「知らせた」ことに
    /// してしまうと、VRAM の警告がいちばん気づきたい状況（席を外していてアイコンを
    /// 隠している）で消える。出せたかどうかを返して、呼んだ側が判断できるようにする。
    /// </summary>
    private bool Notify(string message, ToolTipIcon icon, string title = "TaskbarMeter")
    {
        Slot? shown = ActiveSlots.FirstOrDefault(slot => slot.Notify.Visible);
        if (shown is null) return false;

        shown.Notify.ShowBalloonTip(5000, title, message, icon);
        return true;
    }

    /// <summary>
    /// 終了する前に、戻しかたを見せる。
    /// 終了するとタスクバーから消え、Ctrl+Alt+M も効かなくなる（常駐していないので当然）。
    /// exe の置き場所を覚えていないと戻せなくなるため、ここで道筋を示しておく。
    /// </summary>
    private void ConfirmQuit()
    {
        string howToReturn = StartMenuShortcut.Exists
            ? "また使うときは、スタートメニューで「TaskbarMeter」と検索してください。"
            : $"また使うときは、この場所の exe をダブルクリックしてください。\n\n{Environment.ProcessPath}";

        // 既定を「キャンセル」にしておく。Enter を押しただけで常駐が落ちると、
        // 戻しかたを知らない人がいちばん困る。
        DialogResult answer = MessageBox.Show(
            $"TaskbarMeter を終了します。\n\n{howToReturn}",
            "TaskbarMeter", MessageBoxButtons.OKCancel, MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2);

        if (answer == DialogResult.OK) Quit();
    }

    private void Quit()
    {
        _timer.Stop();
        foreach (Slot slot in _slots.Values) slot.Notify.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _hotkey.Dispose();
            foreach (Slot slot in _slots.Values)
            {
                slot.Notify.Dispose();
                slot.Current?.Dispose();
            }
            foreach (Metric metric in _metrics) metric.Dispose();
            _skin?.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}

// ---------------------------------------------------------------------------
//  指標
// ---------------------------------------------------------------------------

internal abstract class Metric : IDisposable
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract Color DefaultColor { get; }
    public virtual bool DefaultEnabled => false;

    /// <summary>
    /// 設定画面の一覧など、幅が限られる場所で使う短い名前。
    /// 名前が長くない指標は何も書かなくてよい（既定で Name をそのまま使う）。
    /// </summary>
    public virtual string ShortName => Name;

    /// <summary>数秒に一度呼ばれる。カウンタの張り替えが必要な指標だけ実装する。</summary>
    public virtual void Refresh() { }

    public abstract Sample Read();

    public virtual void Dispose() { }

    /// <summary>
    /// 0〜100 の整数文字列。100 もそのまま返す
    /// （アイコン側が 3 桁を細い字形で描けるようになったため丸めない）。
    /// </summary>
    protected static string Percent(double ratio)
        => ((int)Math.Round(Math.Clamp(ratio, 0, 1) * 100)).ToString();

    /// <summary>アイコンに収まる短い数値表記。</summary>
    protected static string Compact(double value) => value switch
    {
        >= 100 => "99+",
        >= 10 => Math.Round(value).ToString("0"),
        _ => value.ToString("0.0")
    };
}

internal static class MetricCatalog
{
    public static List<Metric> CreateAll() =>
    [
        new CpuMetric(),
        new RamMetric(),
        new GpuMetric(),
        new GpuComputeMetric(),
        new VramMetric(),
        new DiskMetric(),
        new NetworkMetric(received: true),
        new NetworkMetric(received: false)
    ];
}

/// <summary>CPU。GetSystemTimes なので言語設定・管理者権限に影響されない。</summary>
internal sealed class CpuMetric : Metric
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private long _idle, _kernel, _user;
    private bool _primed;

    public override string Id => "cpu";
    public override string Name => "CPU 使用率";
    public override Color DefaultColor => Color.FromArgb(23, 156, 224);
    public override bool DefaultEnabled => true;

    public override Sample Read()
    {
        double ratio = 0;
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            if (!_primed)
            {
                (_idle, _kernel, _user, _primed) = (idle, kernel, user, true);
            }
            else
            {
                long dIdle = idle - _idle;
                long total = (kernel - _kernel) + (user - _user);   // kernel は idle を含む
                (_idle, _kernel, _user) = (idle, kernel, user);
                if (total > 0) ratio = Math.Clamp((total - dIdle) / (double)total, 0, 1);
            }
        }
        return new Sample(ratio, Percent(ratio), $"CPU {Percent(ratio)}%", ratio * 100, "%");
    }
}

/// <summary>物理メモリ使用率。GlobalMemoryStatusEx を直接叩く。</summary>
internal sealed class RamMetric : Metric
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    public override string Id => "ram";
    public override string Name => "メモリ使用率";
    public override Color DefaultColor => Color.FromArgb(161, 83, 222);

    public override Sample Read()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return new Sample(0, "--", "メモリ 取得不可");

        double ratio = status.dwMemoryLoad / 100.0;
        double usedGb = (status.ullTotalPhys - status.ullAvailPhys) / 1073741824.0;
        double totalGb = status.ullTotalPhys / 1073741824.0;
        return new Sample(ratio, Percent(ratio),
            $"メモリ {Percent(ratio)}%  ({usedGb:0.0} / {totalGb:0.0} GB)", ratio * 100, "%");
    }
}

/// <summary>パフォーマンスカウンタを合算する指標の共通処理。</summary>
internal abstract class CounterMetric : Metric
{
    /// <summary>読めなくなったあと、掛け直すまでの待ち時間。</summary>
    private const int RetryDelayMs = 60_000;

    private readonly Dictionary<string, PerformanceCounter> _counters = new();
    private bool _built;
    private long _retryAt;

    protected bool Available { get; private set; } = true;
    protected abstract string CategoryEnglish { get; }
    protected abstract string CounterEnglish { get; }

    /// <summary>インスタンスが頻繁に入れ替わるなら true（GPU はプロセス単位のため）。</summary>
    protected virtual bool Volatile => false;

    protected virtual bool Match(string instance) => true;

    /// <summary>合算ではなく最大値を採用する場合は true。</summary>
    protected virtual bool UseMax => false;

    public override void Refresh()
    {
        // 一度失敗しても諦めきらない。スリープからの復帰直後、ユーザーの切り替え、
        // GPU ドライバの更新中などはカウンタが一時的に読めなくなる。
        // 諦めたままだと、再起動するまでその指標が「取得不可」で固まる。
        if (!Available)
        {
            if (Environment.TickCount64 < _retryAt) return;
            Available = true;
        }

        if (_built && !Volatile) return;

        try
        {
            var category = new PerformanceCounterCategory(PerfNames.Localize(CategoryEnglish));
            var live = new HashSet<string>(category.GetInstanceNames().Where(Match));
            string counterName = PerfNames.Localize(CounterEnglish);

            // 生き残っているカウンタは作り直さないこと。
            // これらは「前回読んだ時点との差」で値を出すので、作り直すと基準が消え、
            // 直後の NextValue() は差がほぼ 0 の区間を見て 0 を返す。
            // 全部作り直していたころは、張り替えのたびに GPU が 0% に見えていた
            // （5 秒ごとに表情が「限界」から「うとうと」に落ちる形で出ていた）。
            foreach (string gone in _counters.Keys.Where(name => !live.Contains(name)).ToList())
            {
                _counters[gone].Dispose();
                _counters.Remove(gone);
            }

            foreach (string name in live)
            {
                if (_counters.ContainsKey(name)) continue;
                var counter = new PerformanceCounter(category.CategoryName, counterName, name,
                                                     readOnly: true);
                try { counter.NextValue(); } catch { /* 消えたインスタンスは無視 */ }
                _counters[name] = counter;
            }

            _built = true;
        }
        catch
        {
            Available = false;
            _retryAt = Environment.TickCount64 + RetryDelayMs;
        }
    }

    /// <summary>
    /// カウンタを合算する。1 つも読めなければ false を返す。
    /// 「本当に 0%」と「読めなくて 0」を区別しないと、見守り通知が
    /// 動いている最中に「終わったかも」と言い出す。
    /// </summary>
    protected bool TryCollect(out double total)
    {
        if (!_built) Refresh();

        total = 0;
        int read = 0;
        foreach (PerformanceCounter counter in _counters.Values)
        {
            try
            {
                float value = counter.NextValue();
                total = UseMax ? Math.Max(total, value) : total + value;
                read++;
            }
            catch { /* 消えたインスタンスは無視 */ }
        }
        return read > 0;
    }

    public override void Dispose()
    {
        foreach (PerformanceCounter counter in _counters.Values) counter.Dispose();
        _counters.Clear();
    }
}

/// <summary>GPU の 3D エンジン。ゲームや描画の負荷はここに出る。</summary>
internal sealed class GpuMetric : CounterMetric
{
    public override string Id => "gpu3d";
    public override string Name => "GPU 使用率 (3D)";
    public override Color DefaultColor => Color.FromArgb(196, 106, 190);
    public override bool DefaultEnabled => true;

    protected override string CategoryEnglish => "GPU Engine";
    protected override string CounterEnglish => "Utilization Percentage";
    protected override bool Volatile => true;
    protected override bool Match(string instance)
        => instance.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase);

    public override Sample Read()
    {
        if (!TryCollect(out double used)) return new Sample(0, "--", "GPU 3D 取得不可");
        double ratio = Math.Clamp(used / 100.0, 0, 1);
        return new Sample(ratio, Percent(ratio), $"GPU 3D {Percent(ratio)}%", ratio * 100, "%");
    }
}

/// <summary>
/// GPU の Compute エンジン。CUDA / DirectML など、深層学習の負荷はここに出る。
/// 学習中に「3D が 0% なのに実際は回っている」場合はこちらを見る。
/// </summary>
internal sealed class GpuComputeMetric : CounterMetric
{
    public override string Id => "gpucompute";
    public override string Name => "GPU 使用率 (Compute / 学習)";
    public override string ShortName => "GPU 使用率 (学習)";
    public override Color DefaultColor => Color.FromArgb(232, 150, 60);

    protected override string CategoryEnglish => "GPU Engine";
    protected override string CounterEnglish => "Utilization Percentage";
    protected override bool Volatile => true;
    protected override bool Match(string instance)
        => instance.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase)
        || instance.Contains("engtype_Cuda", StringComparison.OrdinalIgnoreCase);

    public override Sample Read()
    {
        if (!TryCollect(out double busy)) return new Sample(0, "--", "GPU Compute 取得不可");
        double ratio = Math.Clamp(busy / 100.0, 0, 1);
        return new Sample(ratio, Percent(ratio), $"GPU Compute {Percent(ratio)}%", ratio * 100, "%");
    }
}

/// <summary>専用 VRAM の使用量。学習中の OOM の予兆を掴むための指標。</summary>
internal sealed class VramMetric : CounterMetric
{
    private readonly double _totalBytes = ReadTotalVram();
    private double _observedMax = 1073741824.0;   // 総量が取れないとき用の暫定上限

    public override string Id => "vram";
    public override string Name => "VRAM 使用量";
    public override Color DefaultColor => Color.FromArgb(107, 209, 192);

    /// <summary>
    /// 総容量が読めたか。読めていないと Ratio は「観測した最大値ぶんの現在値」になり、
    /// 逼迫の判定には使えない。
    /// </summary>
    public bool TotalKnown => _totalBytes > 0;

    protected override string CategoryEnglish => "GPU Adapter Memory";
    protected override string CounterEnglish => "Dedicated Usage";
    protected override bool UseMax => true;   // アダプタごとに出るので合算しない

    public override Sample Read()
    {
        if (!TryCollect(out double used)) return new Sample(0, "--", "VRAM 取得不可");

        double usedGb = used / 1073741824.0;

        if (_totalBytes > 0)
        {
            double ratio = Math.Clamp(used / _totalBytes, 0, 1);
            return new Sample(ratio, Compact(usedGb),
                $"VRAM {usedGb:0.0} / {_totalBytes / 1073741824.0:0.0} GB  ({Percent(ratio)}%)",
                usedGb, "GB");
        }

        _observedMax = Math.Max(_observedMax, used);
        return new Sample(Math.Clamp(used / _observedMax, 0, 1), Compact(usedGb),
            $"VRAM {usedGb:0.0} GB", usedGb, "GB");
    }

    /// <summary>ディスプレイ アダプタのレジストリから総 VRAM 量を拾う（取れなければ 0）。</summary>
    private static double ReadTotalVram()
    {
        try
        {
            const string path =
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(path);
            if (root is null) return 0;

            double best = 0;
            foreach (string name in root.GetSubKeyNames())
            {
                using RegistryKey? sub = root.OpenSubKey(name);
                object? raw = sub?.GetValue("HardwareInformation.qwMemorySize");
                double size = raw switch
                {
                    long l => l,
                    int i => i,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    _ => 0
                };
                best = Math.Max(best, size);
            }
            return best;
        }
        catch { return 0; }
    }
}

/// <summary>ディスクの使用率（アクティブ時間）。データ読み込みが詰まっていないか見る用。</summary>
internal sealed class DiskMetric : CounterMetric
{
    public override string Id => "disk";
    public override string Name => "ディスク使用率";
    public override Color DefaultColor => Color.FromArgb(119, 178, 43);

    protected override string CategoryEnglish => "PhysicalDisk";
    protected override string CounterEnglish => "% Disk Time";
    protected override bool Match(string instance) => instance == "_Total";

    public override Sample Read()
    {
        if (!TryCollect(out double busy)) return new Sample(0, "--", "ディスク 取得不可");
        double ratio = Math.Clamp(busy / 100.0, 0, 1);
        return new Sample(ratio, Percent(ratio), $"ディスク {Percent(ratio)}%", ratio * 100, "%");
    }
}

/// <summary>ネットワーク速度（Mbps）。上限は実測の最大値に自動追従する。</summary>
internal sealed class NetworkMetric : CounterMetric
{
    private readonly bool _received;
    private double _observedMax = 10;   // Mbps。最低これくらいを上限として扱う

    public NetworkMetric(bool received) => _received = received;

    public override string Id => _received ? "netdown" : "netup";
    public override string Name => _received ? "ネット受信速度" : "ネット送信速度";
    public override Color DefaultColor => _received
        ? Color.FromArgb(89, 193, 232)
        : Color.FromArgb(185, 83, 159);

    protected override string CategoryEnglish => "Network Interface";
    protected override string CounterEnglish => _received ? "Bytes Received/sec" : "Bytes Sent/sec";

    /// <summary>
    /// 仮想アダプタは除く。WSL2 や Hyper-V が入っていると、同じ通信が
    /// 物理 NIC と vEthernet の両方で数えられて、実速度の 2 倍になる。
    /// 深層学習の環境では WSL2 が入っていることが多いので現実に起きる。
    /// </summary>
    protected override bool Match(string instance)
        => !instance.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
        && !instance.Contains("isatap", StringComparison.OrdinalIgnoreCase)
        && !instance.Contains("Teredo", StringComparison.OrdinalIgnoreCase)
        && !instance.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
        && !instance.Contains("WSL", StringComparison.OrdinalIgnoreCase)
        && !instance.Contains("Pseudo", StringComparison.OrdinalIgnoreCase);

    public override Sample Read()
    {
        if (!TryCollect(out double bytes)) return new Sample(0, "--", $"{Name} 取得不可");

        double mbps = bytes * 8 / 1_000_000.0;
        _observedMax = Math.Max(_observedMax, mbps);
        double ratio = Math.Clamp(mbps / _observedMax, 0, 1);
        return new Sample(ratio, Compact(mbps), $"{Name} {mbps:0.0} Mbps", mbps, "Mbps");
    }
}

// ---------------------------------------------------------------------------
//  描画
// ---------------------------------------------------------------------------

/// <summary>
/// アイコンを描く。
///
/// どのモードも「16x16 の論理グリッドを組んでから、トレイの実表示サイズちょうどに
/// 敷き詰める」という同じ手順で描く。表示サイズは DPI 依存（100%=16px, 150%=24px,
/// 200%=32px）で、大きく描いて Windows に縮小させるとボケるため、必ず実寸で描く。
/// 数字モードも専用のドット字形を持たせてこの仕組みに乗せてある。
/// </summary>
internal static class IconFactory
{
    private const int G = 16;         // 論理グリッドの一辺
    private const int GaugeTop = 14;  // 下 2 行はゲージ

    private static readonly Color HotTone = Color.FromArgb(255, 96, 72);

    /// <summary>
    /// 段階ごとの下地。負荷が上がるほど背が伸びて、頭の毛が立つ。
    /// 16px では細かい表情よりも輪郭のほうが先に目に入るので、
    /// 「いまどの段階か」をまず形で分かるようにしてある。
    /// L=上のハイライト / C=本体色 / c=下の陰 / #=輪郭 / .=透明。
    /// 上を明るく下を暗くしておくと、16px でも平たい丸ではなく球体に見える。
    /// </summary>
    private static readonly string[][] StageSprites =
    {
        // うとうと: いちばん低い。毛も寝ている
        new[]
        {
            "................",
            "................",
            "................",
            "................",
            "................",
            "......#C........",
            "....########....",
            "..##LLLLLLLL##..",
            ".#LLLLLLLLLLLL#.",
            "#CCCCCCCCCCCCCC#",
            "#CCCCCCCCCCCCCC#",
            ".#cccccccccccc#.",
            "..##cccccccc##..",
            "....########....",
            "................",
            "................"
        },
        // ふつう: 起きて、毛が少し立つ
        new[]
        {
            "................",
            "................",
            "........#.......",
            "........C.......",
            ".....######.....",
            "...##LLLLLL##...",
            "..#LLLLLLLLLL#..",
            ".#CCCCCCCCCCCC#.",
            "#CCCCCCCCCCCCCC#",
            "#CCCCCCCCCCCCCC#",
            ".#CCCCCCCCCCCC#.",
            ".#cccccccccccc#.",
            "..##cccccccc##..",
            "....########....",
            "................",
            "................"
        },
        // ごきげん: 背が伸びて、毛が跳ねる
        new[]
        {
            "................",
            ".........#......",
            "........CC......",
            ".....######.....",
            "...##LLLLLL##...",
            "..#LLLLLLLLLL#..",
            ".#CCCCCCCCCCCC#.",
            "#CCCCCCCCCCCCCC#",
            "#CCCCCCCCCCCCCC#",
            "#CCCCCCCCCCCCCC#",
            ".#CCCCCCCCCCCC#.",
            ".#cccccccccccc#.",
            "..##cccccccc##..",
            "....########....",
            "................",
            "................"
        },
        // あせり: さらに伸びて、毛がぴんと立つ
        new[]
        {
            "........#.......",
            "........C.......",
            ".....######.....",
            "...##LLLLLL##...",
            "..#LLLLLLLLLL#..",
            ".#LLLLLLLLLLLL#.",
            "#CCCCCCCCCCCCCC#",
            "#CCCCCCCCCCCCCC#",
            "#CCCCCCCCCCCCCC#",
            "#CCCCCCCCCCCCCC#",
            ".#CCCCCCCCCCCC#.",
            ".#cccccccccccc#.",
            "..##cccccccc##..",
            "....########....",
            "................",
            "................"
        },
        // 限界: 体がいちばん大きく、毛も残っていない
        new[]
        {
            "................",
            ".....######.....",
            "...##LLLLLL##...",
            "..#LLLLLLLLLL#..",
            ".#LLLLLLLLLLLL#.",
            "#CCCCCCCCCCCCCC#",
            "#CCCCCCCCCCCCCC#",
            "#CCCCCCCCCCCCCC#",
            "#CCCCCCCCCCCCCC#",
            "#CCCCCCCCCCCCCC#",
            ".#CCCCCCCCCCCC#.",
            ".#cccccccccccc#.",
            "..##cccccccc##..",
            "....########....",
            "................",
            "................"
        }
    };

    /// <summary>
    /// 段階ごとの顔の位置。体の高さが段階で変わるので、目・ほっぺ・口の行も一緒に動かす。
    /// Eye は楕円の目の上端で、閉じ目・笑い目・× 目もここを基準に置く。
    /// Cheek はほっぺの行（あせり以上は付けないので使わない）。Mouth は口の上端。
    /// </summary>
    private static readonly (int Eye, int Cheek, int Mouth)[] FaceRows =
    {
        (7, 10, 11),   // うとうと
        (6, 10, 11),   // ふつう
        (6, 10, 10),   // ごきげん（口が 2 行あるので 1 行上げる。下端に着くと割れて見える）
        (5,  0, 10),   // あせり
        (5,  0,  9)    // 限界
    };

    /// <summary>
    /// 数字モード用のドット字形。数字は 5x9、記号は細身にしてある。
    /// 16px の中に 3 文字（"9.9" や "99+"）まで収めるための幅配分。
    /// </summary>
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
        ['1'] = new[] { "..#..", ".##..", "#.#..", "..#..", "..#..", "..#..", "..#..", "..#..", ".###." },
        ['2'] = new[] { ".###.", "#...#", "....#", "....#", "...#.", "..#..", ".#...", "#....", "#####" },
        ['3'] = new[] { "####.", "....#", "....#", "...#.", "..##.", "....#", "....#", "#...#", ".###." },
        ['4'] = new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#.", "...#.", "...#." },
        ['5'] = new[] { "#####", "#....", "#....", "####.", "....#", "....#", "....#", "#...#", ".###." },
        ['6'] = new[] { "..##.", ".#...", "#....", "#....", "####.", "#...#", "#...#", "#...#", ".###." },
        ['7'] = new[] { "#####", "....#", "....#", "...#.", "...#.", "..#..", "..#..", ".#...", ".#..." },
        ['8'] = new[] { ".###.", "#...#", "#...#", "#...#", ".###.", "#...#", "#...#", "#...#", ".###." },
        ['9'] = new[] { ".###.", "#...#", "#...#", "#...#", ".####", "....#", "....#", "...#.", ".##.." },
        ['.'] = new[] { "..", "..", "..", "..", "..", "..", "..", "##", "##" },
        ['+'] = new[] { "...", "...", "...", ".#.", "###", ".#.", "...", "...", "..." },
        ['-'] = new[] { "...", "...", "...", "...", "###", "...", "...", "...", "..." }
    };

    /// <summary>
    /// 3 文字用の細い字形（幅 4）。"100" は 5 幅では 16 に入らず、字間を詰めると
    /// 0 と 0 がくっついて 1 文字に見えてしまうため、桁数が増えたらこちらに切り替える。
    /// </summary>
    private static readonly Dictionary<char, string[]> NarrowGlyphs = new()
    {
        ['0'] = new[] { ".##.", "#..#", "#..#", "#..#", "#..#", "#..#", "#..#", "#..#", ".##." },
        ['1'] = new[] { ".#..", "##..", ".#..", ".#..", ".#..", ".#..", ".#..", ".#..", "###." },
        ['2'] = new[] { ".##.", "#..#", "...#", "...#", "..#.", ".#..", "#...", "#...", "####" },
        ['3'] = new[] { "###.", "...#", "...#", "..#.", ".##.", "...#", "...#", "#..#", ".##." },
        ['4'] = new[] { "..#.", ".##.", "#.#.", "#.#.", "####", "..#.", "..#.", "..#.", "..#." },
        ['5'] = new[] { "####", "#...", "#...", "###.", "...#", "...#", "...#", "#..#", ".##." },
        ['6'] = new[] { ".##.", "#...", "#...", "###.", "#..#", "#..#", "#..#", "#..#", ".##." },
        ['7'] = new[] { "####", "...#", "...#", "..#.", "..#.", ".#..", ".#..", ".#..", ".#.." },
        ['8'] = new[] { ".##.", "#..#", "#..#", "#..#", ".##.", "#..#", "#..#", "#..#", ".##." },
        ['9'] = new[] { ".##.", "#..#", "#..#", "#..#", ".###", "...#", "...#", "...#", ".##." },
        ['.'] = new[] { "..", "..", "..", "..", "..", "..", "..", "##", "##" },
        ['+'] = new[] { "...", "...", "...", ".#.", "###", ".#.", "...", "...", "..." },
        ['-'] = new[] { "...", "...", "...", "...", "###", "...", "...", "...", "..." }
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(double ratio, string text, Color color, IconStyle style,
                              IconMood mood = default, PixelSkin? skin = null,
                              bool smoothSkin = true)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        int percent = (int)Math.Round(ratio * 100);

        // 85% を超えたあたりから、じわっと熱を持たせる。
        // ただし本体を赤に寄せきると、青も緑も同じ色に潰れて「どれが限界なのか」が
        // 分からなくなる（青×サーモンは灰色、ミント×サーモンは茶色になる）。
        // 本体は指標の色を保ったまま少しだけ温め、危険はゲージの赤と表情で伝える。
        Color tone = percent >= 85
            ? Blend(color, HotTone, (percent - 85) / 15.0 * 0.25)
            : color;

        // 段階と計測ドットはここで 1 回だけ決める。3 モードで判定が割れると、
        // 見た目を切り替えたときだけ挙動が違う、という直しにくい差になる。
        int stage = mood.Stage >= 0 ? Math.Clamp(mood.Stage, 0, 4) : Stage(percent);
        bool dot = mood.Recording && RecordingDotOn(mood.Tick);

        // ゲージの赤は 85% で切り替えるのではなく、そこから 92% にかけて寄せていく。
        // ぴったり 85% を挟んで揺れる負荷だと、切り替えでは 1 秒ごとに色が点滅する。
        double hot = Math.Clamp((percent - HotPercent) / 7.0, 0, 1);

        return style switch
        {
            IconStyle.Face => Render(BuildFace(stage, percent, mood, dot), tone, hot),
            IconStyle.Image when skin is not null
                => CreateFromSkin(stage, percent, tone, skin, smoothSkin, dot),
            IconStyle.Image => Render(BuildFace(stage, percent, mood, dot), tone, hot),
            _ => Render(BuildNumber(percent, text, dot), tone, hot)
        };
    }

    // ----- グリッド共通 ---------------------------------------------------

    /// <summary>組み上げたグリッドを、トレイの実表示サイズちょうどに敷き詰めて Icon にする。</summary>
    private static Icon Render(char[][] grid, Color tone, double hot)
    {
        int size = Math.Clamp(SystemInformation.SmallIconSize.Width, 16, 64);

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            PaintSprite(g, grid, size, tone, hot);
        }
        return ToIcon(bmp);
    }

    private static char[][] EmptyGrid()
    {
        var grid = new char[G][];
        for (int y = 0; y < G; y++)
        {
            grid[y] = new char[G];
            Array.Fill(grid[y], '.');
        }
        return grid;
    }

    private static int Stage(int percent)
        => percent switch { < 20 => 0, < 50 => 1, < 80 => 2, < 95 => 3, _ => 4 };

    /// <summary>段階の境目。</summary>
    private static readonly int[] StageEdges = { 20, 50, 80, 95 };

    /// <summary>
    /// 次の段階。いまの段階を渡すと、境目を 3% ぶん行き過ぎるまで切り替えない。
    ///
    /// 段階ごとに体の形が違うので、境目に張り付いた負荷（93〜97% を往復する学習など）を
    /// そのまま使うと、1 秒ごとに体が上下して「震え」に見える。
    /// 震えるアニメーションは一度入れて撤去した経緯があり、同じものを作り直さないための細工。
    /// </summary>
    public static int NextStage(int current, int percent)
    {
        const int Slack = 3;
        int fresh = Stage(percent);
        if (current < 0 || current > 4) return fresh;
        if (fresh > current && percent < StageEdges[current] + Slack) return current;
        if (fresh < current && percent > StageEdges[fresh] - Slack) return current;
        return fresh;
    }

    /// <summary>
    /// 下 2 行のゲージ。両端を 1 ドット空けて角が丸く見えるようにしてある。
    /// しきい値を超えたら 'H'（赤）で塗り、本体の色を濁らせずに危険を伝える。
    /// </summary>
    private static void PaintGauge(char[][] grid, int percent)
    {
        char on = percent >= HotPercent ? 'H' : 'C';

        // 14 マスで 0〜100% を表すと 1 マスが約 7% になり、42% と 48% が同じ絵になる。
        // 2 行あるのに同じ長さで塗っていたので、下の行だけ半マスぶん先に伸ばして
        // 実質 28 段階にしてある。端に半分の段差が出るぶん「もう少し」が読める。
        double exact = Math.Clamp(percent, 0, 100) / 100.0 * 14;
        int full = (int)exact;
        bool half = exact - full >= 0.5;

        // 3% までは切り捨てで 0 マスになり、止まっているのと見分けが付かない。
        // 少しでも動いているなら半マスは点ける。
        if (percent > 0 && full == 0) half = true;

        for (int x = 1; x <= 14; x++)
        {
            int index = x - 1;
            // 半分の位置に切り欠きを入れて、目分量の基準を作る。
            // 塗ったところには入れない。満タンのバーに穴が空くと欠けて見える。
            if (index == 7 && index >= full) grid[GaugeTop][x] = '.';
            else grid[GaugeTop][x] = index < full ? on : 'D';
            grid[GaugeTop + 1][x] = index < full || (index == full && half) ? on : 'D';
        }
    }

    /// <summary>ゲージが赤に切り替わる負荷。</summary>
    private const int HotPercent = 85;

    private static Color Hot(Color tone) => Blend(tone, HotTone, 0.85);

    /// <summary>
    /// 計測中の赤ドットを出すか。2 秒点いて 1 秒消える。
    /// 出しっぱなしだと「録れているのか」が分かりにくいが、1 秒ごとに
    /// 明滅させるとちらついて見えるので、3 秒周期にしてある。
    /// </summary>
    private static bool RecordingDotOn(int tick) => tick % 3 != 2;

    /// <summary>計測中の目印（左上の赤いドット）。</summary>
    private static void PaintRecordingDot(char[][] grid)
    {
        grid[0][0] = 'R'; grid[0][1] = 'R';
        grid[1][0] = 'R'; grid[1][1] = 'R';
    }

    /// <summary>
    /// 負荷の段階ごとに表情を作る。
    /// 0:うとうと 1:ふつう 2:ごきげん 3:あせり 4:限界。
    ///
    /// 目は 3x3 に取ってハイライトを 1 ドット入れてある。16px では
    /// 「目が大きい・つやがある・ほっぺがある」の 3 つがかわいさをほぼ決める。
    /// </summary>
    private static char[][] BuildFace(int stage, int percent, IconMood mood, bool recordingDot)
    {
        char[][] grid = StageSprites[stage].Select(row => row.ToCharArray()).ToArray();
        (int eye, int cheek, int mouth) = FaceRows[stage];

        int t = mood.Tick;
        // まばたきの時計。指標ごとに位相をずらして、並んだアイコンが一斉に目を閉じないようにする。
        int b = t + mood.Phase;

        // 13 秒に 1 回、そのうち 61 秒に 1 回は 2 度続けて閉じる。
        // タイマーが 1 秒なので閉じ目は必ず 1 秒続く。9 秒間隔だと 1 割の時間ずっと
        // 目を閉じていることになり、まばたきというより居眠りに見えた。
        // t > 0 の条件は設定画面のプレビュー用。IconMood の既定値は Tick=0 なので、
        // これが無いとプレビューが必ず閉じ目で描かれる。
        bool blink = t > 0 && (b % 13 == 0 || b % 61 == 2);
        bool closedEyes = stage == 0 || (blink && stage is 1 or 2);

        void Set(int y, int x, char c)
        {
            if (y is >= 0 and < G && x is >= 0 and < G) grid[y][x] = c;
        }

        // 顔は左右対称なので、左半分の座標だけ書いて右は自動で折り返す。
        // こうしておくと表情をいじっても左右がずれない。
        void Pair(int y, int x, char c) { Set(y, x, c); Set(y, G - 1 - x, c); }

        // 縦長の楕円の目。真四角のまま塗ると黒が重すぎて、目というより穴に見えてしまう。
        void OvalEye(int left, int top)
        {
            Set(top, left + 1, 'K');
            for (int y = top + 1; y <= top + 2; y++)
                for (int x = left; x <= left + 2; x++) Set(y, x, 'K');
            Set(top + 3, left + 1, 'K');
        }

        // ----- 目（左目 x=3..5 / 右目 x=10..12） -----
        if (stage == 4)
        {
            // × 目
            Pair(eye, 3, 'K'); Pair(eye, 5, 'K');
            Pair(eye + 1, 4, 'K');
            Pair(eye + 2, 3, 'K'); Pair(eye + 2, 5, 'K');
        }
        else if (closedEyes)
        {
            // 閉じ目。両端を上げた弧にすると、ただの横線より気持ちよさそうに見える。
            Pair(eye + 1, 3, 'K'); Pair(eye + 1, 5, 'K');
            Pair(eye + 2, 4, 'K');
        }
        else if (stage == 2)
        {
            // ^ ^ の笑い目
            Pair(eye + 1, 4, 'K');
            Pair(eye + 2, 3, 'K'); Pair(eye + 2, 4, 'K'); Pair(eye + 2, 5, 'K');
        }
        else
        {
            OvalEye(3, eye);
            OvalEye(10, eye);

            if (stage == 3) Pair(eye + 1, 4, 'W');   // 瞳だけ小さく残して「見開き」
            else Pair(eye + 1, 3, 'W');              // つやのハイライト
        }

        // ----- ほっぺ -----
        if (stage <= 2)
        {
            Pair(cheek, 2, 'P'); Pair(cheek, 3, 'P');
            if (stage == 2) { Pair(cheek - 1, 2, 'P'); Pair(cheek - 1, 3, 'P'); }   // ごきげんは濃いめ
        }

        // ----- 口 -----
        switch (stage)
        {
            case 0:
                Pair(mouth, 7, 'K');                                        // ちいさい口
                break;
            case 1:
                Pair(mouth, 6, 'K'); Pair(mouth, 7, 'K');                   // 一文字
                break;
            case 2:
                Pair(mouth, 6, 'K'); Pair(mouth, 7, 'K');                   // 開いたにこにこ
                Pair(mouth + 1, 7, 'K');
                break;
            case 3:
                Pair(mouth, 5, 'K'); Pair(mouth, 7, 'K');                   // ぎざぎざ
                Pair(mouth + 1, 6, 'K');
                break;
            default:
                // 大きく開いた口。四隅まで塗ると黒が重すぎて、口ではなく
                // 体に穴が空いたように見える（目を楕円にしたのと同じ理由）。
                Pair(mouth, 6, 'K'); Pair(mouth, 7, 'K');
                Pair(mouth + 1, 5, 'K'); Pair(mouth + 1, 6, 'K'); Pair(mouth + 1, 7, 'K');
                Pair(mouth + 2, 6, 'K'); Pair(mouth + 2, 7, 'K');
                break;
        }

        // ----- 付属物 -----
        if (stage == 0)
        {
            // zzz。3x3 では z と 工 の区別が付かないので、対角がはっきり出る 4x4 で描く。
            // この段階は体が低いぶん上に余白があるので、そこを使える。
            // 3 秒ごとに 1 ドットだけ浮き上がる。体を動かすと 1 秒タイマーでは
            // ちらついて見えるが、マークがゆっくり漂うぶんには落ち着いて見える。
            // 計測中の赤ドットと同時に切り替わらないよう、位相を 1 つずらしてある
            int zy = 1 - ((t + 1) / 3) % 2;
            Set(zy, 11, 'B'); Set(zy, 12, 'B'); Set(zy, 13, 'B'); Set(zy, 14, 'B');
            Set(zy + 1, 13, 'B');
            Set(zy + 2, 12, 'B');
            Set(zy + 3, 11, 'B'); Set(zy + 3, 12, 'B');
            Set(zy + 3, 13, 'B'); Set(zy + 3, 14, 'B');
        }
        else if (stage >= 3)
        {
            // 汗。限界のときは反対側にもう 1 粒足す。
            // 体の輪郭('#')に重ねると丸いシルエットが欠けて見えるので、外側だけに置く。
            // 上 2 マス（y=0,1）は計測中の赤ドットが載る場所なので空けておく。
            Set(2, 1, 'B'); Set(3, 0, 'B'); Set(3, 1, 'B'); Set(4, 0, 'B');
            if (stage == 4)
            {
                Set(2, 14, 'B'); Set(3, 14, 'B'); Set(3, 15, 'B'); Set(4, 15, 'B');
            }
        }

        if (recordingDot) PaintRecordingDot(grid);
        PaintGauge(grid, percent);
        return grid;
    }

    // ----- 数字 -----------------------------------------------------------

    /// <summary>
    /// 数字を白いドットで置き、そのまわりを 1 ドット暗く縁取る。
    /// タスクバーが明色でも暗色でも読めるようにするための縁取り。
    /// </summary>
    private static char[][] BuildNumber(int percent, string text, bool recording)
    {
        char[][] grid = EmptyGrid();

        // 3 文字以上は細い字形に切り替える。太いままだと "100" が 16 に収まらない。
        Dictionary<char, string[]> font =
            text.Count(Glyphs.ContainsKey) >= 3 ? NarrowGlyphs : Glyphs;

        List<string[]> glyphs = text.Where(font.ContainsKey).Select(c => font[c]).ToList();
        if (glyphs.Count == 0) glyphs.Add(font['-']);

        // 字間は 1 ドット。字そのものが 16 に入らないときだけ 0 に詰める。
        int sum = glyphs.Sum(gl => gl[0].Length);
        int gap = sum + (glyphs.Count - 1) <= G ? 1 : 0;
        int width = sum + gap * (glyphs.Count - 1);

        int x0 = (G - width) / 2;
        const int y0 = 3;   // 高さ 9 なので y=3..11。下のゲージ(y=14,15)と 2 ドット空く。

        foreach (string[] glyph in glyphs)
        {
            for (int y = 0; y < glyph.Length; y++)
            {
                for (int x = 0; x < glyph[y].Length; x++)
                {
                    if (glyph[y][x] != '#') continue;
                    int gy = y0 + y, gx = x0 + x;
                    if (gy is >= 0 and < G && gx is >= 0 and < G) grid[gy][gx] = 'W';
                }
            }
            x0 += glyph[0].Length + gap;
        }

        Outline(grid);
        if (recording) PaintRecordingDot(grid);
        PaintGauge(grid, percent);
        return grid;
    }

    /// <summary>白ドットに接している透明セルを縁色にする。</summary>
    private static void Outline(char[][] grid)
    {
        var edges = new List<(int Y, int X)>();
        for (int y = 0; y < G; y++)
        {
            for (int x = 0; x < G; x++)
            {
                if (grid[y][x] != '.') continue;

                bool touching = false;
                for (int dy = -1; dy <= 1 && !touching; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int ny = y + dy, nx = x + dx;
                        if (ny is >= 0 and < G && nx is >= 0 and < G && grid[ny][nx] == 'W')
                        {
                            touching = true;
                            break;
                        }
                    }
                }
                if (touching) edges.Add((y, x));
            }
        }
        // 走査中に書き換えると縁が縁を呼んで太るので、集めてから塗る
        foreach ((int y, int x) in edges) grid[y][x] = '#';
    }

    private static void PaintSprite(Graphics g, char[][] grid, int size, Color tone, double hot)
    {
        var palette = new Dictionary<char, SolidBrush>
        {
            // 輪郭は真っ黒ではなく本体色を少し混ぜた暗色にしてある。硬さが取れて印象が柔らかい。
            ['#'] = new SolidBrush(Color.FromArgb(230, Blend(Color.FromArgb(16, 16, 22), tone, 0.22))),
            ['C'] = new SolidBrush(tone),
            ['H'] = new SolidBrush(Blend(tone, HotTone, 0.85 * Math.Clamp(hot, 0, 1))),
            ['L'] = new SolidBrush(Blend(tone, Color.White, 0.26)),
            ['c'] = new SolidBrush(Blend(tone, Color.FromArgb(20, 20, 30), 0.22)),
            ['K'] = new SolidBrush(Color.FromArgb(238, 16, 16, 20)),
            ['W'] = new SolidBrush(Color.White),
            ['P'] = new SolidBrush(Blend(tone, Color.FromArgb(255, 118, 140), 0.55)),
            ['B'] = new SolidBrush(Color.FromArgb(240, 130, 210, 255)),
            // ゲージの下地。薄すぎるとアイコンが欠けて見えるので、
            // 「まだ伸びていない目盛り」だと分かる程度には出しておく。
            ['D'] = new SolidBrush(Color.FromArgb(190, 130, 130, 142)),
            ['R'] = new SolidBrush(Color.FromArgb(245, 235, 60, 60))
        };

        try
        {
            double cell = size / (double)G;
            for (int y = 0; y < G; y++)
            {
                for (int x = 0; x < G; x++)
                {
                    if (!palette.TryGetValue(grid[y][x], out SolidBrush? brush)) continue;

                    int x0 = (int)Math.Floor(x * cell);
                    int y0 = (int)Math.Floor(y * cell);
                    int w = Math.Max(1, (int)Math.Floor((x + 1) * cell) - x0);
                    int h = Math.Max(1, (int)Math.Floor((y + 1) * cell) - y0);
                    g.FillRectangle(brush, x0, y0, w, h);
                }
            }
        }
        finally
        {
            foreach (SolidBrush brush in palette.Values) brush.Dispose();
        }
    }

    // ----- 画像から作ったドット絵 -----------------------------------------

    /// <summary>
    /// 読み込んだ絵をトレイの実サイズに合わせて描く。負荷の段階は
    /// 明るさ・赤み・マーク（zzz / 汗）・下部ゲージで表す。
    /// </summary>
    private static Icon CreateFromSkin(int stage, int percent, Color tone, PixelSkin skin,
                                       bool smooth, bool recording)
    {
        // 実際に表示される大きさ。100% 表示なら 16、150% なら 24、200% なら 32。
        int size = Math.Clamp(SystemInformation.SmallIconSize.Width, 16, 64);

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = smooth
                ? InterpolationMode.HighQualityBicubic
                : InterpolationMode.NearestNeighbor;

            Bitmap source = skin.ToBitmap();
            using (ImageAttributes? effect = StageEffect(stage))
            {
                var dest = new Rectangle(0, 0, size, size);
                if (effect is null)
                    g.DrawImage(source, dest);
                else
                    g.DrawImage(source, dest, 0, 0, source.Width, source.Height,
                                GraphicsUnit.Pixel, effect);
            }

            // マーク類は 16 分割を基準に置く（解像度が変わっても同じ位置に出る）
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            float u = size / 16f;
            RectangleF Cell(int x, int y) => new(x * u, y * u, u + 0.5f, u + 0.5f);

            // マークはドット絵キャラと同じ色・同じ座標に揃える。
            // 白のままだとライトテーマのタスクバー（ほぼ白）に溶けて消える。
            using var mark = new SolidBrush(Color.FromArgb(240, 130, 210, 255));
            if (stage == 0)
            {
                foreach ((int y, int x) in new[]
                         { (1, 11), (1, 12), (1, 13), (1, 14), (2, 13), (3, 12),
                           (4, 11), (4, 12), (4, 13), (4, 14) })
                    g.FillRectangle(mark, Cell(x, y));
            }
            else if (stage >= 3)
            {
                foreach ((int y, int x) in new[] { (2, 1), (3, 0), (3, 1), (4, 0) })
                    g.FillRectangle(mark, Cell(x, y));
                if (stage == 4)
                    foreach ((int y, int x) in new[] { (2, 14), (3, 14), (3, 15), (4, 15) })
                        g.FillRectangle(mark, Cell(x, y));
            }

            // 下部のゲージ。ドット絵モードと同じ「下 2 行・両端 1 ドット空け」に揃える。
            float barTop = GaugeTop * u;
            float barLeft = u;
            float barWidth = 14 * u;
            using (var off = new SolidBrush(Color.FromArgb(190, 130, 130, 142)))
                g.FillRectangle(off, barLeft, barTop, barWidth, size - barTop);
            using (var on = new SolidBrush(percent >= HotPercent ? Hot(tone) : tone))
                g.FillRectangle(on, barLeft, barTop,
                                barWidth * Math.Clamp(percent, 0, 100) / 100f, size - barTop);

            if (recording)
            {
                using var dot = new SolidBrush(Color.FromArgb(245, 235, 60, 60));
                g.FillRectangle(dot, 0, 0, 2 * u, 2 * u);
            }
        }
        return ToIcon(bmp);
    }

    /// <summary>段階ごとの色補正。補正の要らない段階だけ null を返す。</summary>
    private static ImageAttributes? StageEffect(int stage)
    {
        // 素の色のままでよい段階だけ null（補正なし）にする。
        if (stage == 2) return null;

        // 負荷が上がるほど明るくする。以前は 0.62 → 1.00 → 1.12 → 1.00 → 1.00 で、
        // 「ふつう」と「あせり」の絵が完全に同じになっていた。
        // うとうとを 0.62 まで落とすと、いちばん長く続く状態がいちばん見えにくくもなる。
        float scale = stage switch { 0 => 0.80f, 1 => 0.90f, 2 => 1.0f, 3 => 1.06f, _ => 1.12f };
        float addR = stage == 4 ? 0.25f : 0f;
        float addG = stage == 4 ? -0.05f : 0f;
        float addB = stage == 4 ? -0.05f : 0f;

        var matrix = new ColorMatrix(new[]
        {
            new[] { scale, 0f, 0f, 0f, 0f },
            new[] { 0f, scale, 0f, 0f, 0f },
            new[] { 0f, 0f, scale, 0f, 0f },
            new[] { 0f, 0f, 0f, 1f, 0f },
            new[] { addR, addG, addB, 0f, 1f }
        });

        var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        return attributes;
    }

    // ----- 共通 -----------------------------------------------------------

    private static Icon ToIcon(Bitmap bmp)
    {
        IntPtr handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount),
            (int)(from.B + (to.B - from.B) * amount));
    }
}

// ---------------------------------------------------------------------------
//  画像 → ドット絵
// ---------------------------------------------------------------------------

/// <summary>
/// 好きな画像から作ったアイコン素材。解像度は 16〜64 から選べる。
/// トレイの実表示サイズは DPI 依存（100%=16px, 150%=24px, 200%=32px）なので、
/// 高解像度が効くのは高 DPI 環境か、なめらか表示にしたときの陰影。
/// </summary>
/// <summary>
/// 画像から作ったドット絵。中身は作ったあと変わらないので、
/// Bitmap は 1 枚だけ作って使い回す。使い終わったら Dispose すること。
/// </summary>
internal sealed class PixelSkin : IDisposable
{
    public static readonly int[] Resolutions = { 16, 24, 32, 48, 64 };

    public int Size { get; }

    /// <summary>[y, x] の並び。</summary>
    public Color[,] Pixels { get; }

    private PixelSkin(int size, Color[,] pixels)
    {
        Size = size;
        Pixels = pixels;
    }

    private static string SavedPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMeter", "skin.png");

    /// <summary>
    /// 画像を指定解像度の正方形に縮小する。posterize を付けると色数を落として
    /// ドット絵らしくなり、外すと元画像の陰影がそのまま残る。
    /// removeBackground は四隅に近い色を背景とみなして透明にする。
    /// </summary>
    public static PixelSkin FromImage(string path, int size, bool removeBackground, bool posterize)
    {
        size = Math.Clamp(size, 16, 64);

        using var source = new Bitmap(path);

        // 中央を正方形に切り出してから縮小する（縦横比を崩さないため）
        int side = Math.Min(source.Width, source.Height);
        var crop = new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side);

        using var small = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, size, size), crop, GraphicsUnit.Pixel);
        }

        Color background = AverageCorners(small, size);
        var pixels = new Color[size, size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Color c = small.GetPixel(x, y);

                if (c.A < 100 ||
                    (removeBackground && background.A > 0 && Distance(c, background) < 70))
                {
                    pixels[y, x] = Color.Transparent;
                    continue;
                }
                pixels[y, x] = posterize ? Posterize(c) : Color.FromArgb(255, c.R, c.G, c.B);
            }
        }
        return new PixelSkin(size, pixels);
    }

    /// <summary>各チャンネルを 6 段階に丸めて、ドット絵らしい色数にする。</summary>
    private static Color Posterize(Color c)
    {
        static int Step(int v) => Math.Clamp((int)Math.Round(v / 51.0) * 51, 0, 255);
        return Color.FromArgb(255, Step(c.R), Step(c.G), Step(c.B));
    }

    private static Color AverageCorners(Bitmap bmp, int size)
    {
        Color[] corners =
        {
            bmp.GetPixel(0, 0), bmp.GetPixel(size - 1, 0),
            bmp.GetPixel(0, size - 1), bmp.GetPixel(size - 1, size - 1)
        };
        if (corners.Any(c => c.A < 100)) return Color.Transparent;

        return Color.FromArgb(255,
            (int)corners.Average(c => c.R),
            (int)corners.Average(c => c.G),
            (int)corners.Average(c => c.B));
    }

    private static double Distance(Color a, Color b)
        => Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    private Bitmap? _bitmap;

    /// <summary>
    /// この絵の Bitmap。**呼んだ側で破棄しないこと**（PixelSkin が持っている）。
    /// 毎秒アイコンごとに作り直していたころは、64x64 の絵 × 8 指標で
    /// 1 秒あたり 3 万回以上 SetPixel を回していた。
    /// 使用率を見せるアプリが自分で CPU を食っていた。
    /// </summary>
    public Bitmap ToBitmap()
    {
        if (_bitmap is not null) return _bitmap;

        var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                bmp.SetPixel(x, y, Pixels[y, x]);
        return _bitmap = bmp;
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }

    public void SaveAsDefault()
    {
        try
        {
            string path = SavedPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ToBitmap().Save(path, ImageFormat.Png);
        }
        catch { /* 保存できなくても今回のセッションでは使える */ }
    }

    public static PixelSkin? LoadSaved()
    {
        try
        {
            if (!File.Exists(SavedPath)) return null;

            // ファイルを掴んだままにしないよう、いったんコピーして読む
            using var stored = new Bitmap(SavedPath);
            using var bmp = new Bitmap(stored);

            int size = Math.Clamp(Math.Min(bmp.Width, bmp.Height), 16, 64);
            var pixels = new Color[size, size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    pixels[y, x] = bmp.GetPixel(x, y);

            return new PixelSkin(size, pixels);
        }
        catch { return null; }
    }
}

/// <summary>変換結果を拡大表示と実寸の両方で見せ、採用するかどうかを決めてもらう画面。</summary>
internal sealed class SkinPreviewForm : Form
{
    private const int PreviewBox = 288;

    private readonly string _source;
    private readonly Panel _preview = new();
    private readonly Panel _actual = new();

    private int _resolution = 32;
    private bool _removeBackground = true;
    private bool _posterize;

    public PixelSkin? Result { get; private set; }

    /// <summary>なめらかに拡大縮小するか（false ならドットがそのまま出る）。</summary>
    public bool Smooth { get; private set; } = true;

    public SkinPreviewForm(string sourcePath)
    {
        _source = sourcePath;
        Rebuild();

        Text = "アイコンの作成";
        Font = new Font("Yu Gothic UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(32, 32, 36);
        ForeColor = Color.White;

        // 設定画面と同じ理由で、座標を数値で置かない。
        // 拡大表示のとき文字だけ大きくなって重なるため、並べ方は入れ物に任せる。
        int unit = Math.Max(16, Font.Height);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            Padding = new Padding(unit),
            BackColor = BackColor
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(Control c)
        {
            root.Controls.Add(c);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        _preview.Size = new Size(PreviewBox, PreviewBox);
        _preview.Anchor = AnchorStyles.None;   // セル内で中央になる
        _preview.Margin = new Padding(0, 0, 0, unit / 2);
        _preview.Paint += (_, e) => DrawZoomed(e.Graphics);
        Row(_preview);

        Row(new Label
        {
            Text = "実際の大きさ（16 / 24 / 32 px）",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = Color.FromArgb(190, 255, 255, 255)
        });

        _actual.Size = new Size(160, 40);
        _actual.Anchor = AnchorStyles.None;
        _actual.Margin = new Padding(0, 0, 0, unit);
        _actual.Paint += (_, e) => DrawActualSizes(e.Graphics);
        Row(_actual);

        var resolutionRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, unit / 2)
        };
        resolutionRow.Controls.Add(new Label
        {
            Text = "解像度",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.White,
            Margin = new Padding(0, unit / 4, unit / 2, 0)
        });

        var resolution = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            AutoSize = true,
            Width = unit * 7
        };
        foreach (int value in PixelSkin.Resolutions) resolution.Items.Add($"{value} × {value}");
        resolution.SelectedIndex = Array.IndexOf(PixelSkin.Resolutions, _resolution);
        resolution.SelectedIndexChanged += (_, _) =>
        {
            _resolution = PixelSkin.Resolutions[resolution.SelectedIndex];
            Rebuild();
            _preview.Invalidate();
            _actual.Invalidate();
        };
        resolutionRow.Controls.Add(resolution);
        Row(resolutionRow);

        void Option(string text, bool value, Action<bool> apply)
        {
            var box = new CheckBox
            {
                Text = text,
                Checked = value,
                AutoSize = true,
                ForeColor = Color.White,
                Margin = new Padding(0, 0, 0, unit / 4)
            };
            box.CheckedChanged += (_, _) =>
            {
                apply(box.Checked);
                Rebuild();
                _preview.Invalidate();
                _actual.Invalidate();
            };
            Row(box);
        }

        Option("背景を自動で透明にする", _removeBackground, v => _removeBackground = v);
        Option("色を減らしてドット絵っぽくする", _posterize, v => _posterize = v);
        Option("なめらかに表示する（オフでドットがくっきり）", Smooth, v => Smooth = v);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, unit / 2, 0, 0)
        };
        var cancel = new Button
        {
            Text = "やめる",
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.System,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(unit * 5, unit * 2)
        };
        var ok = new Button
        {
            Text = "これにする",
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.System,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(unit * 5, unit * 2),
            Margin = new Padding(unit / 2, 0, 0, 0)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        Row(buttons);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;

        Size wanted = root.GetPreferredSize(Size.Empty);
        Rectangle screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(Math.Min(wanted.Width, screen.Width - unit * 4),
                              Math.Min(wanted.Height, screen.Height - unit * 6));
    }

    private void Rebuild()
    {
        // 選択肢を変えるたびに作り直すので、前のぶんは捨てる（Bitmap を抱えている）
        PixelSkin? previous = Result;
        Result = PixelSkin.FromImage(_source, _resolution, _removeBackground, _posterize);
        previous?.Dispose();
    }

    private void DrawCheckerboard(Graphics g, Rectangle area, int cell)
    {
        using var light = new SolidBrush(Color.FromArgb(70, 70, 76));
        using var dark = new SolidBrush(Color.FromArgb(56, 56, 62));
        for (int y = 0; y * cell < area.Height; y++)
            for (int x = 0; x * cell < area.Width; x++)
                g.FillRectangle((x + y) % 2 == 0 ? light : dark,
                                area.X + x * cell, area.Y + y * cell, cell, cell);
    }

    private void DrawZoomed(Graphics g)
    {
        DrawCheckerboard(g, new Rectangle(0, 0, PreviewBox, PreviewBox), 12);
        if (Result is null) return;

        Bitmap bmp = Result.ToBitmap();
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = Smooth
            ? InterpolationMode.HighQualityBicubic
            : InterpolationMode.NearestNeighbor;
        g.DrawImage(bmp, new Rectangle(0, 0, PreviewBox, PreviewBox));
    }

    private void DrawActualSizes(Graphics g)
    {
        if (Result is null) return;

        Bitmap bmp = Result.ToBitmap();
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = Smooth
            ? InterpolationMode.HighQualityBicubic
            : InterpolationMode.NearestNeighbor;

        int x = 10;
        foreach (int size in new[] { 16, 24, 32 })
        {
            DrawCheckerboard(g, new Rectangle(x, 4, size, size), 4);
            g.DrawImage(bmp, new Rectangle(x, 4, size, size));
            x += size + 22;
        }
    }
}

// ---------------------------------------------------------------------------
//  セットアップ / 設定
// ---------------------------------------------------------------------------

/// <summary>
/// 初回起動の案内と、ふだんの設定を兼ねる 1 枚の画面。
/// 右クリックメニューでも同じことはできるが、初めての人にメニューを探させないために、
/// 「表示する項目・見た目・タスクバーに出す・見失わないために」をここに集めてある。
///
/// 変更はその場で保存して apply を呼ぶので、トレイのアイコンが即座に変わる。
///
/// **座標を数値で置かないこと。** 画面の拡大率（125% / 150% など）が上がると文字だけが
/// 大きくなり、手で決めた枠から溢れて重なる。実際にノート PC で崩れた。
/// 位置と大きさは TableLayoutPanel と AutoSize に任せ、余白だけ文字の高さを基準に決める。
/// </summary>
internal sealed class SetupForm : Form
{
    private static readonly Color Back = Color.FromArgb(30, 30, 36);
    private static readonly Color Card = Color.FromArgb(42, 42, 50);
    private static readonly Color Faint = Color.FromArgb(180, 255, 255, 255);
    private static readonly Color Accent = Color.FromArgb(120, 200, 255);

    private readonly List<Metric> _metrics;
    private readonly Action _apply;
    private readonly Func<PixelSkin?> _skin;
    private readonly Action _chooseImage;

    private readonly List<CheckBox> _metricBoxes = new();
    private readonly List<Panel> _previews = new();
    private readonly List<(IconStyle Style, RadioButton Button)> _styleButtons = new();
    private bool _syncing;

    private readonly TableLayoutPanel _body = new();

    /// <summary>文字の高さを基準寸法にする。拡大率が変わればこれも変わる。</summary>
    private int Unit => Math.Max(16, Font.Height);

    private int Swatch => Unit * 2;   // プレビューアイコンの一辺

    public SetupForm(List<Metric> metrics, bool firstRun, Action apply,
                     Func<PixelSkin?> skin, Action chooseImage)
    {
        _metrics = metrics;
        _apply = apply;
        _skin = skin;
        _chooseImage = chooseImage;

        Text = firstRun ? "TaskbarMeter へようこそ" : "TaskbarMeter の設定";
        BackColor = Back;
        ForeColor = Color.White;
        Font = new Font("Yu Gothic UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        ShowInTaskbar = true;

        // 拡大率や文字サイズが想定外でも詰まないよう、縮められる窓にしておく
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;

        _body.Dock = DockStyle.Fill;
        _body.ColumnCount = 1;
        _body.AutoScroll = true;
        _body.BackColor = Back;
        _body.Padding = new Padding(Unit);
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Add(BuildHeader(firstRun));
        Add(SectionTitle("① 表示する項目"));
        // 「高負荷のときだけ表示」を選んでいると、ここで項目を足しても
        // しきい値を超えるまで何も出てこない。黙っていると壊れたように見える。
        if (Settings.Mode == DisplayMode.Auto)
            Add(Para($"いまは「高負荷のときだけ表示」です。{Settings.Threshold}% を超えるまで" +
                     "アイコンは出てきません（右クリック →「常に表示」で切り替えられます）。", Accent));
        Add(BuildMetricSection());
        Add(SectionTitle("② 見た目"));
        Add(BuildStyleSection());
        Add(SectionTitle("③ タスクバーに出す"));
        Add(BuildTaskbarSection());
        Add(SectionTitle("④ 見失わないために"));
        Add(BuildOptionSection());

        Control bar = BuildButtonBar(firstRun);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Back
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_body, 0, 0);
        root.Controls.Add(bar, 0, 1);
        Controls.Add(root);

        // 幅は中身に決めさせる。数値で決め打つと、拡大表示のときに
        // 文字だけ大きくなって「GPU 使用率 (学」のように切れる。
        Rectangle screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size natural = _body.GetPreferredSize(Size.Empty);
        int width = Math.Clamp(natural.Width + Unit, Unit * 22, screen.Width - Unit * 4);

        // 高さはその幅で折り返した結果から。入りきらないぶんは本文をスクロールさせる
        // （ボタンは常に見える）。
        int barHeight = bar.GetPreferredSize(new Size(width, 0)).Height;
        int contentHeight = _body.GetPreferredSize(new Size(width, 0)).Height;
        int maxBody = screen.Height - barHeight - Unit * 6;

        ClientSize = new Size(width,
            Math.Max(Unit * 14, Math.Min(contentHeight, maxBody)) + barHeight);
        MinimumSize = new Size(Unit * 18, Unit * 12);
    }

    private void Add(Control section)
    {
        section.Dock = DockStyle.Top;
        section.AutoSize = true;
        _body.Controls.Add(section);
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    }

    /// <summary>折り返しつきの説明文。幅は親に合わせ、高さは中身から決まる。</summary>
    private Label Para(string text, Color color, bool bold = false, float scale = 1f)
    {
        var label = new Label
        {
            Text = text,
            ForeColor = color,
            AutoSize = true,
            MaximumSize = new Size(Unit * 24, 0),   // ここで折り返す
            Margin = new Padding(0, 0, 0, Unit / 3)
        };
        if (bold || scale != 1f)
            label.Font = new Font(Font.FontFamily, Font.Size * scale,
                                  bold ? FontStyle.Bold : FontStyle.Regular);
        return label;
    }

    private Control Stack(params Control[] children)
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            BackColor = Color.Transparent
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (Control c in children)
        {
            panel.Controls.Add(c);
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        return panel;
    }

    // ----- 見出し ---------------------------------------------------------

    private Control BuildHeader(bool firstRun)
        => Stack(
            Para("TaskbarMeter", Color.White, bold: true, scale: 1.7f),
            Para(firstRun
                    ? "タスクバーの右下に、CPU や GPU の使用率を住まわせます。\n下の 4 つを決めるだけで使いはじめられます。"
                    : "設定はすぐに反映されます。閉じるボタンで終わりです。",
                 Faint));

    private Control SectionTitle(string text)
    {
        Label label = Para(text, Accent, bold: true, scale: 1.1f);
        label.Margin = new Padding(0, Unit, 0, Unit / 3);
        return label;
    }

    // ----- ① 表示する項目 -------------------------------------------------

    private Control BuildMetricSection()
    {
        // 2 列に並べる。8 項目を縦一列にすると画面が長くなりすぎるため。
        int rows = (_metrics.Count + 1) / 2;
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = rows,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            BackColor = Color.Transparent
        };
        // AutoSize にして、列幅は中でいちばん長い名前に合わせる。
        // 割合で決めると、拡大表示のときに名前がはみ出して切れる。
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        for (int i = 0; i < _metrics.Count; i++)
        {
            Metric metric = _metrics[i];
            grid.Controls.Add(BuildMetricRow(metric), i / rows, i % rows);
        }
        return grid;
    }

    private Control BuildMetricRow(Metric metric)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, Unit / 2, Unit / 3),
            BackColor = Color.Transparent
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var preview = new Panel
        {
            Size = new Size(Swatch, Swatch),
            Margin = new Padding(0, 0, Unit / 2, 0),
            BackColor = Card
        };
        preview.Paint += (_, e) => DrawPreview(e.Graphics, preview.ClientRectangle, 0.45, "45",
                                               Settings.MetricColor(metric.Id, metric.DefaultColor));
        row.Controls.Add(preview, 0, 0);
        _previews.Add(preview);

        var box = new CheckBox
        {
            Text = metric.ShortName,
            Checked = Settings.MetricEnabled(metric.Id, metric.DefaultEnabled),
            ForeColor = Color.White,
            AutoSize = true,
            Anchor = AnchorStyles.Left,     // セル内で縦中央になる
            Margin = new Padding(0)
        };
        box.CheckedChanged += (_, _) => OnMetricToggled(box, metric);
        row.Controls.Add(box, 1, 0);
        _metricBoxes.Add(box);

        return row;
    }

    private void OnMetricToggled(CheckBox box, Metric metric)
    {
        if (_syncing) return;

        // 全部消すと右クリックする先が無くなり、設定に戻れなくなる。
        // 画面のチェックではなく設定を数えること。この画面を開いたまま
        // トレイの右クリックからも項目を外せるので、画面の状態は古くなりうる。
        // この指標を除いて数えること。ここに来る時点ではまだ Settings に
        // 書き戻していないので、自分自身は有効なままに見える。
        int others = _metrics.Count(
            m => m.Id != metric.Id && Settings.MetricEnabled(m.Id, m.DefaultEnabled));
        if (!box.Checked && others == 0)
        {
            _syncing = true;
            box.Checked = true;
            _syncing = false;
            MessageBox.Show(this,
                "最低 1 つは表示したままにしてください。\n" +
                "アイコンが 1 つも無くなると、右クリックで設定を開けなくなります。",
                "TaskbarMeter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Settings.SetMetricEnabled(metric.Id, box.Checked);
        _apply();
    }

    // ----- ② 見た目 -------------------------------------------------------

    private Control BuildStyleSection()
    {
        var choices = new (IconStyle Style, string Label, string Note)[]
        {
            (IconStyle.Face, "ドット絵キャラ", "負荷で表情が変わります"),
            (IconStyle.Number, "数字", "使用率をそのまま数字で"),
            (IconStyle.Image, "好きな画像", "画像をドット絵に変換します")
        };

        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            BackColor = Color.Transparent
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        int rowIndex = 0;
        foreach ((IconStyle style, string label, string note) in choices)
        {
            IconStyle captured = style;

            // 低・中・高の 3 つを並べる。負荷で見た目が変わることを言葉より早く伝えられる。
            var strip = new Panel
            {
                Size = new Size(Swatch * 3 + Unit, Swatch),
                Margin = new Padding(0, 0, Unit / 2, Unit / 3),
                BackColor = Card
            };
            strip.Paint += (_, e) =>
            {
                Color color = _metrics[0].DefaultColor;
                double[] samples = { 0.15, 0.55, 1.0 };
                string[] texts = { "15", "55", "100" };

                // 実際に与えられた幅から 1 つぶんの大きさを割り出す。
                // 作ったときの寸法を覚えて描くと、あとで拡大率が変わったときに
                // 3 つ目がはみ出して消える。
                int pad = Math.Max(2, strip.ClientSize.Width / 40);
                int side = Math.Min(strip.ClientSize.Height,
                                    (strip.ClientSize.Width - pad * 4) / 3);
                int top = (strip.ClientSize.Height - side) / 2;
                for (int i = 0; i < 3; i++)
                    DrawPreview(e.Graphics,
                                new Rectangle(pad + i * (side + pad), top, side, side),
                                samples[i], texts[i], color, captured);
            };
            // 説明文は次の行に置き、ラジオは全部この grid の直接の子にする。
            // **入れ子のパネルに 1 つずつ入れてはいけない。** ラジオの「どれか 1 つ」は
            // 同じ親の中でしか働かないので、別々の親に入れると 2 つ選べてしまう。
            grid.Controls.Add(strip, 0, rowIndex);
            grid.SetRowSpan(strip, 2);
            _previews.Add(strip);

            var radio = new RadioButton
            {
                Text = label,
                Checked = Settings.Style == captured,
                ForeColor = Color.White,
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
                Margin = new Padding(0)
            };
            radio.CheckedChanged += (_, _) => OnStyleChosen(radio, captured);
            _styleButtons.Add((captured, radio));
            grid.Controls.Add(radio, 1, rowIndex);

            Label note2 = Para(note, Faint);
            note2.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            note2.Margin = new Padding(0, 0, 0, Unit / 3);
            grid.Controls.Add(note2, 1, rowIndex + 1);

            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rowIndex += 2;
        }

        var change = new Button
        {
            Text = "画像を選び直す…",
            FlatStyle = FlatStyle.System,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, Unit / 3, 0, 0)
        };
        // 選び直すと常駐側が Style を Image に変える。ラジオも合わせないと画面が嘘をつく
        change.Click += (_, _) => { _chooseImage(); SyncStyleButtons(); RefreshPreviews(); };

        return Stack(grid, change);
    }

    private void OnStyleChosen(RadioButton radio, IconStyle style)
    {
        if (_syncing || !radio.Checked) return;

        // 画像がまだ無いのに「好きな画像」を選ばれたら、先に画像を作ってもらう。
        // 途中でやめられたら、選択を元の見た目に戻す。
        if (style == IconStyle.Image && _skin() is null)
        {
            _chooseImage();
            if (_skin() is null) { SyncStyleButtons(); return; }
        }

        Settings.Style = style;
        _apply();
        RefreshPreviews();
    }

    private void SyncStyleButtons()
    {
        _syncing = true;
        try
        {
            foreach ((IconStyle style, RadioButton button) in _styleButtons)
                button.Checked = Settings.Style == style;
        }
        finally { _syncing = false; }
    }

    // ----- ③ タスクバーに出す ---------------------------------------------

    private Control BuildTaskbarSection()
    {
        var button = new Button
        {
            Text = "タスクバーに出す",
            FlatStyle = FlatStyle.System,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, Unit / 2, 0),
            Enabled = TrayPromotion.Supported
        };

        Label result = Para(
            TrayPromotion.Supported ? "" : "この Windows では下の手順で出してください。", Faint);
        result.Anchor = AnchorStyles.Left;

        button.Click += (_, _) =>
        {
            // 希望を覚えておく。Windows 側の設定は消えることがあるので毎回掛け直す。
            Settings.PromoteTray = true;
            bool ok = TrayPromotion.Promote();
            _apply();
            result.ForeColor = ok ? Color.FromArgb(130, 230, 160) : Color.FromArgb(255, 190, 120);
            // 失敗しても常駐側が数十秒は試し続けるので、言い切らずに待ってもらう
            result.Text = ok ? "出しました。" : "少し待ってください。変わらなければ下の手順で。";
        };

        var line = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, Unit / 3),
            BackColor = Color.Transparent
        };
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.Controls.Add(button, 0, 0);
        line.Controls.Add(result, 1, 0);

        return Stack(
            Para("Windows 11 は新しいアイコンを、最初は「∧」ボタンの中に隠します。\n" +
                 "下のボタンで表に出せます。", Faint),
            line,
            Para("うまくいかないときは、タスクバー右下の「∧」を押して、\n" +
                 "出てきたアイコンをタスクバーへドラッグしてください。", Faint));
    }

    // ----- ④ 見失わないために ---------------------------------------------

    private Control BuildOptionSection()
    {
        // 終了するとタスクバーから消える。exe の場所を覚えていない人は戻せなくなるので、
        // スタートメニューから探せるようにしておく。
        var startMenu = new CheckBox
        {
            Text = "スタートメニューに追加する（終了しても探し出せます）",
            Checked = StartMenuShortcut.Exists,
            ForeColor = Color.White,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, Unit / 4)
        };
        startMenu.CheckedChanged += (_, _) =>
        {
            if (_syncing) return;

            if (!startMenu.Checked) { StartMenuShortcut.Remove(); return; }
            if (StartMenuShortcut.Create()) return;

            _syncing = true;
            startMenu.Checked = false;
            _syncing = false;
            MessageBox.Show(this,
                "スタートメニューに追加できませんでした。\n" +
                "exe の場所をメモしておくか、右クリックで「送る」→「デスクトップ」を使ってください。",
                "TaskbarMeter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        var autoStart = new CheckBox
        {
            Text = "Windows を起動したら自動で始める",
            Checked = AutoStart.IsEnabled,
            ForeColor = Color.White,
            AutoSize = true,
            Margin = new Padding(0)
        };
        autoStart.CheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            AutoStart.Set(autoStart.Checked);
        };

        // 復帰手段は 5 系統あるのに、この画面にはチェック 2 つしか載っていなかった。
        // 初回セットアップを閉じた人が、戻りかたを知らないまま終わってしまう。
        Label howBack = Para(
            "この画面は、タスクバーのアイコンを右クリック →「設定…」でいつでも開けます。\n" +
            "Ctrl + Alt + M でアイコンを隠す／戻すの切り替えができます。", Faint);

        return Stack(startMenu, autoStart, howBack);
    }

    // ----- 下段 -----------------------------------------------------------

    /// <summary>本文の下に固定する帯。スクロールしてもボタンが隠れないようにする。</summary>
    private Control BuildButtonBar(bool firstRun)
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(Unit, Unit / 2, Unit, Unit / 2),
            BackColor = Card
        };

        var close = new Button
        {
            Text = firstRun ? "使いはじめる" : "閉じる",
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.System,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(Unit * 6, Unit * 2),
            Margin = new Padding(0)
        };
        bar.Controls.Add(close);
        AcceptButton = close;
        CancelButton = close;

        return bar;
    }

    // ----- 前面に出す -----------------------------------------------------

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool join);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>
    /// 確実に前面へ出す。
    ///
    /// exe をダブルクリック → SmartScreen の「実行」という流れだと、前面はエクスプローラーのまま。
    /// Windows は「いま前面にいるアプリ以外」からの前面化を拒むため、ふつうに `Activate()` を
    /// 呼んでもこの画面は後ろに隠れ、「案内画面が出てこない」と受け取られる。
    /// 前面のスレッドに入力状態を一時的に結び付けると、同じ入力キューの一員として扱われ、
    /// 前面化が通る。結び付けは必ず外すこと。
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        IntPtr foreground = GetForegroundWindow();
        uint theirs = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        uint ours = GetCurrentThreadId();
        bool attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);

        try
        {
            TopMost = true;
            TopMost = false;   // 出したあとは普通のウィンドウに戻す（常時最前面は邪魔なので）
            Activate();
            SetForegroundWindow(Handle);
        }
        finally
        {
            if (attached) AttachThreadInput(ours, theirs, false);
        }
    }

    // ----- プレビュー -----------------------------------------------------

    private void RefreshPreviews()
    {
        foreach (Panel panel in _previews) panel.Invalidate();
    }

    private void DrawPreview(Graphics g, Rectangle box, double ratio, string text, Color color)
        => DrawPreview(g, box, ratio, text, color, Settings.Style);

    private void DrawPreview(Graphics g, Rectangle box, double ratio, string text,
                             Color color, IconStyle style)
    {
        PixelSkin? skin = _skin();
        if (style == IconStyle.Image && skin is null) style = IconStyle.Face;

        using Icon icon = IconFactory.Create(ratio, text, color, style,
                                             mood: default,
                                             skin: skin, smoothSkin: Settings.SkinSmooth);
        using Bitmap bmp = icon.ToBitmap();

        // ドットを潰さずに拡大したいので最近傍で引き伸ばす
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.None;
        g.DrawImage(bmp, box);
    }
}

// ---------------------------------------------------------------------------
//  計測セッション
// ---------------------------------------------------------------------------

/// <summary>記録対象の指標。色と単位は記録開始時点のものを保持する。</summary>
internal sealed record TrackedMetric(string Id, string Name, Color Color, string Unit);

/// <summary>1 回の計測結果。</summary>
internal sealed class SessionResult
{
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public required List<TrackedMetric> Metrics { get; init; }
    public required List<DateTime> Times { get; init; }
    public required Dictionary<string, List<double>> Values { get; init; }   // 実値
    public required Dictionary<string, List<double>> Ratios { get; init; }   // 0.0〜1.0

    public TimeSpan Duration => End - Start;
}

/// <summary>
/// 計測中の値をためこむ。長時間まわしても破綻しないよう、一定数を超えたら
/// 1 つおきに間引いて解像度を半分に落としていく。
/// </summary>
internal sealed class SessionRecorder
{
    private const int MaxSamples = 20000;   // 1 秒間隔なら約 5.5 時間ぶん

    private readonly List<DateTime> _times = new();
    private readonly Dictionary<string, List<double>> _values = new();
    private readonly Dictionary<string, List<double>> _ratios = new();
    private List<TrackedMetric> _metrics = new();
    private DateTime _start;
    private int _skip;      // 間引き後、何個に 1 個だけ採用するか
    private int _counter;

    public bool Recording { get; private set; }

    public void Start(List<TrackedMetric> metrics)
    {
        _metrics = metrics;
        _times.Clear();
        _values.Clear();
        _ratios.Clear();
        foreach (TrackedMetric metric in metrics)
        {
            _values[metric.Id] = new List<double>();
            _ratios[metric.Id] = new List<double>();
        }
        _start = DateTime.Now;
        _skip = 1;
        _counter = 0;
        Recording = true;
    }

    /// <summary>この指標を記録しているか。表示を切っても読み続けるかの判定に使う。</summary>
    public bool Tracks(string id) => _values.ContainsKey(id);

    public void Add(DateTime time, Dictionary<string, Sample> samples)
    {
        if (!Recording) return;

        if (++_counter % _skip != 0) return;

        _times.Add(time);
        foreach (TrackedMetric metric in _metrics)
        {
            // 値が来なかった指標に 0 を積むと、グラフが床に落ちて平均も薄まる。
            // 取れなかったときは直前の値をそのまま伸ばす。
            List<double> values = _values[metric.Id];
            List<double> ratios = _ratios[metric.Id];
            if (samples.TryGetValue(metric.Id, out Sample sample))
            {
                values.Add(sample.Value);
                ratios.Add(sample.Ratio);
            }
            else
            {
                values.Add(values.Count > 0 ? values[^1] : 0);
                ratios.Add(ratios.Count > 0 ? ratios[^1] : 0);
            }
        }

        if (_times.Count >= MaxSamples) Thin();
    }

    /// <summary>
    /// 1 つおきに捨てて、以降の記録間隔も倍にする。
    /// 途中の要素を消すと後ろが毎回ずれるので、前から詰め直してから末尾を切る。
    /// 20000 点を 1 つずつ RemoveAt していたころは、5 時間半に 1 回
    /// アイコンが 1 秒近く止まっていた。
    /// </summary>
    private void Thin()
    {
        static void Halve<T>(List<T> list)
        {
            int write = 0;
            for (int read = 0; read < list.Count; read += 2) list[write++] = list[read];
            list.RemoveRange(write, list.Count - write);
        }

        Halve(_times);
        foreach (List<double> list in _values.Values) Halve(list);
        foreach (List<double> list in _ratios.Values) Halve(list);
        _skip *= 2;
    }

    public SessionResult Stop()
    {
        Recording = false;
        return new SessionResult
        {
            Start = _start,
            End = DateTime.Now,
            Metrics = new List<TrackedMetric>(_metrics),
            Times = new List<DateTime>(_times),
            Values = _values.ToDictionary(kv => kv.Key, kv => new List<double>(kv.Value)),
            Ratios = _ratios.ToDictionary(kv => kv.Key, kv => new List<double>(kv.Value))
        };
    }
}

/// <summary>計測結果のグラフと集計を表示するウィンドウ。</summary>
internal sealed class SessionResultForm : Form
{
    private static readonly Color Background = Color.FromArgb(30, 30, 34);
    private static readonly Color Panel = Color.FromArgb(22, 22, 26);
    private static readonly Color Grid = Color.FromArgb(60, 255, 255, 255);

    private readonly SessionResult _result;

    /// <summary>余白と寸法の基準。数値で決め打たず、文字の高さから取る。</summary>
    private int Unit => Math.Max(16, Font.Height);

    public SessionResultForm(SessionResult result)
    {
        _result = result;

        Text = "計測結果 - TaskbarMeter";
        // 画面の拡大表示（125/150/200%）では文字だけが大きくなる。
        // 寸法を px で決め打つと、150% で集計表の下の行が枠に隠れ、
        // 指標名も途中で切れた。他の画面と同じく Font.Height を基準にする。
        Font = new Font("Yu Gothic UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Font;

        int unit = Unit;
        ClientSize = new Size(unit * 40, unit * 27);
        MinimumSize = new Size(unit * 26, unit * 20);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = Color.White;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(unit / 2)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // 表示している指標ぶんの行 ＋ 見出し。多いときはグラフを潰さない範囲で止める
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,
                                        unit * Math.Clamp(_result.Metrics.Count + 2, 4, 10)));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildChart(), 0, 1);
        root.Controls.Add(BuildSummary(), 0, 2);
        root.Controls.Add(BuildButtons(), 0, 3);
        Controls.Add(root);
    }

    private Label BuildHeader()
    {
        TimeSpan span = _result.Duration;
        string length = span.TotalHours >= 1
            ? $"{(int)span.TotalHours} 時間 {span.Minutes} 分 {span.Seconds} 秒"
            : span.TotalMinutes >= 1
                ? $"{span.Minutes} 分 {span.Seconds} 秒"
                : $"{span.Seconds} 秒";

        return new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.White,
            Text = $"{_result.Start:yyyy/MM/dd HH:mm:ss}  〜  {_result.End:HH:mm:ss}    " +
                   $"（{length} / {_result.Times.Count} サンプル）"
        };
    }

    private Control BuildChart()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Panel };
        panel.Paint += (_, e) => DrawChart(e.Graphics, panel.ClientRectangle);
        panel.Resize += (_, _) => panel.Invalidate();
        return panel;
    }

    private void DrawChart(Graphics g, Rectangle area)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using var axisFont = new Font(Font.FontFamily, 8f);
        using var labelBrush = new SolidBrush(Color.FromArgb(200, 255, 255, 255));

        // 余白は「100%」というラベルの実寸から決める。px で決め打つと
        // 拡大表示のときに目盛りの文字がグラフに重なる。
        int line = axisFont.Height;
        int gutter = (int)Math.Ceiling(g.MeasureString("100%", axisFont).Width) + line / 2;
        var plot = new Rectangle(area.Left + gutter, area.Top + line * 2,
                                 Math.Max(10, area.Width - gutter - line),
                                 Math.Max(10, area.Height - line * 4));

        // 目盛り（0〜100%）
        using (var gridPen = new Pen(Grid))
        {
            for (int step = 0; step <= 4; step++)
            {
                int value = step * 25;
                float y = plot.Bottom - plot.Height * value / 100f;
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                g.DrawString($"{value}%", axisFont, labelBrush, area.Left, y - line / 2f);
            }
        }

        // 凡例
        float legendX = plot.Left;
        foreach (TrackedMetric metric in _result.Metrics)
        {
            using var swatch = new SolidBrush(metric.Color);
            float box = line * 0.8f;
            g.FillRectangle(swatch, legendX, area.Top + line * 0.4f, box, box);
            g.DrawString(metric.Name, axisFont, labelBrush, legendX + box + 4, area.Top + line * 0.2f);
            legendX += box + 4 + g.MeasureString(metric.Name, axisFont).Width + line;
        }

        int count = _result.Times.Count;
        if (count < 2) return;

        // 横方向は描画幅までしか意味がないので間引いて描く
        int stride = Math.Max(1, count / Math.Max(1, plot.Width));

        foreach (TrackedMetric metric in _result.Metrics)
        {
            List<double> ratios = _result.Ratios[metric.Id];
            var points = new List<PointF>();
            for (int i = 0; i < count; i += stride)
            {
                float x = plot.Left + plot.Width * (i / (float)(count - 1));
                float y = plot.Bottom - plot.Height * (float)Math.Clamp(ratios[i], 0, 1);
                points.Add(new PointF(x, y));
            }
            if (points.Count < 2) continue;

            using var pen = new Pen(metric.Color, 1.8f) { LineJoin = LineJoin.Round };
            g.DrawLines(pen, points.ToArray());
        }

        // 時刻ラベル
        g.DrawString(_result.Start.ToString("HH:mm:ss"), axisFont, labelBrush,
                     plot.Left, plot.Bottom + line * 0.3f);
        string endText = _result.End.ToString("HH:mm:ss");
        float endWidth = g.MeasureString(endText, axisFont).Width;
        g.DrawString(endText, axisFont, labelBrush, plot.Right - endWidth, plot.Bottom + line * 0.3f);
    }

    private Control BuildSummary()
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BackColor = Panel,
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None
        };
        // 「GPU 使用率 (Compute / 学習)」が入る幅を、拡大表示でも確保する
        int unit = Unit;
        list.Columns.Add("指標", unit * 11);
        list.Columns.Add("平均", unit * 5, HorizontalAlignment.Right);
        list.Columns.Add("最大", unit * 5, HorizontalAlignment.Right);
        list.Columns.Add("最小", unit * 5, HorizontalAlignment.Right);
        list.Columns.Add("単位", unit * 4);

        foreach (TrackedMetric metric in _result.Metrics)
        {
            List<double> values = _result.Values[metric.Id];
            if (values.Count == 0) continue;

            var item = new ListViewItem(metric.Name) { ForeColor = metric.Color };
            item.SubItems.Add(values.Average().ToString("0.0"));
            item.SubItems.Add(values.Max().ToString("0.0"));
            item.SubItems.Add(values.Min().ToString("0.0"));
            item.SubItems.Add(metric.Unit);
            list.Items.Add(item);
        }
        return list;
    }

    private Control BuildButtons()
    {
        int unit = Unit;
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, unit / 2, 0, 0)
        };

        Button Make(string text) => new()
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(unit * 6, unit * 2),
            FlatStyle = FlatStyle.System
        };

        Button close = Make("閉じる");
        close.Click += (_, _) => Close();

        Button save = Make("CSV で保存");
        save.Click += (_, _) => SaveCsv();

        panel.Controls.Add(close);
        panel.Controls.Add(save);
        CancelButton = close;   // Esc で閉じられるように
        return panel;
    }

    private void SaveCsv()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "CSV ファイル (*.csv)|*.csv",
            FileName = $"taskbarmeter_{_result.Start:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        try
        {
            var text = new StringBuilder();
            text.Append("時刻,経過秒");
            foreach (TrackedMetric metric in _result.Metrics)
                text.Append($",{metric.Name} ({metric.Unit})");
            text.AppendLine();

            for (int i = 0; i < _result.Times.Count; i++)
            {
                double elapsed = (_result.Times[i] - _result.Start).TotalSeconds;
                text.Append($"{_result.Times[i]:yyyy-MM-dd HH:mm:ss},{elapsed:0}");
                foreach (TrackedMetric metric in _result.Metrics)
                    text.Append($",{_result.Values[metric.Id][i]:0.00}");
                text.AppendLine();
            }

            // Excel が文字化けしないよう BOM 付き UTF-8 で書き出す
            File.WriteAllText(dialog.FileName, text.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存に失敗しました。\n{ex.Message}", "TaskbarMeter",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

// ---------------------------------------------------------------------------
//  下回り
// ---------------------------------------------------------------------------

/// <summary>Ctrl+Alt+M を受け取るためだけの、画面に出ない小さなウィンドウ。</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xB001;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkM = 0x4D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event Action? Pressed;
    public bool Registered { get; }

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
        Registered = RegisterHotKey(Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkM);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Registered) UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }
}

/// <summary>
/// パフォーマンスカウンタ名は OS 言語でローカライズされる（日本語 Windows で
/// "GPU Engine" は通らない）。レジストリの英語一覧と現在言語一覧を突き合わせて変換する。
/// </summary>
internal static class PerfNames
{
    private static readonly Dictionary<string, string> Map = Build();

    public static string Localize(string english)
        => Map.TryGetValue(english, out string? localized) ? localized : english;

    private static Dictionary<string, string> Build()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const string root = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib";
            using var englishKey = Registry.LocalMachine.OpenSubKey(root + @"\009");
            using var localKey = Registry.LocalMachine.OpenSubKey(root + @"\CurrentLanguage");

            if (englishKey?.GetValue("Counter") is not string[] englishList) return result;
            if (localKey?.GetValue("Counter") is not string[] localList) return result;

            // どちらも [index, name, index, name, ...] の並び
            var byIndex = new Dictionary<string, string>();
            for (int i = 0; i + 1 < localList.Length; i += 2)
                byIndex[localList[i]] = localList[i + 1];

            for (int i = 0; i + 1 < englishList.Length; i += 2)
                if (byIndex.TryGetValue(englishList[i], out string? localized))
                    result[englishList[i + 1]] = localized;
        }
        catch { /* 取れなければ英語名のまま使う */ }
        return result;
    }
}

/// <summary>設定は HKCU に保存する（管理者権限は不要）。</summary>
internal static class Settings
{
    private const string Key = @"Software\TaskbarMeter";

    public static DisplayMode Mode
    {
        get => Read("Mode", 0) == 1 ? DisplayMode.Auto : DisplayMode.Always;
        set => Write("Mode", (int)value);
    }

    public static int Threshold
    {
        get
        {
            int value = Read("Threshold", 20);
            return value is 5 or 10 or 20 or 30 or 50 ? value : 20;
        }
        set => Write("Threshold", value);
    }

    public static IconStyle Style
    {
        // Image(2) を取りこぼすと、画像から作ったアイコンが再起動のたびに内蔵キャラへ戻る
        get => Read("Style", 1) switch
        {
            0 => IconStyle.Number,
            2 => IconStyle.Image,
            _ => IconStyle.Face
        };
        set => Write("Style", (int)value);
    }

    public static bool SkinSmooth
    {
        get => Read("SkinSmooth", 1) == 1;
        set => Write("SkinSmooth", value ? 1 : 0);
    }

    public static bool HintShown
    {
        get => Read("HintShown", 0) == 1;
        set => Write("HintShown", value ? 1 : 0);
    }

    /// <summary>初回セットアップを一度でも終えたか。初めての起動を見分けるために使う。</summary>
    public static bool SetupDone
    {
        get => Read("SetupDone", 0) == 1;
        set => Write("SetupDone", value ? 1 : 0);
    }

    /// <summary>
    /// 「タスクバーに出す」を使うか。Windows 側の設定は消えることがあるので、
    /// 希望を覚えておいて起動のたびに掛け直す。
    /// </summary>
    public static bool PromoteTray
    {
        get => Read("PromoteTray", 0) == 1;
        set => Write("PromoteTray", value ? 1 : 0);
    }

    public static bool MetricEnabled(string id, bool fallback)
        => Read($"Metric_{id}_On", fallback ? 1 : 0) == 1;

    public static void SetMetricEnabled(string id, bool enabled)
        => Write($"Metric_{id}_On", enabled ? 1 : 0);

    public static Color MetricColor(string id, Color fallback)
    {
        int value = Read($"Metric_{id}_Color", -1);
        return value < 0 ? fallback : Color.FromArgb(value | unchecked((int)0xFF000000));
    }

    public static void SetMetricColor(string id, Color color)
        => Write($"Metric_{id}_Color", color.ToArgb() & 0x00FFFFFF);

    public static void ClearMetricColor(string id)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            key?.DeleteValue($"Metric_{id}_Color", throwOnMissingValue: false);
        }
        catch { /* 消せなくても既定色で動く */ }
    }

    private static int Read(string name, int fallback)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch { return fallback; }
    }

    private static void Write(string name, int value)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(Key);
            key?.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch { /* 保存できなくても動作は続ける */ }
    }
}

/// <summary>
/// Windows 11 は「初めて見るトレイアイコン」を既定でオーバーフロー（∧ ボタンの中）へ入れる。
/// 配布相手が exe をダブルクリックしても、タスクバーには何も出ないので「動いてない」と思われる。
/// ここが配布でいちばんつまずく場所なので、表に出すための操作を用意しておく。
///
/// Windows 11 は自分のアイコンの表示設定を HKCU\Control Panel\NotifyIconSettings に持っていて、
/// IsPromoted=1 で「タスクバーに出す」になる。HKCU なので管理者権限は要らない。
/// Windows 10 にはこのキーが無いため、その場合は何もせず false を返す（案内は画面側で出す）。
/// </summary>
internal static class TrayPromotion
{
    private const string Key = @"Control Panel\NotifyIconSettings";

    /// <summary>この Windows が「表に出す」設定を持っているか。</summary>
    public static bool Supported
    {
        get
        {
            try
            {
                using RegistryKey? root = Registry.CurrentUser.OpenSubKey(Key);
                return root is not null;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 自分の exe が出しているアイコンをすべて「タスクバーに出す」にする。
    /// エントリは一度アイコンを表示しないと作られないので、常駐開始後に呼ぶこと。
    /// </summary>
    public static bool Promote()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null) return false;

            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            if (root is null) return false;

            bool promoted = false;
            foreach (string name in root.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? entry = root.OpenSubKey(name, writable: true);
                    if (entry?.GetValue("ExecutablePath") is not string path) continue;
                    if (!string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;

                    entry.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                    promoted = true;
                }
                catch { /* 個別のエントリが読めなくても他を試す */ }
            }
            return promoted;
        }
        catch { return false; }
    }
}

/// <summary>
/// スタートメニューへのショートカット。
///
/// 「終了」したあと戻せなくなるのを防ぐための仕掛け。exe は bin の奥や
/// ダウンロードフォルダにあることが多く、配布相手は場所を覚えていない。
/// スタートメニューに置いておけば、スタートボタンから "taskbar" と打つだけで戻せる。
///
/// ショートカット (.lnk) の作成は COM の WScript.Shell に任せる。
/// 参照を増やしたくないので、型は ProgID から遅延バインドで取っている。
/// </summary>
internal static class StartMenuShortcut
{
    private static string LinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TaskbarMeter.lnk");

    public static bool Exists
    {
        get { try { return File.Exists(LinkPath); } catch { return false; } }
    }

    /// <summary>
    /// ショートカットが「いまの exe」を指しているか。
    /// 存在だけを見ていると、exe を別の場所へ移したあとも設定画面のチェックは
    /// 付いたまま、実際には消えた場所を指した死んだショートカットが残る。
    /// </summary>
    public static bool PointsHere()
    {
        try
        {
            if (!File.Exists(LinkPath)) return false;

            string? exe = Environment.ProcessPath;
            if (exe is null) return true;

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return true;

            object? shell = Activator.CreateInstance(shellType);
            if (shell is null) return true;

            object? link = shellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { LinkPath });
            if (link is null) return true;

            object? target = link.GetType().InvokeMember(
                "TargetPath", BindingFlags.GetProperty, null, link, null);

            return string.Equals(target as string, exe, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }   // 読めないなら触らない
    }

    public static bool Create()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null) return false;

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;

            object? shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;

            object? link = shellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { LinkPath });
            if (link is null) return false;

            Type linkType = link.GetType();
            void Set(string name, string value) => linkType.InvokeMember(
                name, BindingFlags.SetProperty, null, link, new object[] { value });

            Set("TargetPath", exe);
            Set("WorkingDirectory", Path.GetDirectoryName(exe) ?? "");
            Set("Description", "タスクバーに CPU や GPU の使用率を表示する");
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);

            return File.Exists(LinkPath);
        }
        catch { return false; }
    }

    public static void Remove()
    {
        try { if (File.Exists(LinkPath)) File.Delete(LinkPath); }
        catch { /* 消せなくても実害はない */ }
    }
}

/// <summary>HKCU の Run キーに登録するだけなので管理者権限は不要。</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskbarMeter";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is not null;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 登録されている値が「いまの exe」を指しているか。
    /// 存在だけを見ていると、exe を移したあとも設定画面のチェックは付いたまま、
    /// 実際には起動しない状態になる。
    /// </summary>
    public static bool PointsHere()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string stored) return false;

            string? exe = Environment.ProcessPath;
            if (exe is null) return true;

            return string.Equals(stored.Trim('"'), exe, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            // CreateSubKey にしてあるのは、Run キーが無い環境で
            // 「チェックは入るのに何も保存されない」を避けるため。
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
            {
                string? path = Environment.ProcessPath;
                if (path is not null) key.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* 権限がなければ何もしない */ }
    }
}
