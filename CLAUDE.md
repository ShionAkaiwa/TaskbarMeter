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

## ファイル構成

| ファイル | 内容 |
|---|---|
| `Program.cs` | 全実装。約 2100 行 |
| `TaskbarMeter.csproj` | 配布設定（self-contained, PublishSingleFile） |
| `build.cmd` | ビルド＆再起動用。日本語を含めない（文字コードで壊れた実績あり） |
| `はじめかた.md` | 配布相手（初心者）向けのセットアップ手順 |

## アーキテクチャ

```
Program.Main
  └ MeterContext : ApplicationContext     … ウィンドウを持たない本体
       ├ List<Metric>                     … 指標。1 指標 = 1 トレイアイコン
       ├ Dictionary<string, Slot>         … 有効な指標ぶんの NotifyIcon
       ├ IconFactory                      … アイコン画像の生成
       ├ SessionRecorder / SessionResultForm … 計測とグラフ表示
       ├ PixelSkin / SkinPreviewForm      … 画像→アイコン変換
       ├ HotkeyWindow                     … Ctrl+Alt+M
       └ Settings                         … HKCU への保存
```

### Metric（指標）

`Metric` 抽象クラスを継承し、`MetricCatalog.CreateAll()` に足せば、メニュー・色設定・計測記録に自動で載る。
**指標を追加するときはここだけ触ればよい**設計にしてある。

実装済み: CPU / メモリ / GPU(3D) / GPU(Compute) / VRAM / ディスク / ネット受信 / ネット送信

`Read()` は `Sample(Ratio, Text, Tooltip, Value, Unit)` を返す。
`Ratio` は 0〜1 でゲージ用、`Value` と `Unit` は記録・CSV 用の実値（% / GB / Mbps）。

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
   復帰手段を 3 系統用意してある。壊さないこと。
   - `Ctrl+Alt+M`（`HotkeyWindow`）
   - exe を再度起動すると、名前付き Event 経由で常駐側に「出てこい」と伝わる
   - 「表示する項目」は最後の 1 つを外せないようにガードしてある

5. **GPU の深層学習負荷は 3D エンジンではなく Compute エンジンに出る**
   `engtype_3D` だけを見ていると学習中でも 0% に見える。`engtype_Compute` / `engtype_Cuda` を拾っている。

## 設定の保存先

- `HKEY_CURRENT_USER\Software\TaskbarMeter` … 表示モード、しきい値、見た目、指標ごとの ON/OFF と色
- `HKEY_CURRENT_USER\...\CurrentVersion\Run` … 自動起動（ユーザーが有効にした場合のみ）
- `%LOCALAPPDATA%\TaskbarMeter\skin.png` … 画像から作ったアイコン素材

管理者権限は要求しない。ネットワーク通信は一切しない。**この 2 つは維持すること**（配布相手に説明済み）。

## 実装済みの機能

- 表示モード（常に表示 / 高負荷のときだけ表示 + しきい値）
- 指標の選択、指標ごとの色変更（ColorDialog）
- 見た目の切り替え（数字のみ / 内蔵ドット絵キャラ / 画像から作ったドット絵）
- 内蔵キャラは負荷で 5 段階に表情が変化（うとうと・ふつう・ごきげん・あせり・限界）＋まばたき
- 画像→アイコン変換（解像度 16〜64、背景の自動透明化、ポスタライズ、なめらか表示）
- 計測セッション（開始/停止 → 折れ線グラフ + 平均/最大/最小 + CSV 保存）
- 見守り通知（GPU が高負荷後 3 分間ほぼ 0% で「終わったかも」）
- Ctrl+Alt+M で表示トグル、自動起動

## 今後の候補（未着手）

- アイコン同士の掛け合い（隣のアイコンの負荷を見て表情が変わる）。差別化の目玉として案が出ている
- スキン切り替え（内蔵キャラを猫・スライムなどに）
- プロセス指定モニター（`pid_xxx` インスタンスで python.exe の GPU/VRAM だけ見る）
- VRAM 逼迫アラート
- 高負荷時の微アニメーション（震える・跳ねる）
- グラフのマウスオーバーで数値表示、区間平均

## 作業時の注意

- コメントは日本語。「なぜそうしたか」を書く（何をしているかはコードを読めば分かる）
- 作者は C# 初心者寄り。大きな変更をするときは、何をどう変えたかを説明すること
- 変更後は `build.cmd` でビルドが通ることを確認してもらう（この環境ではビルドできない場合がある）
- 機能を盛りすぎない方針。見た目と、研究用途に効く機能を優先する
