# TaskbarMeter

Windows のタスクバー（通知領域）に CPU / GPU などの使用率をアイコンで常時表示する常駐アプリ。
個人開発。作者は学生で、深層学習の研究に使う想定。友人にも配布したいので「exe 1個で動く」ことを重視している。

## 環境

- .NET 8 / C# / WinForms（単一プロジェクト、単一ファイル `Program.cs`）
- 開発環境は Windows 11、日本語ロケール、GPU は NVIDIA RTX 3060 Ti（VRAM 8GB）
- 外部ライブラリは `System.Diagnostics.PerformanceCounter` のみ。これ以上依存を増やさない方針

## ビルドと実行

```
build.cmd
```

これ 1 つで「常駐プロセスの終了 → `dotnet publish -c Release` → 新しい exe を起動」まで行う。
**常駐中の exe はロックされるため、必ず先にプロセスを落とすこと**（build.cmd がやっている）。

出力: `bin\Release\net8.0-windows\win-x64\publish\TaskbarMeter.exe`
self-contained / single-file なので、配布はこの exe 1 個で完結する。

Git 管理下にある。壊したら `git checkout .` で戻せる。段階ごとにコミットすること。

## ファイル構成

| ファイル | 内容 |
|---|---|
| `Program.cs` | 全実装。約 2400 行 |
| `TaskbarMeter.csproj` | 配布設定（self-contained, PublishSingleFile） |
| `build.cmd` | 開発用。プロセス終了 → publish → 起動。日本語を含めない（文字コードで壊れた実績あり） |
| `release.cmd` | 配布用。publish → `dist\TaskbarMeter.zip` と `dist\release-notes.md`。同じく日本語を含めない |
| `dist-template\` | ZIP に同梱するファイル（`お読みください.txt`） |
| `release-notes.template.md` | リリースページの雛形。`{DATE}` が置換される |
| `はじめかた.md` | 配布相手（初心者）向けのセットアップ手順 |
| `説明書.md` | 全機能の説明とトラブル対処 |
| `仕様書.md` | 中身の仕組み。先生に見せる前提の技術説明 |
| `README.md` | 開発者向けの概要と配布手順 |

**ドキュメントは 4 本とも役割が違う。機能を変えたら、該当するものを直すこと。**
各ファイルの末尾に他の 3 本へのリンクがある。増やしたらそこにも足すこと。

### `README.md` と `はじめかた.md` は作者が直接書き換えている

この 2 本は配布相手が読むもので、作者が GitHub 上で手を入れている。
**文体を整えにいかないこと。** 口語寄り・感嘆符多めなのは意図的で、
受け取る側の心理的なハードルを下げるためのもの。

決まっていること:

- **exe の具体的な設置パスは書かない**（作者が意図的に消した）。
  「うっかり消さない場所がおすすめ」までにとどめる
- README はダウンロード導線で役割終了。詳しい話は他の 3 本に送る
- 作業前に `git pull` すること。ローカルが遅れていることがある

## アーキテクチャ

```
Program.Main
  └ MeterContext : ApplicationContext     … ウィンドウを持たない本体
       ├ List<Metric>                     … 指標。1 指標 = 1 トレイアイコン
       ├ Dictionary<string, Slot>         … 有効な指標ぶんの NotifyIcon
       ├ IconFactory                      … アイコン画像の生成
       ├ SetupForm                        … 初回セットアップ兼 設定画面
       ├ TrayPromotion                    … Windows 11 のオーバーフロー対策
       ├ SessionRecorder / SessionResultForm … 計測とグラフ表示
       ├ PixelSkin / SkinPreviewForm      … 画像→アイコン変換
       ├ HotkeyWindow                     … Ctrl+Alt+M
       └ Settings                         … HKCU への保存
