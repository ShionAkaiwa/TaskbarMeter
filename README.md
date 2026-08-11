# TaskbarMeter

Windows のタスクバー（通知領域）に、CPU / GPU / VRAM などの使用率をアイコンで常時表示する常駐アプリ。
深層学習の学習中に「今どれくらい回っているか」を横目で見られるように作っています。

配布相手向けの手順は [はじめかた.md](はじめかた.md) にあります。

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

## 実装メモ

- **カウンタ名は OS 言語でローカライズされる**。日本語 Windows で `new PerformanceCounter("GPU Engine", ...)` は通らない。
  `PerfNames.Localize()` がレジストリの `Perflib\009`（英語）と `Perflib\CurrentLanguage` を突き合わせて変換している
- **Windows 11 は新しいトレイアイコンをオーバーフローに入れる**。`TrayPromotion` が
  `HKCU\Control Panel\NotifyIconSettings` に `IsPromoted=1` を書いて表に出す（管理者権限は不要）
- **`Bitmap.GetHicon()` は `DestroyIcon` で解放が必要**。毎秒生成しているので、漏らすと GDI ハンドルを食い潰す
- 温度まで出したい場合は LibreHardwareMonitorLib が必要（管理者権限が要るようになるため入れていない）
