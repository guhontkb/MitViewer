# MitViewer

MIT 画像（`*.mit`）の表示位置調整・保存を行う Windows Forms ツールです。  
ドラッグ操作で画像の表示位置（X は 8 ドット単位）を調整できます。

## 主な機能

- `.mit` ファイルの読み込み
- 768x512 仮想画面上での表示位置調整
- 表示位置の上書き保存 / 名前を付けて保存
- 4 色 BMP から `.mit` への変換読み込み

## 必要環境

- Windows
- .NET 10 SDK（`net10.0-windows`）

## ビルドと実行

```powershell
dotnet build
dotnet run -- "<path-to-file.mit>"
```

## GitHub 公開時の注意

- ビルド成果物（`bin/`, `obj/`）やローカル設定は `.gitignore` で除外しています。
- `*.mit` / `*.mag` などの画像データは、配布権限が明確なもののみ公開してください。
  - 必要な場合は、権利情報を明記したうえで個別にコミット（または `git add -f`）してください。

## ライセンス

このプロジェクトは **GNU General Public License v3.0 (GPL-3.0)** の下で公開しています。  
詳細は [LICENSE](./LICENSE) を参照してください。

Copyright (C) 2026 guhontkb and contributors

本ソフトウェアは **無保証** です。  
利用・再配布・改変時は GPL-3.0 の条件に従ってください。