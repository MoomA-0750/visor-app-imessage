# visor-app-imessage

[visor](https://github.com/MoomA-0750/visor)（VR の中で Claude と話すための SteamVR のオーバーレイ）のアプリ「iMessage」。
Mac で動く [imsg-web](https://github.com/MoomA-0750/imsg-web) の、ほかのプログラム向けの API（imsg-web の `docs/api.md`）につないで、iMessage の会話を読み、返事を打って送る。

- 左の板: 会話の一覧（新しい順。連絡先の顔（無ければ頭文字の丸、電話番号とメールなら人の形）・名前・最後の文・時刻・未読の数）。押すと開く
- 窓の中: 選んだ会話（相手は左・自分は右の吹き出し、日付の区切り、写真、リンクの札、返信の引用、リアクション。グループなら送った人の名前と顔）。いちばん下から始まる
- 下の板: ＋（ファイルを添える）・入力欄・送るボタン。＋で、最近の写真（ピクチャ・デスクトップ・ダウンロード。iCloud 写真も）とファイル（ダウンロード・デスクトップ・ドキュメント）を選ぶ小窓が開く（12 個ずつ。写真の縮小版は Windows のエクスプローラーと同じものを使い、クラウドにしか無い写真を落とさない）。visor のファイルの窓・ギャラリー・写真の窓から窓へ運んでも添えられる。10 個まで・1 つ 100MB まで。長い文は折り返して帯ごと上へ伸びる。入力欄を押すと visor のキーボードの窓が出る（VR のキーボード・フリック・声・PC のキーボード）。Enter か送るボタンで送る。送るとすぐ会話に「送信中」の吹き出しを出し、会話に出てきたら消す（出るまで詰めて読み直す）。送っていないと分かったときだけ、文と添えたものを欄に戻す。imsg-web が「届いたか分からない」と答えたときは戻さず（送り直して二重にならないよう）、吹き出しで「確かめています」と出し、1 分出てこなければ Messages で確かめるよう出す
- 上の板: 会話の名前と、読み直す・つなぐ先の設定
- 新着: 窓を出している間は 15 秒、出していない間は 1 分おきに見て、相手からの新しいメッセージを visor の通知で知らせる
- Claude に道具 `list_chats`・`read_chat`・`send_message`・`send_file`（本人がはっきり頼んだときだけ。送る前に相手と中身を本人に確かめる）・`open_chat` を渡す

## つなぐ

1. Mac で imsg-web のトークンを出す: `~/Library/Application\ Support/imsg-web/imsg-web client add visor --send`（読むだけなら `--send` を付けない）
2. visor の iMessage の窓の歯車で、URL（Tailscale Serve の名前なら `https://<Mac>.<tailnet>.ts.net`）とトークンを入れて「つなぐ」。LAN の名前・IP でつなぐときは、Mac の `state/tls/cert.pem` の中身も入れる（証明書を確かめるのは切らない）

設定は `%LOCALAPPDATA%\visor\appdata\imessage\config.json`。トークンは Windows の DPAPI で守った形で書く（手で `"token"` を書いておくと、次に読んだときに守った形に書き直す）。`cert.pem` を同じ場所に置いてもよい。

## 作り方

- .NET 9 の C#。visor との受け渡しは `VisorApp.cs`（visor の `sdk/dotnet/VisorApp.cs` の写し）、imsg-web とは `ImsgClient.cs`
- visor の `input`（文字の入力欄）・`scroll` の `bottom`・app.json の `drop` が要る（2026-11-15 の visor）
- 写真と顔は小さな JPEG にしてから送る（画面を送り直すたびに絵も送るため）。webp など読めない形は「写真」の札
- imsg-web の id は一時のもの（サーバーの epoch の中だけ）なので残さない。epoch が変われば、選んでいた会話を名前で選び直す
- `dotnet publish -c Release -o bin` で組み立てる
- 試し: `python tools/mock_imsg.py` で作りものの imsg-web（`http://127.0.0.1:8799`、トークン `test`）を起こし、visor の `--ext-test` で見る（http はこの PC の中だけ許す）