```

### アイコン描画（IconFactory）

**3 モードすべてが「16x16 の論理グリッド `char[][]` を組む → 実表示サイズに敷き詰める」**
という同じ手順に乗っている。数字モードも `Glyphs`（5x9 のドット字形）を持っていて、
GDI+ の文字描画は使っていない。

- `BuildFace()` … キャラ。`Pair(y, x, c)` で左半分だけ書けば右は自動で折り返す
  - **例外**: 「待ちぼうけ」のよそ見だけは `Pair` を使わない。両目を同じ向きへ
    ずらす必要があり、対称にずらすと目が離れて驚き顔になる
- `BuildNumber()` … 数字。白ドットを置いて `Outline()` で 1 ドットの縁を付ける
- `PaintGauge()` … 下 2 行（y=14,15）のゲージ。3 モード共通
- `Render()` … グリッドを `SystemInformation.SmallIconSize` ちょうどに描いて Icon 化

見た目を変えるときは基本ここだけ触ればよい。

### 設定画面（SetupForm）

初回起動の案内と、ふだんの設定を 1 枚で兼ねている。変更はその場で保存して
`MeterContext.ReloadFromSettings()` を呼ぶので、選んでいる最中にトレイが変化する。

右クリックメニューでも同じ設定ができるため、**両者のチェック状態がずれないように
`_syncMenu`（各項目の同期処理を足し込んだ Action）と `_syncingMenu` ガードがある**。
メニュー項目を増やすときは `_syncMenu +=` も足すこと。

### Metric（指標）

`Metric` 抽象クラスを継承し、`MetricCatalog.CreateAll()` に足せば、メニュー・色設定・計測記録に自動で載る。
**指標を追加するときはここだけ触ればよい**設計にしてある。

実装済み: CPU / メモリ / GPU(3D) / GPU(Compute) / VRAM / ディスク / ネット受信 / ネット送信

`Read()` は `Sample(Ratio, Text, Tooltip, Value, Unit)` を返す。
`Ratio` は 0〜1 でゲージ用、`Value` と `Unit` は記録・CSV 用の実値（% / GB / Mbps）。
`Text` はアイコンに描く文字。`Glyphs` にある `0-9 . + -` しか描けないので注意。

`ShortName` は設定画面など幅の狭い場所で使う名前。省略すると `Name` がそのまま使われるので、
名前が長い指標のときだけ書けばよい。

パフォーマンスカウンタ系は `CounterMetric` を継承すると、インスタンス列挙・張り替え・例外処理を任せられる。
`Volatile => true` にすると、プロセス単位で入れ替わるインスタンス（GPU）に対応して定期的に張り替える。

## ハマりどころ（重要・再発しやすい）

1. **パフォーマンスカウンタ名は OS 言語でローカライズされる**
   日本語 Windows では `new PerformanceCounter("GPU Engine", ...)` は通らない。
   `PerfNames.Localize()` がレジストリの `Perflib\009`（英語）と `Perflib\CurrentLanguage` を
   突き合わせて変換している。**新しいカウンタを使うときは必ずこれを通すこと**。

2. **トレイアイコンの表示サイズは DPI 依存**（100%=16px, 150%=24px, 200%=32px）
   32px 固定で描いて Windows に縮小させるとボケる。`SystemInformation.SmallIconSize` を取得し、
   **実サイズちょうどに描く**こと。ドット絵モードはアンチエイリアスを切っている。

3. **`Bitmap.GetHicon()` は必ず `DestroyIcon` で解放する**（`IconFactory.ToIcon`）
   毎秒生成しているので、漏らすと GDI ハンドルを食い潰す。差し替えた古い `Icon` の Dispose も必要。

4. **アイコンを全部消すと設定メニューに触れなくなる**
   復帰手段を 5 系統用意してある。壊さないこと。
   - `Ctrl+Alt+M`（`HotkeyWindow`）
   - exe を再度起動すると、名前付き Event 経由で常駐側が設定画面を開く
   - 「表示する項目」は最後の 1 つを外せないようにガードしてある
   - スタートメニューのショートカット（`StartMenuShortcut`、初回起動時に作成）
   - 「終了」は確認を挟み、戻しかたと exe の場所を出す（`ConfirmQuit`）

5. **GPU の深層学習負荷は 3D エンジンではなく Compute エンジンに出る**
   `engtype_3D` だけを見ていると学習中でも 0% に見える。`engtype_Compute` / `engtype_Cuda` を拾っている。

6. **Windows 11 は初めて見るトレイアイコンをオーバーフロー（∧ の中）に入れる**
   配布相手が exe をダブルクリックしても、タスクバーには何も出ない。**配布でいちばん
   つまずくのはここ**。`TrayPromotion` が `HKCU\Control Panel\NotifyIconSettings` の
   自分の exe のエントリに `IsPromoted=1` を書いて表に出す（管理者権限は不要）。
   Windows 10 にはこのキーが無いので、その場合は手動手順を画面に出している。

   このエントリは**一度アイコンを表示しないと作られない**うえ、**作られるまで数秒かかる**。
   1 回だけ試す作りにすると早すぎて空振りし、二度と表に出てこない。
   `_promoteTries` で成功するまで 30 秒ほど試し続けること。

   なお、このエントリを**手で消すと explorer が作り直さないことがある**（キャッシュを
   持っているため）。アイコンはオーバーフローに入ったまま戻せなくなる。
   復帰にはサインアウトか explorer の再起動が要る。デバッグで消すときは注意。

9. **トレイアイコンは「exe のパス + UID」で識別され、UID は NotifyIcon を作った順に振られる**
   表示の ON/OFF で `NotifyIcon` を作り直すと UID がずれ、`IsPromoted` が古いエントリに
   取り残されて、アイコンがまたオーバーフローに引っ込む。実際にこれが起き、
   「3 番目の指標である GPU(3D) が UID 6 に居る」という形でレジストリに現れた。
   **`CreateSlots()` で全指標ぶんを同じ順に作り、以後は `Visible` だけを切り替えること。**
   ここを「使う指標だけ作る」に戻すと再発する。

7. **高負荷を「本体の色を赤に寄せて」表すと、指標の見分けがつかなくなる**
   青×サーモンは灰色、ミント×サーモンは茶色になり、100% でどの指標も同じ色に潰れた。
   一番「どれが限界か」を知りたい瞬間に使えなくなる。**本体は指標の色を保ち、
   危険はゲージの赤（`PaintGauge` の `'H'`）と表情で伝える**方式にしてある。

8. **モーダルは `Application.Run` が始まってから開く**
   `MeterContext` のコンストラクタはメッセージループの前に走るので、そこで `ShowDialog()`
   すると不安定になる。初回セットアップは `_pendingSetup` フラグを立てて、
   最初のタイマー tick で開いている。

## 設定の保存先

- `HKEY_CURRENT_USER\Software\TaskbarMeter` … 表示モード、しきい値、見た目、指標ごとの ON/OFF と色
- `HKEY_CURRENT_USER\...\CurrentVersion\Run` … 自動起動（ユーザーが有効にした場合のみ）
- `%LOCALAPPDATA%\TaskbarMeter\skin.png` … 画像から作ったアイコン素材

管理者権限は要求しない。ネットワーク通信は一切しない。**この 2 つは維持すること**（配布相手に説明済み）。

**自動更新は入れられない。** 更新確認は通信なので「通信しない」約束と両立しない。
配布は `releases/latest/download/TaskbarMeter.zip`（固定名 = URL 不変）で、
相手が自分で取りに来る形にしてある。ZIP 名に version を入れると URL が変わるので入れないこと。

**リリースページには変更点を書かない。** 日付とダウンロード導線だけの短いページにする方針
（作者の指示）。落としに来た人の前でリンクが埋もれるのを避けるため。
変更の履歴は `git log` が担う。動線は README →（直リンクで ZIP）→ はじめかた.md。

## 実装済みの機能

- **初回セットアップ画面**（表示する項目 / 見た目 / タスクバーに出す / 自動起動）。設定画面も兼ねる
- 表示モード（常に表示 / 高負荷のときだけ表示 + しきい値）
- 指標の選択、指標ごとの色変更（ColorDialog）
- 見た目の切り替え（数字 / 内蔵ドット絵キャラ / 画像から作ったドット絵）
- 内蔵キャラは負荷で 5 段階に表情が変化（うとうと・ふつう・ごきげん・あせり・限界）＋まばたき
- **アイコン同士の掛け合い**（自分は暇 + 他が 80% 超 → 「待ちぼうけ」の顔）。
  GPU が待ちぼうけ + CPU が限界 = データ供給待ち、と読める。見た目と診断を兼ねている
- 画像→アイコン変換（解像度 16〜64、背景の自動透明化、ポスタライズ、なめらか表示）
- 計測セッション（開始/停止 → 折れ線グラフ + 平均/最大/最小 + CSV 保存）
- 見守り通知（GPU が高負荷後 3 分間ほぼ 0% で「終わったかも」）
- **VRAM 逼迫アラート**（90% が 5 秒続いたら警告、80% を切ると再武装）
- Ctrl+Alt+M で表示トグル、自動起動

## 今後の候補（未着手）

- スキン切り替え（内蔵キャラを猫・スライムなどに）。`BaseSprite` と `BuildFace` を差し替え可能にする形が素直
- プロセス指定モニター（`pid_xxx` インスタンスで python.exe の GPU/VRAM だけ見る）
- グラフのマウスオーバーで数値表示、区間平均

### 見送ったもの

- **高負荷時の微アニメーション（震える）** … タイマーが 1 秒間隔なので、
  1 秒ごとに位置が変わると「震え」ではなく「ちらつき」に見える。
  100% が何時間も続く用途なので目障りになる。やるなら描画だけ別の速いタイマーが要る
- **macOS 対応** … WinForms・Win32 API・パフォーマンスカウンタ・レジストリが土台なので
  移植ではなく作り直しになる。かつ Mac に NVIDIA GPU が載らず、
  「CUDA の学習負荷を見る」という核心の価値が移らない。作者が Mac で学習しないため見送り

## 作業時の注意

- コメントは日本語。「なぜそうしたか」を書く（何をしているかはコードを読めば分かる）
- 作者は C# 初心者寄り。大きな変更をするときは、何をどう変えたかを説明すること
- 機能を盛りすぎない方針。見た目と、研究用途に効く機能を優先する

### 見た目を変えたときの確認方法

**アイコンはコードを読んでも良し悪しが分からない。必ず描いて目で見ること。**
`IconFactory` は internal なので、`Program.cs` をリンクして `StartupObject` を差し替えた
使い捨てプロジェクトを作れば、本体を汚さずに呼び出せる。

```xml
<Compile Include="C:\Users\Shion\TaskbarMeter\Program.cs" Link="Program.cs" />
<StartupObject>IconSheet.Entry</StartupObject>
```

`IconFactory.Create(...)` の結果を `Icon.ToBitmap()` して最近傍で 8 倍に拡大し、
負荷（5 / 35 / 65 / 90 / 100%）× モード × 色 の一覧を PNG に吐くと判断しやすい。
この方法で「100% で全色が同じ赤に潰れる」「`99+` が読めない字になる」を見つけた。

**フォームの確認に `DrawToBitmap()` を使わないこと。** 入れ子パネル（`SetupForm._body` と
ボタン帯）の位置を取り違えて描き、実際には収まっているボタンが切れて見える。
実際に `Show()` してから `Graphics.CopyFromScreen` で画面のピクセルを撮ること。

```csharp
form.Location = new Point(40, 20);
form.Show(); form.Activate();
for (int i = 0; i < 40; i++) { Application.DoEvents(); Thread.Sleep(25); }
Point origin = form.PointToScreen(Point.Empty);
using var g = Graphics.FromImage(bmp);
g.CopyFromScreen(origin, Point.Empty, form.ClientSize);
```
