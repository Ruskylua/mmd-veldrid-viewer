# Simple PMX Viewer

**PMXモデルとVMDモーションをローカルで再生する、Windows向けMMD互換ビューアです。**

`Simple PMX Viewer` は、MikuMikuDance形式のモデル・モーション・カメラワークを読み込み、GPUスキニングで表示する開発中のデスクトップアプリケーションです。MikuMikuDance本体を改造したものではなく、C#で新規に実装しています。

> [!WARNING]
> 開発中のプロジェクトです。本家MMDとの完全互換性や、すべてのPMX/VMD/FBXファイルでの動作は保証していません。

## できること

| 分類 | 内容 |
| --- | --- |
| モデル | `.pmx` の読み込み、材質・テクスチャ・透過の表示 |
| モーション | `.vmd` のボーンモーション再生、PMX IK・付与親・TransformLayerの簡易反映 |
| カメラ | `.vmd` カメラワークの読み込みと再生。モーションとは別ファイルでも使用可能 |
| FBX | `.fbx` の読み込みと、PMXボーンへの簡易リターゲット |
| 音声 | `.wav` / `.mp3` / `.mp4` の再生 |
| 描画 | GPU-LBS、CPUフォールバック、陰影、輪郭線、グリッド、背景、カリング |
| デバッグ | 法線・材質ID・ウェイト種別表示、FPS・CPUフォールバック率、診断ログ |

## 動作環境

- Windows 10 / 11
- .NET 10 SDK（ソースから実行・ビルドする場合）
- Direct3D 11 / Vulkan / OpenGL のいずれかを使えるGPU

## 最短で起動する

```powershell
git clone https://github.com/<your-account>/mmd-veldrid-viewer.git
cd mmd-veldrid-viewer
dotnet run --project .\SimplePmxVeldrid.csproj -c Release
```

Windowsでは、`Simple PMX Viewer.cmd` をダブルクリックして起動することもできます。初回はNuGetパッケージの復元が行われます。

配布用のRelease出力を作る場合は、以下を実行します。

```powershell
.\build.cmd
```

実行ファイル一式は `publish/` に出力されます。このフォルダは生成物のため、Git管理の対象外です。

## 使い方

1. アプリを起動する
2. **Open PMX File** を押すか、PMXファイルを画面へドロップする
3. **Open VMD Motion** からモーションVMDを開く
4. カメラVMDが別にある場合は、続けて同じボタンから読み込む
5. 必要に応じて **Open Audio File** から音源を追加する
6. `Space` で再生する

PMXファイルを `Simple PMX Viewer.cmd` にドラッグ＆ドロップして、直接起動することもできます。

## レンダーウィンドウ操作

| 操作 | 内容 |
| --- | --- |
| 左ドラッグ / 右ドラッグ / ホイール | 回転 / 平行移動 / ズーム |
| `R` | 表示リセット |
| `Space` | 再生 / 一時停止 |
| `,` / `.` | 再生速度を下げる / 上げる |
| `Left` / `Right` | 1フレーム戻る / 進む |
| `Down` / `Up` | 10フレーム戻る / 進む |
| `Home` / `End` | 先頭 / 最終フレームへ移動 |
| `L` | ループ ON/OFF |
| `C` / `V` | VMDカメラ / ボーンモーション ON/OFF |
| `I` / `K` | IK ON/OFF / IKプロファイル切り替え |
| `G` | 付与親（Grant）ON/OFF |
| `Y` | GPU-LBS ON/OFF |
| `H` | GPU-LBSデバッグ ON/OFF |
| `U` | ウェイト種別の色分け |
| `1`–`4` | テクスチャ / 材質色 / 法線 / 白表示 |
| `M` | 材質ID色デバッグ ON/OFF |
| `T` | 透過ソート切り替え |
| `5`–`9` | 透過 / 陰影 / カリング / 輪郭線 / 配置基準 |
| `0` / `B` | グリッド / 背景色切り替え |

再生速度は `0.25x / 0.5x / 0.75x / 1.0x / 1.25x / 1.5x / 2.0x` です。

`Y` でGPU-LBSを有効にすると、ウィンドウタイトルに次の実行情報を表示します。

```text
FPS:60.0 | CPUVerts:skipped | Fallback:0.0%
```

- `CPUVerts:skipped` は、通常再生でCPUスキニングを省略できている状態です。
- `CPUVerts:needed` は、アウトラインや透明ソートなどのためCPU頂点が必要な状態です。
- `Fallback` はGPUスキニングを使えない頂点の割合です。

## 対応するウェイト

GPU Linear Blend Skinning（LBS）は、BDEF1 / BDEF2 / BDEF4 / SDEF / QDEFを処理します。SDEF/QDEFは現時点ではLBSとしての簡易処理で、本家MMDの変形と完全には一致しません。

## 技術スタック

- C# / .NET 10
- Avalonia UI
- Veldrid（Direct3D 11 / Vulkan / OpenGL）
- SkiaSharp
- AssimpNet（FBX読み込み）

## 診断ログ

PMXを開いた状態でVMDまたはFBXを読み込むと、実行フォルダの `diagnostics/` にバインド・リターゲット用のログを出力します。

下半身が動かない、足IKが不安定、FBXの骨対応が不自然な場合は、このログを添えてIssueを作成してください。`diagnostics/` はGit管理の対象外です。

## 既知の制限と今後の予定

- VMD表情モーフは未対応
- 剛体・ジョイントを含むMMD物理演算は未対応
- セルフシャドウ、Toon、スフィアマップなどMMD固有描画は簡易実装
- PMX・VMD・FBXの完全互換は未達成

IssueやPull Requestは歓迎します。特に、再現可能なPMX/VMDの組み合わせ、エラー画面、診断ログがあると調査しやすくなります。

## ライセンスと利用者コンテンツ

ソースコードは [MIT License](LICENSE) の下で公開されています。

PMX、VMD、FBX、テクスチャ、音源などの利用者コンテンツはこのリポジトリに含めません。これらのモデル・モーション・素材には、個別の著作権および配布条件が適用されます。公開・再配布する前に、各素材の規約を必ず確認してください。
