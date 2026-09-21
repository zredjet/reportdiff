using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace ReportDiff.Pdf;

public sealed record PdfFontWarning(string Code, string Message);

/// <summary>使用したフォントの元辞書を調べる。描画やテキスト注釈の成否とは別に警告を返す。</summary>
internal sealed class PdfFontInspector(PdfDocument document)
{
    internal const string IncompleteCode = "FONT_INSPECTION_INCOMPLETE";

    public IReadOnlyList<PdfFontWarning> Inspect(Page page)
    {
        var warnings = new List<PdfFontWarning>();
        var references = new HashSet<IndirectReference>();
        var directFonts = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var letter in page.Letters)
        {
            var details = letter.FontDetails;
            if (details.FontDictionaryReference is not { } reference)
            {
                if (directFonts.Add((object?)letter.GetFont() ?? details))
                    warnings.Add(Incomplete("フォント名不明（直接辞書または代替フォント）: 元のフォント辞書を特定できません。"));
                continue;
            }
            if (!references.Add(reference)) continue;
            if (InspectFont(reference) is { } warning) warnings.Add(warning);
        }

        // 外観は PDFium では描画するが、PdfPig の Letters には含まれない。
        try
        {
            if (Get(page.Dictionary, "Annots") is { } annotations
                && Required<ArrayToken>(annotations).Data.Count > 0)
                warnings.Add(Incomplete("PDF 注釈・入力フォームの外観はフォント検査の対象外です。本文と Form XObject だけを検査しています。"));
            var resources = InheritedResources(page.Dictionary);
            if (resources is not null && HasUninspectedResources(resources, false))
                warnings.Add(Incomplete("リソースにタイルパターン等があります。その内部のフォント使用は検査できません。"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            warnings.Add(Incomplete("ページの注釈・リソースを解析できず、フォント検査の範囲を確認できません。"));
        }
        return warnings.AsReadOnly();
    }

    internal PdfFontWarning? InspectFont(IndirectReference reference)
    {
        var identity = $"フォント名不明（{reference.ObjectNumber} {reference.Generation} R）";
        try
        {
            var font = Required<DictionaryToken>(new IndirectReferenceToken(reference));
            var subtype = Name(font, "Subtype");
            var name = Name(font, subtype == "Type3" ? "Name" : "BaseFont") ?? "名前不明";
            identity = $"フォント「{name}」（{reference.ObjectNumber} {reference.Generation} R）";
            return InspectDefinition(font) is { } warning
                ? warning with { Message = $"{identity}: {warning.Message}" } : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Incomplete($"{identity}: 辞書・参照・ストリームを解析できず、埋め込みを確認できません。");
        }
    }

    // 字形自体の正しさではなく、埋め込みプログラムの所在を検査する。
    private PdfFontWarning? InspectDefinition(DictionaryToken font)
    {
        var subtype = Name(font, "Subtype");
        if (subtype == "Type0")
        {
            var descendants = Required<ArrayToken>(Get(font, "DescendantFonts"));
            if (descendants.Data.Count != 1) return Incomplete("DescendantFonts の定義を確認できません。");
            font = Required<DictionaryToken>(descendants.Data[0]);
            subtype = Name(font, "Subtype");
            if (subtype is not ("CIDFontType0" or "CIDFontType2"))
                return Incomplete("未対応の子フォント形式のため、埋め込みを確認できません。");
        }
        else if (subtype == "Type3")
        {
            var glyphs = Required<DictionaryToken>(Get(font, "CharProcs"));
            if (glyphs.Data.Count == 0) return Incomplete("Type3 の字形プログラムが空です。");
            foreach (var glyph in glyphs.Data.Values) CheckStream(glyph);
            if (Get(font, "Resources") is { } resources
                && HasUninspectedResources(Required<DictionaryToken>(resources), true))
                return Incomplete("Type3 の字形リソース内にある別フォント・パターンの使用は検査できません。");
            return null;
        }
        else if (subtype is not ("Type1" or "MMType1" or "TrueType"))
            return Incomplete("未対応のフォント形式のため、埋め込みを確認できません。");

        if (Get(font, "FontDescriptor") is not { } descriptorToken)
            return Missing("FontDescriptor がなく、フォントプログラムが埋め込まれていません。");
        var descriptor = Required<DictionaryToken>(descriptorToken);
        var present = false;
        foreach (var key in new[] { "FontFile", "FontFile2", "FontFile3" })
        {
            if (Get(descriptor, key) is not { } token) continue;
            // 種類に対応しない指定を「埋め込み済み」と扱わない。
            if (key == "FontFile" && subtype is not ("Type1" or "MMType1")
                || key == "FontFile2" && subtype is not ("TrueType" or "CIDFontType2"))
                return Incomplete("フォント形式と埋め込みプログラムの指定が一致しません。");
            var stream = CheckStream(token);
            if (key == "FontFile3")
            {
                var programType = Name(stream.StreamDictionary, "Subtype");
                if (programType != "OpenType" && !(programType == "Type1C" && subtype is "Type1" or "MMType1")
                    && !(programType == "CIDFontType0C" && subtype == "CIDFontType0"))
                    return Incomplete("FontFile3 のフォントプログラム形式を確認できません。");
            }
            present = true;
        }
        return present ? null : Missing("FontDescriptor に FontFile / FontFile2 / FontFile3 がなく、フォントプログラムが埋め込まれていません。");
    }

    private StreamToken CheckStream(IToken token)
    {
        var stream = Required<StreamToken>(token);
        if (stream.Decode(document.Structure.FilterProvider, document.Structure.TokenScanner).Length == 0)
            throw new InvalidDataException("埋め込みストリームが空です。");
        return stream;
    }

    private DictionaryToken? InheritedResources(DictionaryToken page)
    {
        var visited = new HashSet<DictionaryToken>(ReferenceEqualityComparer.Instance);
        var parents = new HashSet<IndirectReference>();
        while (visited.Add(page))
        {
            if (Get(page, "Resources") is { } resources) return Required<DictionaryToken>(resources);
            if (Get(page, "Parent") is not { } parent) return null;
            if (parent is IndirectReferenceToken reference && !parents.Add(reference.Data))
                throw new InvalidDataException("ページツリーの参照が循環しています。");
            page = Required<DictionaryToken>(parent);
        }
        throw new InvalidDataException("ページツリーが循環しています。");
    }

    private bool HasUninspectedResources(DictionaryToken resources, bool insideGlyph)
    {
        var pending = new Stack<DictionaryToken>();
        var visited = new HashSet<DictionaryToken>(ReferenceEqualityComparer.Instance);
        // PdfPig が参照を解決するたびに新しい辞書を返す場合もあるため、
        // .NET のオブジェクト同一性だけでは PDF 内の循環を止められない。
        var objectsSeen = new HashSet<IndirectReference>();
        pending.Push(resources);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (Get(current, "Pattern") is { } patterns && Required<DictionaryToken>(patterns).Data.Count > 0)
                return true;
            if (insideGlyph && Get(current, "Font") is { } fonts && Required<DictionaryToken>(fonts).Data.Count > 0)
                return true;
            if (Get(current, "XObject") is not { } objects) continue;
            foreach (var value in Required<DictionaryToken>(objects).Data.Values)
            {
                if (value is IndirectReferenceToken reference && !objectsSeen.Add(reference.Data)) continue;
                var stream = Required<StreamToken>(value);
                if (Name(stream.StreamDictionary, "Subtype") == "Form"
                    && Get(stream.StreamDictionary, "Resources") is { } nested)
                    pending.Push(Required<DictionaryToken>(nested));
            }
        }
        return false;
    }

    private static IToken? Get(DictionaryToken dictionary, string key) =>
        dictionary.TryGet(NameToken.Create(key), out var token) && token is not NullToken ? token : null;

    private string? Name(DictionaryToken dictionary, string key) =>
        Get(dictionary, key) is { } token ? Required<NameToken>(token).Data : null;

    private T Required<T>(IToken? token) where T : class, IToken
    {
        var visited = new HashSet<IndirectReference>();
        while (token is IndirectReferenceToken reference)
        {
            if (!visited.Add(reference.Data)) throw new InvalidDataException("PDF オブジェクト参照が循環しています。");
            token = document.Structure.GetObject(reference.Data).Data;
        }
        return token as T ?? throw new InvalidDataException("PDF オブジェクトの型が不正です。");
    }

    internal static PdfFontWarning Incomplete(string message) => new(IncompleteCode, message);
    private static PdfFontWarning Missing(string message) => new("NON_EMBEDDED_FONT", message + " 同じ実行環境で比較し、元の PDF へのフォント埋め込みを検討してください。");
}
