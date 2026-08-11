# TaskbarMeter

タスクバー（通知領域）に **CPU 使用率**と **GPU 使用率**を数字で常時表示する常駐アプリ。

- 🔵 青いアイコン = CPU
- 🟢 緑のアイコン = GPU
- アイコンは「下からの塗り」でも使用率を表現。マウスを乗せると `CPU 42%` のように出る。
- 右クリック → 自動起動の ON/OFF、タスク マネージャー起動、終了。

## 特徴（「誰でも入れられる」ための設計）

| 項目 | 内容 |
|---|---|
| インストール | 不要。exe 1個をダブルクリックするだけ |
| 管理者権限 | 不要 |
| .NET ランタイム | 不要（self-contained で同梱） |
| 外部ライブラリ | なし（OS 標準の API のみ） |
| GPU ベンダー | NVIDIA / AMD / Intel 問わず動作 |
| OS 言語 | 日本語 Windows でも動く（カウンタ名を自動変換） |
| Windows | 10 / 11 どちらも可 |

## ビルド

.NET 8 SDK が必要（https://dotnet.microsoft.com/download）。

```powershell
cd TaskbarMeter
dotnet publish -c Release
```

出力：`bin\Release\net8.0-windows\win-x64\publish\TaskbarMeter.exe`
この exe 1個を配れば、相手は置いてダブルクリックするだけ。

開発中の実行は `dotnet run` でも可。

## 仕組み（要点）

- **CPU**: `GetSystemTimes()` の差分。OS 言語やカウンタ破損の影響を受けない。
- **GPU**: パフォーマンスカウンタ `GPU Engine \ Utilization Percentage` のうち
  `engtype_3D` のインスタンスを合算（タスク マネージャーと同じ考え方）。
- カウンタ名は OS 言語でローカライズされるため、レジストリの
  `Perflib\009`（英語）と `Perflib\CurrentLanguage` を突き合わせて変換している。

## 既知の制限 / 今後の拡張候補

- GPU はプロセス単位のインスタンスを 5 秒ごとに取り直しているため、
  瞬間的な負荷はタスク マネージャーより少し遅れて出ることがある。
- 100% は桁が入らないため `99` と表示。
- 拡張しやすい追加候補：メモリ使用率・VRAM・温度・ネットワーク速度、
  折れ線グラフ表示、タスクバー上の横長オーバーレイ表示、設定ファイル対応。
- 温度や VRAM まで出したい場合は LibreHardwareMonitorLib の追加が必要
  （その場合は管理者権限が要る点に注意）。
