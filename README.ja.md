# XcpCam - FFXIV GPose ビジュアルカメラワーク編集プラグイン

<p>
  <a href="https://github.com/MWangLF/XcpCam/releases"><img alt="Release" src="https://img.shields.io/github/v/release/MWangLF/XcpCam?include_prereleases&style=flat-square&label=Release&color=blue"></a>
  <a href="https://github.com/MWangLF/XcpCam/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/MWangLF/XcpCam/total?style=flat-square&label=Downloads&color=blue"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/MWangLF/XcpCam?style=flat-square&label=License&color=blue"></a>
</p>

<p>
  <a href="README.md"><img alt="中文" src="https://img.shields.io/badge/%E4%B8%AD%E6%96%87-lightgrey?style=flat-square"></a>
  <a href="README.en.md"><img alt="English" src="https://img.shields.io/badge/English-lightgrey?style=flat-square"></a>
  <a href="README.ja.md"><img alt="日本語" src="https://img.shields.io/badge/%E6%97%A5%E6%9C%AC%E8%AA%9E-blue?style=flat-square"></a>
  <a href="README.ko.md"><img alt="한국어" src="https://img.shields.io/badge/%ED%95%9C%EA%B5%AD%EC%96%B4-lightgrey?style=flat-square"></a>
</p>

> **現在のバージョン：0.0.6.4（Pre-release）**。継続的に開発中のため、不具合報告やご要望は [GitHub Issues](https://github.com/MWangLF/XcpCam/issues) までお寄せください。

**XcpCam** は、FFXIV のグループポーズ（GPose）内でカメラワークを直感的に作成・編集できる Dalamud プラグインです。ゲーム画面上にカメラノード、Blender 風の 3D カーブ、ボーン追従コンストレイントを配置し、フレーム単位のマルチトラックタイムラインとグラフエディタで自在に調整できます。XAT / Brio 互換の `.xcp` 書き出しはもちろん、**Brio** の Vivacity タイムラインやアニメーションとのリアルタイム連携にも対応しています。

## 主な特徴・機能

- **ゲーム画面上でそのまま編集**
  - カメラ軌道（速度に応じた青〜赤の色分け表示）、カメラ視錐台、カット位置、ターゲット照準線をリアルタイム表示。
  - カメラノード・カーブ制御点・ベジェハンドルを直接クリック選択。ギズモが選択対象に自動追従し、移動・回転・拡縮、ローカル座標、スナップ、複数選択グループの一括移動・回転に対応。
  - プレビューノードの右クリックメニュー、GPose 開始時の自動起動（設定で切替可）、ショートカットの自由なカスタマイズに対応。
- **マルチトラックタイムラインとカット編集**
  - フレーム単位の複数トラック管理に対応。優先度レイヤー、ミュート／ソロ、ドラッグによるタイミング調整、ホールドフレーム、範囲選択、複数ステップのアンドゥ／リドゥが可能。
  - **同一トラック内カット**と**複数トラック切り替えカット**の両方に対応し、ライブプレビューや Brio へのベイクでは完全な瞬間カットを実現。
- **Blender 風の 3D カーブ＆コンストレイント**
  - **カーブ編集**：ベジェカーブ、ベジェ円、NURBS パスを作成し、ゲーム内で直接押し出し・細分化・開閉・反転・ハンドル調整が可能。
  - **パス追従（Follow Path）**：3D カーブに沿ってカメラを移動。タイムライン上での制御点表示、小数フレームを保持したタイムライン固定、速度カーブとの双方向同期に対応。
  - **ボーントラッキング**：減衰トラック・ロックトラック・トラックの 3 種類を搭載。任意キャラクターのボーンを追従し、画面オフセットや画角維持（Keep Framing）、書き出し用のターゲット軌道録画に対応。
- **2 モードのグラフエディタ**
  - **詳細カーブ**：パス進行度、位置オフセット、連続回転（複数回転対応）、FoV のベジェハンドルを直接編集。
  - **シンプルカーブ**：空間経路を保ったまま緩急のリズムを素早く調整。位置・回転・FoV の連動／個別編集に対応。
- **Brio との深い連携**
  - Brio の **Vivacity タイムライン** または **キャラクターのアニメーション再生バー** にバインドでき、FPS 自動換算・総フレーム数の双方向同期・再生ヘッド連動に対応。
  - **リアルタイムカメラプレビュー**、**XAT 連動再生**、Brio フリーカメラトラックへの**ワンクリックベイク**（アンドゥ対応）を搭載。
- **`.xcp` 読み込み＆再編集可能な書き出し**
  - **プロジェクト埋め込み書き出し**：通常の再生互換性を保ったまま XcpCam の編集データを `.xcp` に同梱（標準有効）。再読み込みすればトラック・カーブ・ボーン追従コンストレイントをそのまま復元可能。
  - **通常 `.xcp` のスマート復元**：外部 `.xcp` ファイルを編集しやすいカメラノードとカーブへ自動再構築。誤差プレビューとカット候補の確認ができ、全フレームにノードが乱立しません。

## インストール

**Dalamud API 15 / .NET 10** が必要です。ライブプレビュー・連動再生・ベイク機能には [Brio](https://github.com/Etheirys/Brio) が必要です。

### 📦 カスタムリポジトリから導入（推奨）

1. ゲーム内で `/xlsettings` を開き、**実験的機能（Experimental）** タブへ移動します。
2. **カスタムプラグインリポジトリ** に以下の URL を追加し、有効化して保存します：
   ```text
   https://raw.githubusercontent.com/MWangLF/XcpCam/main/repo.json
   ```
3. `/xlplugins` から **XcpCam** を検索してインストールします。
4. GPose に入ると自動でウィンドウが開きます。`/xcpcam` で手動開閉も可能です。

### 🛠️ 手動インストール（開発プラグイン）

1. [Releases](https://github.com/MWangLF/XcpCam/releases) から最新版をダウンロードして展開します。
2. `/xlsettings` → **Experimental** → `Dev Plugin Locations` に `XcpCam.dll` のフルパスを追加します。
3. `/xlplugins` → **Dev Tools** から XcpCam を有効化します。

## サポート

バグ報告や機能リクエストは [GitHub Issues](https://github.com/MWangLF/XcpCam/issues) へお寄せください。

## ソースからのビルド

.NET 10 SDK と Dalamud（API 15）が必要です：

```bash
dotnet build -c Release
```

## 謝辞

- [Brio](https://github.com/Etheirys/Brio)（GPL-3.0）：XAT カメラ再生・Vivacity タイムライン
- [XAT](https://github.com/Etheirys/XAT)：`.xcp` カメラファイル形式
- [Blender](https://github.com/blender/blender)（GPL-2.0-or-later）：カーブオブジェクト・ベジェハンドル・NURBS・パス追従／トラッキングの設計
- [psenxiv/vista](https://github.com/psenxiv/vista)：弧長テーブルと単調 PCHIP タイミングカーブの着想
- [ReplayMod / ReplayStudio](https://github.com/ReplayMod/ReplayStudio)：キーフレーム時間ベースのスプラインカメラ編集の着想
- Shoemake (1985/1987)：SQUAD クォータニオン補間

## ライセンス

[GPL-3.0](LICENSE) ライセンスのもとで公開されています。
