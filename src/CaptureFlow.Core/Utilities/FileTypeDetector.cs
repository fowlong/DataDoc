using CaptureFlow.Core.Models;

namespace CaptureFlow.Core.Utilities;

public static class FileTypeDetector
{
    private static readonly Dictionary<string, SupportedFileType> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = SupportedFileType.Pdf,
        [".docx"] = SupportedFileType.Docx,
        [".doc"] = SupportedFileType.Doc,
        [".txt"] = SupportedFileType.Txt,
        [".rtf"] = SupportedFileType.Rtf,
        [".html"] = SupportedFileType.Html,
        [".htm"] = SupportedFileType.Html,
        [".eml"] = SupportedFileType.Eml,
        [".msg"] = SupportedFileType.Msg,
        [".png"] = SupportedFileType.Png,
        [".jpg"] = SupportedFileType.Jpg,
        [".jpeg"] = SupportedFileType.Jpg,
        [".tiff"] = SupportedFileType.Tiff,
        [".tif"] = SupportedFileType.Tiff,
        [".bmp"] = SupportedFileType.Bmp,
        [".odt"] = SupportedFileType.Odt,
        [".xps"] = SupportedFileType.Xps,
        [".xml"] = SupportedFileType.Xml,
    };

    public static SupportedFileType Detect(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ExtensionMap.GetValueOrDefault(ext, SupportedFileType.Unknown);
    }

    public static bool IsSupported(string filePath)
        => Detect(filePath) != SupportedFileType.Unknown;

    public static IReadOnlyList<string> GetSupportedExtensions()
        => ExtensionMap.Keys.ToList();

    public static string GetFileFilter()
    {
        var extensions = string.Join(";", ExtensionMap.Keys.Select(e => $"*{e}"));
        return $"Supported Documents ({extensions})|{extensions}|All Files (*.*)|*.*";
    }

    public static bool IsImageType(SupportedFileType type)
        => type is SupportedFileType.Png or SupportedFileType.Jpg
            or SupportedFileType.Tiff or SupportedFileType.Bmp;
}
