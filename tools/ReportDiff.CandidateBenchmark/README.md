# 初期差分SIMDの評価ツール

製品に組み込む前の候補抽出SIMDを比較する開発用ツール。製品プロジェクトからは参照しない。判断と性能証跡は [PERF-2f](../../docs/verification/perf-2f-candidate-simd.md) を参照。

## 実行

```sh
dotnet run -c Release --project tools/ReportDiff.CandidateBenchmark -- verify
DOTNET_EnableHWIntrinsic=0 dotnet run -c Release --project tools/ReportDiff.CandidateBenchmark -- verify
dotnet run -c Release --project tools/ReportDiff.CandidateBenchmark -- measure > micro.json
```

環境によってローカルNuGetキャッシュの指定が必要。比較コアの既存friend assembly名 `ReportDiff.Benchmark` を使用するため、出力DLL名はプロジェクト名とは異なる。別ツールの同名DLLと同じ出力ディレクトリへ混在させない。

`verify` はベクトル境界・0件・端数・3チャネル・float32しきい値・FMAと丸めが異なる1,000条件・担当行・入力不変・範囲外書き込みを検査する。不一致時は終了コードが非0となる。成功JSONには照合回数と、実際に選択されたSIMD幅を記録する。

`Auto` は256bit、128bit、スカラーの順でハードウェア対応を判定する。検証では固定幅を明示した経路も実行する。256bit非対応のMacで成功してもAVX実機の検証にはならない。`DOTNET_EnableHWIntrinsic=0` の実行では自動選択がScalarであることをJSONでも確認する。

`measure` は5サイズ×エッジ許容0/0.3×局所/全面変更の20条件を、スカラー逐次・SIMD逐次・スカラー行並列・SIMD行並列で比較する。出力はJSON、進捗はstderr。特徴量を一度生成し、候補マスクの確保と走査を計測する。100万画素未満は「行並列」構成でも逐次になる。3回のウォームアップ後15回を往復順で測り、各回のマスク一致を確認する。強制GCは行わない。

## 試作の範囲

3ベクトルで128bitなら4画素、256bitなら8画素の全チャネルを比較し、3bitずつORして画素の判定へ戻す。入力は既存のfloat32特徴量、出力は候補マスク1枚。特徴量・マスクの追加コピーや全ページの中間画像は作らない。末尾は現行スカラー式で処理する。

FMAを使用せず乗算と加算を分ける。ベクトルの入力区間はSpan.Sliceで事前検証し、範囲外をLoadUnsafeしない。全チャネルをまとめて計算するため、現行のチャネル単位の早期終了による速度上の利点は維持しない。出力一致と全面変更の性能を別々に評価する。

通常CLIの測定には、当時のソースをコピーしたgit管理外の比較用Core DLLを使った。手元に保持した入力・DLL・スクリプトの参照先は検証文書に記録している。このツール単体では通常CLIのSIMDを有効にしない。

APIの参照はMicrosoftの [.NET 10 Vector128](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.vector128?view=net-10.0) と [Vector256](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.vector256?view=net-10.0)。`IsHardwareAccelerated` による選択、ビットの抽出、明示的なFMAとの違いを確認した。速度・採否は資料の一般論ではなく、このリポジトリでの測定に基づく。
