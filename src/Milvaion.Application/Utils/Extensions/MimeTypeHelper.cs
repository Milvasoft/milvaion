using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Milvasoft.Core.Utils.Constants;
using System.Text;

namespace Milvaion.Application.Utils.Extensions;

/// <summary>
/// File helpers.
/// </summary>
public static class FileHelper
{
    /// <summary>
    /// Source: https://en.wikipedia.org/wiki/List_of_file_signatures
    /// To read the first bytes of a file you can use the following command on UNIX systems: od -t x1 -N 10 filename
    /// </summary>
    public static Dictionary<string, List<byte[]>> FileSignatures { get; private set; } = new()
    {
        { ".apk", new List<byte[]> { new byte[] { 0x50, 0x4B, 0x03, 0x04 } } },
        { ".ipa", new List<byte[]> { new byte[] { 0x50, 0x4B, 0x03, 0x04 } } }
    };

    /// <summary>
    /// ipa file extension.
    /// </summary>
    public const string IpaExtension = ".ipa";

    /// <summary>
    /// apk file extension.
    /// </summary>
    public const string ApkExtension = ".apk";

    /// <summary>
    /// Gets boundary from content type.
    /// </summary>
    /// <param name="contentType"></param>
    /// <param name="lengthLimit"></param>
    /// <returns></returns>
    /// <exception cref="InvalidDataException"></exception>
    public static string GetBoundary(MediaTypeHeaderValue contentType, int lengthLimit)
    {
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;

        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new InvalidDataException("Missing content-type boundary.");
        }

        if (boundary.Length > lengthLimit)
        {
            throw new InvalidDataException(
                $"Multipart boundary length limit {lengthLimit} exceeded.");
        }

        return boundary;
    }

    /// <summary>
    /// Gets encoding from section.
    /// </summary>
    /// <param name="section"></param>
    /// <returns></returns>
    public static Encoding GetEncoding(MultipartSection section)
    {
        var hasMediaTypeHeader =
            MediaTypeHeaderValue.TryParse(section.ContentType, out var mediaType);

#pragma warning disable SYSLIB0001 // Type or member is obsolete
        // UTF-7 is insecure and shouldn't be honored. UTF-8 succeeds in most cases.
        if (!hasMediaTypeHeader || Encoding.UTF7.Equals(mediaType.Encoding))
        {
            return Encoding.UTF8;
        }
#pragma warning restore SYSLIB0001 // Type or member is obsolete

        return mediaType.Encoding;
    }

    /// <summary>
    /// Checks if content disposition has file.
    /// </summary>
    /// <param name="contentDisposition"></param>
    /// <returns></returns>
    public static bool HasFileContentDisposition(this ContentDispositionHeaderValue contentDisposition)
        => contentDisposition != null && contentDisposition.DispositionType.Equals("form-data") &&
           (!string.IsNullOrEmpty(contentDisposition.FileName.Value) || !string.IsNullOrEmpty(contentDisposition.FileNameStar.Value));

    /// <summary>
    /// Cheks form data has content disposition.
    /// </summary>
    /// <param name="contentDisposition"></param>
    /// <returns></returns>
    public static bool HasFormDataContentDisposition(this ContentDispositionHeaderValue contentDisposition)
        => contentDisposition != null
           && contentDisposition.DispositionType.Equals("form-data")
           && string.IsNullOrEmpty(contentDisposition.FileName.Value)
           && string.IsNullOrEmpty(contentDisposition.FileNameStar.Value);

    /// <summary>
    /// Gets file extension from file path.
    /// </summary>
    /// <returns></returns>
    public static string GetFileExtension(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return string.Empty;

        return Path.GetExtension(filePath).ToString();
    }

    /// <summary>
    /// Gets file name without extension from file path.
    /// </summary>
    /// <returns></returns>
    public static string GetFileNameWithoutExtension(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return string.Empty;

        return Path.GetFileNameWithoutExtension(filePath);
    }

    /// <summary>
    /// Gets file name with extension from file path.
    /// </summary>
    /// <returns></returns>
    public static string GetFileName(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return string.Empty;

        return Path.GetFileName(filePath);
    }

    /// <summary>
    /// Mime type helper
    /// </summary>
    public static class MimeTypeHelper
    {
        /// <summary>
        /// .apk mime type.
        /// </summary>
        public const string ApplicationApk = "application/vnd.android.package-archive";

        /// <summary>
        /// .ipa mime type.
        /// </summary>
        public const string ApplicationIpa = "application/x-itunes-ipa";

        /// <summary>
        /// File extension mime type pairs.
        /// </summary>
        public static IDictionary<string, string> ExtensionMimeTypePairs { get; }

        private static readonly FileExtensionContentTypeProvider _provider = new();

        static MimeTypeHelper()
        {
            _provider.Mappings.TryAdd(IpaExtension, ApplicationIpa);
            _provider.Mappings.TryAdd(ApkExtension, ApplicationApk);
            ExtensionMimeTypePairs = _provider.Mappings;
        }

        /// <summary>
        /// Gets mime type from file extension.
        /// </summary>
        /// <param name="fileExtension"></param>
        /// <returns></returns>
        public static string GetMimeType(string fileExtension)
        {
            if (_provider.TryGetContentType(fileExtension, out var contentType))
                return contentType;

            return MimeTypeNames.ApplicationOctetStream;
        }
    }
}
