# TaskbarMeter

Windows のタスクバーに、CPU / GPU / VRAM などの使用率をアイコンで表示するアプリを作りました！

例えば、深層学習の学習中に「今どれくらい回っているか」をスマートに横目で見られるように作っています！

## 🚀 ダウンロードはこちら👇

###  **[TaskbarMeter.zip をダウンロード](../../releases/latest/download/TaskbarMeter.zip)**

### 落とせたら → **[はじめかた.md を開く](はじめかた.md)**

<sub>過去の版は [Releases](../../releases) にあります。</sub>

ここまででreadmeの役割は終わり！！
ダウンロードが終わったら「はじめかた.md」に行ってください！


---

| ドキュメント | 対象 |
|---|---|
| [はじめかた.md](はじめかた.md) | **配布相手向け。まずこれ**。導入して使いはじめるまで |
| [説明書.md](説明書.md) | 全機能の説明とトラブル対処。困ったときに引く |
| [仕様書.md](仕様書.md) | 中身の仕組み。コード付きの技術説明 |

## 表示できるもの

1 つの指標 = 1 つのトレイアイコン。必要なものだけを選んで出せます。

| 指標 | 取得元 | 備考 |
|---|---|---|
| CPU 使用率 | `GetSystemTimes` | OS 言語やカウンタ破損の影響を受けない |
| メモリ使用率 | `GlobalMemoryStatusEx` | |
| GPU 使用率 (3D) | `GPU Engine \ Utilization Percentage` の `engtype_3D` | ゲーム・描画の負荷 |
| GPU 使用率 (学習) | 同上の `engtype_Compute` / `engtype_Cuda` | **深層学習の負荷はこちらに出る** |
| VRAM 使用量 | `GPU Adapter Memory \ Dedicated Usage` | 総量はレジストリから取得 |
| ディスク使用率 | `PhysicalDisk \ % Disk Time` | |
| ネット受信 / 送信 | `Network Interface \ Bytes Received,Sent/sec` | 上限は実測の最大値に追従 |

## 見た目

3 種類から選べます。どれも 16x16 の論理グリッドを組んでから、
トレイの実表示サイズ（DPI 依存で 16 / 24 / 32px）ちょうどに描くので、縮小によるボケがありません。

- **ドット絵キャラ** — 負荷で 5 段階に表情が変わる（うとうと / ふつう / ごきげん / あせり / 限界）＋まばたき
- **数字** — 内蔵の 5x9 ドット字形。白抜き＋縁取りなので明色・暗色どちらのタスクバーでも読める
- **好きな画像** — 画像をドット絵に変換して使う（背景の自動透明化、ポスタライズ、解像度 16〜64）

指標ごとに色を変えられます。85% を超えると下部のゲージが赤くなります
（本体の色は指標ごとの色のまま残すので、どれが限界なのかが分かります）。

## そのほかの機能

- **初回セットアップ画面** — 表示する項目・見た目・タスクバーへの表示・自動起動を 1 画面で
- **計測セッション** — 開始/停止で記録し、折れ線グラフ＋平均/最大/最小を表示。CSV 保存も可
- **見守り通知** — GPU が高負荷のあと 3 分ほぼ 0% なら「終わったかも」と知らせる
- **VRAM 逼迫アラート** — VRAM が 90% を超え続けたら警告。OOM で落ちる前に気づける
- **表示モード** — 常に表示 / 高負荷のときだけ表示（しきい値を選択可）
- **Ctrl+Alt+M** — 表示のトグル
- **自動起動** — HKCU の Run キーに登録（管理者権限は不要）

## 「誰でも入れられる」ための設計

| 項目 | 内容 |
|---|---|
| インストール | 不要。exe 1個をダブルクリックするだけ |
| 管理者権限 | 不要 |
| .NET ランタイム | 不要（self-contained で同梱） |
| 外部ライブラリ | `System.Diagnostics.PerformanceCounter` のみ |
| ネットワーク通信 | 一切しない |
| GPU ベンダー | NVIDIA / AMD / Intel 問わず動作 |
| OS 言語 | 日本語 Windows でも動く（カウンタ名を自動変換） |
| Windows | 10 / 11 どちらも可 |

## ビルド

.NET 8 SDK が必要（https://dotnet.microsoft.com/download）。

```
build.cmd
```

