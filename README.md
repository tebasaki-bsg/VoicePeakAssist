# VoicePeak 発音調整アシスト

VOICEPEAK の発音調整パネルで、イントネーションと長さをホイールやキーで調整する常駐ツール。

> **注意：これは非公式ツールです。**
> VOICEPEAK の開発元・販売元とは関係のない個人が作ったもので、公式のサポート対象ではありません。
> このツールについて VOICEPEAK の開発元・販売元へ問い合わせないでください。
> 「VOICEPEAK」は各権利者の商標または登録商標です。

## フォルダ構成

| 場所 | 中身 |
| --- | --- |
| `src/` | ソースコード（C#、.NET Framework 4.x、Windows Forms） |
| `VoicePeakAssist/` | 配布用フォルダ。`README.txt`（使い方）と、ビルドした `VoicePeakAssist.exe` |
| `build.bat` | ビルド用スクリプト |

## ビルド

`build.bat` をダブルクリックすると、`VoicePeakAssist\VoicePeakAssist.exe` ができる。
Windows に最初から入っている .NET Framework 4.x の C# コンパイラを使うので、追加のインストールは不要。

## Git で管理しないもの

ビルドした exe と、実行時に exe の横に作られる設定・ログ・学習データ（`VoicePeakAssist.ini`、`VoicePeakAssist.log`、`glyphs.dat`、`diag/`）は `.gitignore` で除外している。

## ライセンス

[MIT License](LICENSE)
