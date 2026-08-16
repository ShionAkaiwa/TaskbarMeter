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
internal readonly record struct IconMood(int Tick = 0, bool Recording = false);

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
        _timer.Start();

        // 初回はここで計測を始めない。カウンタの組み立てに時間がかかり、
        // その間なにも出ないまま待たされるため、先に案内画面を出す。
        if (!_pendingSetup) Update();

        if (!_hotkey.Registered)
        {
            _autoItem.Enabled = false;
            if (_mode == DisplayMode.Auto) SetMode(DisplayMode.Always);
            Notify("Ctrl+Alt+M が他のアプリと競合しており登録できませんでした。" +
                   "「高負荷のときだけ表示」は無効にしています。", ToolTipIcon.Warning);
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
                SyncSlots();
                Update();
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
                    Update();
                }
            };
            root.DropDownItems.Add(item);
        }

        root.DropDownItems.Add(new ToolStripSeparator());
        var reset = new ToolStripMenuItem("すべて既定の色に戻す");
        reset.Click += (_, _) =>
        {
            foreach (Metric metric in _metrics) Settings.ClearMetricColor(metric.Id);
            Update();
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
            Update();
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

        SyncSlots();
        RefreshModeChecks();

        _syncingMenu = true;
        try { _syncMenu(); }
        finally { _syncingMenu = false; }

        Update();
    }

    /// <summary>画像を選び、ドット絵に変換してプレビューし、採用されたら保存する。</summary>
    private void ChooseImage()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "ドット絵にする画像を選ぶ",
            Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|すべてのファイル|*.*"
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        try
        {
            using var preview = new SkinPreviewForm(dialog.FileName);
            if (preview.ShowDialog() != DialogResult.OK || preview.Result is null) return;

            preview.Result.SaveAsDefault();
            Settings.SkinSmooth = preview.Smooth;
            _skinSmooth = preview.Smooth;
            _skin = preview.Result;
            _style = IconStyle.Image;
            Settings.Style = IconStyle.Image;
            Update();
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
            _slots[metric.Id] = new Slot { Metric = metric, Notify = notify };
        }
        SyncSlots();
    }

    /// <summary>どの指標を表示するかを設定から読み直す。器は作り直さず表示だけ切り替える。</summary>
    private void SyncSlots()
    {
        foreach (Slot slot in _slots.Values)
        {
            slot.Enabled = Settings.MetricEnabled(slot.Metric.Id, slot.Metric.DefaultEnabled);
            slot.Notify.Visible = slot.Enabled && _iconsVisible;
        }
    }

    private IEnumerable<Slot> ActiveSlots => _slots.Values.Where(s => s.Enabled);

    private int EnabledCount => _slots.Values.Count(s => s.Enabled);

    // ----- 毎秒の更新 -----------------------------------------------------

    private void Update()
    {
        if (_tick++ % 5 == 0)
            foreach (Slot slot in ActiveSlots) slot.Metric.Refresh();

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
        double peak = 0;
        foreach (Slot slot in ActiveSlots)
        {
            Sample sample = slot.Metric.Read();
            samples[slot.Metric.Id] = sample;
            peak = Math.Max(peak, sample.Ratio);
        }

        _lastSamples = samples;
        WatchVramPressure(samples);
        if (_recorder.Recording)
        {
            _recorder.Add(DateTime.Now, samples);
            WatchForFinish(samples);
        }

        if (peak * 100 >= _threshold) _cooldown = CooldownSeconds;
        else if (_cooldown > 0) _cooldown--;

        bool shouldShow = _forceShow
                          || (!_forceHide && (_mode == DisplayMode.Always || _cooldown > 0));
        SetVisible(shouldShow);
        if (!_iconsVisible) return;

        var mood = new IconMood(_tick, _recorder.Recording);
        foreach (Slot slot in ActiveSlots)
        {
            Sample sample = samples[slot.Metric.Id];
            Color color = Settings.MetricColor(slot.Metric.Id, slot.Metric.DefaultColor);

            IconStyle style = _style == IconStyle.Image && _skin is null ? IconStyle.Face : _style;
            Icon fresh = IconFactory.Create(sample.Ratio, sample.Text, color, style,
                                            mood, _skin, _skinSmooth);
            slot.Notify.Icon = fresh;
            slot.Current?.Dispose();
            slot.Current = fresh;

            slot.Notify.Text = sample.Tooltip;
        }
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
                _vramNotified = true;
                Notify($"VRAM が {(int)Math.Round(vram.Ratio * 100)}% です。" +
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
            _finishNotified = true;
            Notify("GPU が 3 分間ほとんど動いていません。処理が終わったかもしれません。",
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
        Update();
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
        Update();
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

    private void Notify(string message, ToolTipIcon icon, string title = "TaskbarMeter")
    {
        Slot? first = ActiveSlots.FirstOrDefault();
        first?.Notify.ShowBalloonTip(5000, title, message, icon);
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

        DialogResult answer = MessageBox.Show(
            $"TaskbarMeter を終了します。\n\n{howToReturn}",
            "TaskbarMeter", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);

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
    private List<PerformanceCounter> _counters = new();
    private bool _built;

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
        if (!Available) return;
        if (_built && !Volatile) return;

        try
        {
            var category = new PerformanceCounterCategory(PerfNames.Localize(CategoryEnglish));
            string[] instances = category.GetInstanceNames().Where(Match).ToArray();
            string counterName = PerfNames.Localize(CounterEnglish);

            var fresh = instances
                .Select(n => new PerformanceCounter(category.CategoryName, counterName, n, readOnly: true))
                .ToList();

            foreach (PerformanceCounter counter in fresh)
            {
                try { counter.NextValue(); } catch { /* 消えたインスタンスは無視 */ }
            }

            foreach (PerformanceCounter old in _counters) old.Dispose();
            _counters = fresh;
            _built = true;
        }
        catch
        {
            Available = false;
        }
    }

    protected double Collect()
    {
        if (!_built) Refresh();

        double total = 0;
        foreach (PerformanceCounter counter in _counters)
        {
            try
            {
                float value = counter.NextValue();
                total = UseMax ? Math.Max(total, value) : total + value;
            }
            catch { /* 消えたインスタンスは無視 */ }
        }
        return total;
    }

    public override void Dispose()
    {
        foreach (PerformanceCounter counter in _counters) counter.Dispose();
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
        if (!Available) return new Sample(0, "--", "GPU 3D 取得不可");
        double ratio = Math.Clamp(Collect() / 100.0, 0, 1);
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
        if (!Available) return new Sample(0, "--", "GPU Compute 取得不可");
        double ratio = Math.Clamp(Collect() / 100.0, 0, 1);
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
        if (!Available) return new Sample(0, "--", "VRAM 取得不可");

        double used = Collect();
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
        if (!Available) return new Sample(0, "--", "ディスク 取得不可");
        double ratio = Math.Clamp(Collect() / 100.0, 0, 1);
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

    protected override bool Match(string instance)
        => !instance.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
        && !instance.Contains("isatap", StringComparison.OrdinalIgnoreCase)
        && !instance.Contains("Teredo", StringComparison.OrdinalIgnoreCase);

    public override Sample Read()
    {
        if (!Available) return new Sample(0, "--", $"{Name} 取得不可");

        double mbps = Collect() * 8 / 1_000_000.0;
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
    /// ドット絵キャラの下地。
    /// L=上のハイライト / C=本体色 / c=下の陰 / #=輪郭 / .=透明。
    /// 上を明るく下を暗くしておくと、16px でも平たい丸ではなく球体に見える。
    /// </summary>
    private static readonly string[] BaseSprite =
    {
        ".....######.....",
        "...##LLLLLL##...",
        "..#LLLLLLLLLL#..",
        ".#LLLLLLLLLLLL#.",
        ".#CCCCCCCCCCCC#.",
        "#CCCCCCCCCCCCCC#",
        "#CCCCCCCCCCCCCC#",
        "#CCCCCCCCCCCCCC#",
        "#CCCCCCCCCCCCCC#",
        ".#CCCCCCCCCCCC#.",
        ".#cccccccccccc#.",
        "..#cccccccccc#..",
        "...##cccccc##...",
        ".....######.....",
        "................",
        "................"
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

        return style switch
        {
            IconStyle.Face => Render(BuildFace(percent, mood), tone),
            IconStyle.Image when skin is not null
                => CreateFromSkin(percent, tone, skin, smoothSkin, mood.Recording),
            IconStyle.Image => Render(BuildFace(percent, mood), tone),
            _ => Render(BuildNumber(percent, text, mood.Recording), tone)
        };
    }

    // ----- グリッド共通 ---------------------------------------------------

    /// <summary>組み上げたグリッドを、トレイの実表示サイズちょうどに敷き詰めて Icon にする。</summary>
    private static Icon Render(char[][] grid, Color tone)
    {
        int size = Math.Clamp(SystemInformation.SmallIconSize.Width, 16, 64);

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            PaintSprite(g, grid, size, tone);
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

    /// <summary>
    /// 下 2 行のゲージ。両端を 1 ドット空けて角が丸く見えるようにしてある。
    /// しきい値を超えたら 'H'（赤）で塗り、本体の色を濁らせずに危険を伝える。
    /// </summary>
    private static void PaintGauge(char[][] grid, int percent)
    {
        char on = percent >= HotPercent ? 'H' : 'C';
        int filled = (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * 14);
        for (int x = 1; x <= 14; x++)
        {
            char c = x - 1 < filled ? on : 'D';
            for (int y = GaugeTop; y < G; y++) grid[y][x] = c;
        }
    }

    /// <summary>ゲージが赤に切り替わる負荷。</summary>
    private const int HotPercent = 85;

    private static Color Hot(Color tone) => Blend(tone, HotTone, 0.85);

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
    private static char[][] BuildFace(int percent, IconMood mood)
    {
        char[][] grid = BaseSprite.Select(row => row.ToCharArray()).ToArray();

        int stage = Stage(percent);
        bool blink = mood.Tick % 9 == 0;
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

        // ----- 目（左目 x=3..5 / 右目 x=10..12、高さ y=5..8） -----
        if (stage == 4)
        {
            // × 目
            Pair(5, 3, 'K'); Pair(5, 5, 'K');
            Pair(6, 4, 'K');
            Pair(7, 3, 'K'); Pair(7, 5, 'K');
        }
        else if (closedEyes)
        {
            // 閉じ目。両端を上げた弧にすると、ただの横線より気持ちよさそうに見える。
            Pair(6, 3, 'K'); Pair(6, 5, 'K');
            Pair(7, 4, 'K');
        }
        else if (stage == 2)
        {
            // ^ ^ の笑い目
            Pair(6, 4, 'K');
            Pair(7, 3, 'K'); Pair(7, 4, 'K'); Pair(7, 5, 'K');
        }
        else
        {
            OvalEye(3, 5);
            OvalEye(10, 5);

            if (stage == 3) Pair(6, 4, 'W');   // 瞳だけ小さく残して「見開き」
            else Pair(6, 3, 'W');              // つやのハイライト
        }

        // ----- ほっぺ -----
        if (stage <= 2)
        {
            Pair(9, 2, 'P'); Pair(9, 3, 'P');
            if (stage == 2) { Pair(8, 2, 'P'); Pair(8, 3, 'P'); }   // ごきげんは濃いめ
        }

        // ----- 口 -----
        switch (stage)
        {
            case 0:
                Pair(11, 7, 'K');                                   // ちいさい口
                break;
            case 1:
                Pair(11, 6, 'K'); Pair(11, 7, 'K');                 // 一文字
                break;
            case 2:
                Pair(10, 6, 'K'); Pair(10, 7, 'K');                 // 開いたにこにこ
                Pair(11, 7, 'K');
                break;
            case 3:
                Pair(10, 5, 'K'); Pair(10, 7, 'K');                 // ぎざぎざ
                Pair(11, 6, 'K');
                break;
            default:
                for (int y = 10; y <= 12; y++)
                    for (int x = 6; x <= 9; x++) Set(y, x, 'K');    // 大きく開いた口
                break;
        }

        // ----- 付属物 -----
        if (stage == 0)
        {
            // zzz
            Set(0, 13, 'W'); Set(0, 14, 'W'); Set(0, 15, 'W');
            Set(1, 14, 'W');
            Set(2, 13, 'W'); Set(2, 14, 'W'); Set(2, 15, 'W');
        }
        else if (stage >= 3)
        {
            // 汗。限界のときは反対側にもう 1 粒足す。
            Set(2, 1, 'B'); Set(3, 0, 'B'); Set(3, 1, 'B'); Set(4, 0, 'B'); Set(4, 1, 'B');
            if (stage == 4)
            {
                Set(2, 14, 'B'); Set(3, 15, 'B'); Set(3, 14, 'B');
                Set(4, 15, 'B'); Set(4, 14, 'B');
            }
        }

        if (mood.Recording) PaintRecordingDot(grid);
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

    private static void PaintSprite(Graphics g, char[][] grid, int size, Color tone)
    {
        var palette = new Dictionary<char, SolidBrush>
        {
            // 輪郭は真っ黒ではなく本体色を少し混ぜた暗色にしてある。硬さが取れて印象が柔らかい。
            ['#'] = new SolidBrush(Color.FromArgb(230, Blend(Color.FromArgb(16, 16, 22), tone, 0.22))),
            ['C'] = new SolidBrush(tone),
            ['H'] = new SolidBrush(Hot(tone)),
            ['L'] = new SolidBrush(Blend(tone, Color.White, 0.26)),
            ['c'] = new SolidBrush(Blend(tone, Color.FromArgb(20, 20, 30), 0.22)),
            ['K'] = new SolidBrush(Color.FromArgb(238, 16, 16, 20)),
            ['W'] = new SolidBrush(Color.White),
            ['P'] = new SolidBrush(Blend(tone, Color.FromArgb(255, 118, 140), 0.55)),
            ['B'] = new SolidBrush(Color.FromArgb(240, 130, 210, 255)),
            // ゲージの下地。薄すぎるとアイコンが欠けて見えるので、
            // 「まだ伸びていない目盛り」だと分かる程度には出しておく。
            ['D'] = new SolidBrush(Color.FromArgb(150, 148, 148, 160)),
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
    private static Icon CreateFromSkin(int percent, Color tone, PixelSkin skin,
                                       bool smooth, bool recording)
    {
        // 実際に表示される大きさ。100% 表示なら 16、150% なら 24、200% なら 32。
        int size = Math.Clamp(SystemInformation.SmallIconSize.Width, 16, 64);
        int stage = Stage(percent);

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = smooth
                ? InterpolationMode.HighQualityBicubic
                : InterpolationMode.NearestNeighbor;

            using (Bitmap source = skin.ToBitmap())
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

            if (stage == 0)
            {
                using var white = new SolidBrush(Color.White);
                foreach ((int y, int x) in new[]
                         { (0, 13), (0, 14), (0, 15), (1, 14), (2, 13), (2, 14), (2, 15) })
                    g.FillRectangle(white, Cell(x, y));
            }
            else if (stage >= 3)
            {
                using var sweat = new SolidBrush(Color.FromArgb(245, 130, 210, 255));
                foreach ((int y, int x) in new[] { (2, 1), (3, 0), (3, 1), (4, 0), (4, 1) })
                    g.FillRectangle(sweat, Cell(x, y));
            }

            // 下部のゲージ。ドット絵モードと同じ「下 2 行・両端 1 ドット空け」に揃える。
            float barTop = GaugeTop * u;
            float barLeft = u;
            float barWidth = 14 * u;
            using (var off = new SolidBrush(Color.FromArgb(150, 148, 148, 160)))
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

    /// <summary>段階ごとの色補正。1 と 3 は素の色のままなので null を返す。</summary>
    private static ImageAttributes? StageEffect(int stage)
    {
        if (stage is 1 or 3) return null;

        float scale = stage switch { 0 => 0.62f, 2 => 1.12f, _ => 1.0f };
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
internal sealed class PixelSkin
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

    public Bitmap ToBitmap()
    {
        var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                bmp.SetPixel(x, y, Pixels[y, x]);
        return bmp;
    }

    public void SaveAsDefault()
    {
        try
        {
            string path = SavedPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using Bitmap bmp = ToBitmap();
            bmp.Save(path, ImageFormat.Png);
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
        => Result = PixelSkin.FromImage(_source, _resolution, _removeBackground, _posterize);

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

        using Bitmap bmp = Result.ToBitmap();
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = Smooth
            ? InterpolationMode.HighQualityBicubic
            : InterpolationMode.NearestNeighbor;
        g.DrawImage(bmp, new Rectangle(0, 0, PreviewBox, PreviewBox));
    }

    private void DrawActualSizes(Graphics g)
    {
        if (Result is null) return;

        using Bitmap bmp = Result.ToBitmap();
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

        // 全部消すと右クリックする先が無くなり、設定に戻れなくなる
        if (!box.Checked && _metricBoxes.Count(b => b.Checked) == 0)
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
            grid.Controls.Add(strip, 0, rowIndex);
            _previews.Add(strip);

            var radio = new RadioButton
            {
                Text = label,
                Checked = Settings.Style == captured,
                ForeColor = Color.White,
                AutoSize = true,
                Margin = new Padding(0)
            };
            radio.CheckedChanged += (_, _) => OnStyleChosen(radio, captured);
            _styleButtons.Add((captured, radio));

            grid.Controls.Add(Stack(radio, Para(note, Faint)), 1, rowIndex);
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rowIndex++;
        }

        var change = new Button
        {
            Text = "画像を選び直す…",
            FlatStyle = FlatStyle.System,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, Unit / 3, 0, 0)
        };
        change.Click += (_, _) => { _chooseImage(); RefreshPreviews(); };

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

        return Stack(startMenu, autoStart);
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

    public void Add(DateTime time, Dictionary<string, Sample> samples)
    {
        if (!Recording) return;

        if (++_counter % _skip != 0) return;

        _times.Add(time);
        foreach (TrackedMetric metric in _metrics)
        {
            samples.TryGetValue(metric.Id, out Sample sample);
            _values[metric.Id].Add(sample.Value);
            _ratios[metric.Id].Add(sample.Ratio);
        }

        if (_times.Count >= MaxSamples) Thin();
    }

    /// <summary>1 つおきに捨てて、以降の記録間隔も倍にする。</summary>
    private void Thin()
    {
        for (int i = _times.Count - 1; i >= 0; i -= 2) _times.RemoveAt(i);
        foreach (List<double> list in _values.Values)
            for (int i = list.Count - 1; i >= 0; i -= 2) list.RemoveAt(i);
        foreach (List<double> list in _ratios.Values)
            for (int i = list.Count - 1; i >= 0; i -= 2) list.RemoveAt(i);
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

    public SessionResultForm(SessionResult result)
    {
        _result = result;

        Text = "計測結果 - TaskbarMeter";
        ClientSize = new Size(940, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = Color.White;
        MinimumSize = new Size(640, 480);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

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
            Font = new Font("Yu Gothic UI", 10.5f, FontStyle.Bold),
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

        var plot = new Rectangle(area.Left + 52, area.Top + 34,
                                 Math.Max(10, area.Width - 68),
                                 Math.Max(10, area.Height - 62));

        using var axisFont = new Font("Yu Gothic UI", 8f);
        using var labelBrush = new SolidBrush(Color.FromArgb(200, 255, 255, 255));

        // 目盛り（0〜100%）
        using (var gridPen = new Pen(Grid))
        {
            for (int step = 0; step <= 4; step++)
            {
                int value = step * 25;
                float y = plot.Bottom - plot.Height * value / 100f;
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                g.DrawString($"{value}%", axisFont, labelBrush, area.Left + 6, y - 8);
            }
        }

        // 凡例
        float legendX = plot.Left;
        foreach (TrackedMetric metric in _result.Metrics)
        {
            using var swatch = new SolidBrush(metric.Color);
            g.FillRectangle(swatch, legendX, area.Top + 10, 12, 12);
            g.DrawString(metric.Name, axisFont, labelBrush, legendX + 16, area.Top + 8);
            legendX += 16 + g.MeasureString(metric.Name, axisFont).Width + 18;
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
                     plot.Left, plot.Bottom + 6);
        string endText = _result.End.ToString("HH:mm:ss");
        float endWidth = g.MeasureString(endText, axisFont).Width;
        g.DrawString(endText, axisFont, labelBrush, plot.Right - endWidth, plot.Bottom + 6);
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
        list.Columns.Add("指標", 240);
        list.Columns.Add("平均", 110, HorizontalAlignment.Right);
        list.Columns.Add("最大", 110, HorizontalAlignment.Right);
        list.Columns.Add("最小", 110, HorizontalAlignment.Right);
        list.Columns.Add("単位", 80);

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
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 0, 0)
        };

        var close = new Button { Text = "閉じる", Width = 110, Height = 32, FlatStyle = FlatStyle.System };
        close.Click += (_, _) => Close();

        var save = new Button { Text = "CSV で保存", Width = 130, Height = 32, FlatStyle = FlatStyle.System };
        save.Click += (_, _) => SaveCsv();

        panel.Controls.Add(close);
        panel.Controls.Add(save);
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

    public static void Set(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
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