これ 1 つで「常駐プロセスの終了 → `dotnet publish -c Release` → 新しい exe を起動」まで行います。
**常駐中の exe はロックされるため、必ず先にプロセスを落とす必要があります**（build.cmd がやっています）。

出力：`bin\Release\net8.0-windows\win-x64\publish\TaskbarMeter.exe`
この exe 1個を配れば、相手は置いてダブルクリックするだけです。

## 配布のしかた

exe は約 70MB あります。**Git リポジトリに直接置かず、GitHub の Releases に添付してください。**
リポジトリに入れると、更新のたびに 70MB がリポジトリ履歴に積み上がって取り返しがつかなくなります。

### 必ず ZIP で配ること

**exe を直接置くと、ブラウザがダウンロードを止めます。**
「一般的にダウンロードされていません」と出て `.crdownload` のまま保存されず、
受け取った側は「ファイルが壊れている」と受け取ります。
これは実際に踏んだ問題で、署名証明書のない exe では避けられません。

ZIP に包むとこの警告は出ません（起動時の SmartScreen 警告は別途出ます）。

### 手順

```
release.cmd
```

これで 2 つ出来ます。

- `dist\TaskbarMeter.zip` … exe と `dist-template\お読みください.txt`
- `dist\release-notes.md` … `release-notes.template.md` に日付を入れたもの

あとは版番号を上げて添付するだけです。

```bash
gh release create v1.2 dist\TaskbarMeter.zip --title "TaskbarMeter v1.2" --notes-file dist\release-notes.md
```

### リリースページに変更点は書かない

**日付とダウンロード導線だけの短いページにしてあります。**
リリースページは「落として使いたい人」が来る場所で、変更点を読みに来る場所ではありません。
長いと肝心のダウンロードリンクが埋もれます。

変更の履歴は `git log` に残ります。コミットメッセージに何をなぜ変えたかを書いておけば、
それが記録として十分機能します。

### ZIP の名前に version を入れないこと

`TaskbarMeter.zip` という固定名にしてあるのは、この URL を不変にするためです。

```
https://github.com/ShionAkaiwa/TaskbarMeter/releases/latest/download/TaskbarMeter.zip
```

`releases/latest/download/<ファイル名>` は、常に最新リリースの同名アセットを返します。
version を名前に入れると、リリースのたびにこの URL が変わり、
配った相手のブックマークが切れます。version はリリースのタグと
`お読みください.txt` の中で示せば足ります。

渡すときに一緒に伝えること：

- **ZIP のほうを落として、展開してから使う**
- **「Windows によって PC が保護されました」が出るが、詳細情報 → 実行で進めてよい**
  コード署名証明書（年 1〜2 万円）が無いため必ず出ます。避けられません
- **タスクバーに何も出なくても動いている**。最初の画面の「タスクバーに出す」を押してもらう

### SmartScreen の「安全であることを報告する」は効果がない

ブラウザの警告から Microsoft へ誤検知を報告できますが、報告フォームに載る URL は
GitHub が発行する**数時間で失効する一時 URL** です。恒久的な評価には結び付きません。
SmartScreen の評価はダウンロード実績と署名証明書で積み上がるもので、
報告で付与されるものではないため、時間をかける価値はありません。

### 相手が困ったときの導線

| 症状 | 案内先 |
|---|---|
| アイコンが出てこない | [はじめかた.md](はじめかた.md) の「4. アイコンが見当たらないとき」 |
| 終了したら戻せない | スタートメニューで「TaskbarMeter」を検索（初回起動時に登録される） |
| GPU が 0% のまま | [説明書.md](説明書.md) の「深層学習をする人へ」 |

## 実装メモ

- **カウンタ名は OS 言語でローカライズされる**。日本語 Windows で `new PerformanceCounter("GPU Engine", ...)` は通らない。
  `PerfNames.Localize()` がレジストリの `Perflib\009`（英語）と `Perflib\CurrentLanguage` を突き合わせて変換している
- **Windows 11 は新しいトレイアイコンをオーバーフローに入れる**。`TrayPromotion` が
  `HKCU\Control Panel\NotifyIconSettings` に `IsPromoted=1` を書いて表に出す（管理者権限は不要）
- **`Bitmap.GetHicon()` は `DestroyIcon` で解放が必要**。毎秒生成しているので、漏らすと GDI ハンドルを食い潰す
- 温度まで出したい場合は LibreHardwareMonitorLib が必要（管理者権限が要るようになるため入れていない）
