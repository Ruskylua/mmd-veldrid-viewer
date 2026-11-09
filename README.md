# Simple PMX Viewer

Windows向けのローカルMMDビューアです。PMXモデル、VMDモーション／カメラ、音声を読み込み、GPUスキニングを使って再生します。

> 本プロジェクトはMMD形式の互換ビューアを目標にした開発中のソフトウェアです。MikuMikuDance本体とは別の、独立した実装です。

## 主な機能

- `.pmx` モデルの読み込み
- `.vmd` ボーンモーションとカメラワークの読み込み（別ファイルを順番に読み込み可能）
- `.fbx` モーションの読み込みとPMXボーンへの簡易リターゲット
- `.wav` / `.mp3` / `.mp4` 音源の再生
- PMX IK、付与親、TransformLayerの簡易反映
- GPU Linear Blend Skinning（BDEF1 / BDEF2 / BDEF4 / SDEF / QDEF）
- CPUスキニングへのフォールバック
- テクスチャ、材質色、法線、材質ID、ウェイト種別のデバッグ表示
- 透過、カリング、簡易陰影、輪郭線、グリッド、背景色の切り替え
- VMDボーンバインド／FBXリターゲットの診断出力

## 必要環境

- Windows 10 または Windows 11
- .NET 10 SDK（ソースからビルドする場合）
- Direct3D 11 / Vulkan / OpenGL のいずれかに対応したGPU

## 起動

リポジトリを取得後、次のどちらかで起動できます。

```powershell
dotnet run --project .\SimplePmxVeldrid.csproj -c Release
```

または、`Simple PMX Viewer.cmd` を実行してください。

`build.cmd` は `publish` フォルダへReleaseビルドを出力します。

## ファイルの開き方

- **PMX**: `Open PMX File` ボタン、または画面へドラッグ＆ドロップ
- **VMD**: `Open VMD Motion` ボタン、または画面へドラッグ＆ドロップ
- **音源**: `Open Audio File` ボタン、または画面へドラッグ＆ドロップ

モーションVMDとカメラVMDが別の場合は、両方を順番に読み込んでください。PMXファイルを `Simple PMX Viewer.cmd` へドロップして起動することもできます。

## レンダーウィンドウ操作

| 操作 | 内容 |
| --- | --- |
| 左ドラッグ | 回転 |
| 右ドラッグ | 平行移動 |
| ホイール | ズーム |
| `R` | 表示リセット |
| `Space` | 再生／一時停止 |
| `,` / `.` | 再生速度を下げる／上げる |
| `Left` / `Right` | 1フレーム戻る／進む |
| `Down` / `Up` | 10フレーム戻る／進む |
| `Home` / `End` | 先頭／最後へ移動 |
| `L` | ループON/OFF |
| `C` / `V` | VMDカメラ／ボーンモーションON/OFF |
| `I` | IK Full / Off |
| `K` | IKプロファイル Stable / Normal / Strong |
| `G` | 付与親（Grant）ON/OFF |
| `Y` | GPU-LBSスキニングON/OFF |
| `H` | GPU-LBSデバッグON/OFF |
| `U` | ウェイト種別の色分け表示 |
| `1`–`4` | テクスチャ／材質色／法線／白表示 |
| `M` | 材質ID色デバッグON/OFF |
| `T` | 透過ソート BackToFront / MaterialOrder |
| `5`–`9` | 透過／陰影／カリング／輪郭線／配置基準 |
| `0` | グリッドON/OFF |
| `B` | 背景色切り替え |

再生速度は `0.25x / 0.5x / 0.75x / 1.0x / 1.25x / 1.5x / 2.0x` です。

GPU-LBS使用中はウィンドウタイトルに `FPS`、CPU頂点生成の状態、GPUフォールバック率を表示します。

## 技術スタック

- C# / .NET 10
- Avalonia UI
- Veldrid（Direct3D 11 / Vulkan / OpenGL）
- SkiaSharp
- AssimpNet（FBX読み込み）

## 診断ファイル

PMX読み込み後にVMDまたはFBXを読み込むと、`publish/diagnostics/` に診断ファイルを出力します。下半身が動かない、足IKが不安定、FBXのボーン対応が不自然といった場合の調査に使えます。

`publish/` はビルド生成物であり、Git管理の対象外です。

## 既知の制限

- VMD表情モーフは未対応です。
- 剛体・ジョイントを含むMMD物理演算は未対応です。
- SDEF/QDEFは現在GPU-LBSの簡易処理です。本家MMDと完全に同じ変形にはなりません。
- シェーダ、材質、セルフシャドウなどのMMD固有描画は簡易実装です。
- PMX・VMD・FBXのすべてのファイルに対する完全互換性は保証していません。

## ライセンス

ライセンスは公開時に追加してください。一般公開する場合は、MIT Licenseが扱いやすい選択肢です。
