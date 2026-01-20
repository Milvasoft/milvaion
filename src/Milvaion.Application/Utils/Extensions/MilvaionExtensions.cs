using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Milvasoft.Core.Utils.Constants;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Milvaion.Application.Utils.Extensions;

/// <summary>
/// Contains extension methods for the Milvaion.
/// </summary>
public static partial class MilvaionExtensions
{
    /// <summary>
    /// Gets current environment.
    /// </summary>
    /// <returns></returns>
    public static string GetCurrentEnvironment() => Environment.GetEnvironmentVariable("MILVA_ENV") ?? string.Empty;

    /// <summary>
    /// Checks if the current environment is production.
    /// </summary>
    /// <returns></returns>
    public static bool IsCurrentEnvProduction() => (GetCurrentEnvironment() ?? string.Empty) == "prod";

    /// <summary>
    /// Generates metadata for the request according to header.
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <returns></returns>
    public static bool GenerateMetadata(IServiceProvider serviceProvider)
    {
        var httpContextAccessor = serviceProvider.GetService<IHttpContextAccessor>();

        if (httpContextAccessor.HttpContext is null)
            return false;

        var exists = httpContextAccessor.HttpContext.Request.Headers.TryGetValue(GlobalConstant.GenerateMetadataHeaderKey, out var generateMetadata);

        return !string.IsNullOrWhiteSpace(generateMetadata) && exists && bool.Parse(generateMetadata);
    }

    /// <summary>
    /// Converts the input string to lowercase and removes diacritics (accents) from characters.
    /// </summary>
    /// <param name="input">The input string to be converted.</param>
    /// <returns>The converted string with lowercase letters and no diacritics.</returns>
    public static string ToLowerAndNonSpacingUnicode(this string input)
    {
        // Remove diacritics (accents) from characters
        string normalizedString = input.Normalize(NormalizationForm.FormD);
        char[] charArray = normalizedString.ToCharArray();
        var stringWithoutDiacriticsBuilder = new StringBuilder();

        foreach (char c in charArray)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                stringWithoutDiacriticsBuilder.Append(c);
            }
        }

        // Remove spaces and convert to lowercase
        string result = ReplaceAndToLowerRegex().Replace(stringWithoutDiacriticsBuilder.ToString(), "").ToLower();

        return result;
    }

    /// <summary>
    /// Calculates base 64 string length and returns whether it is valid.
    /// </summary>
    /// <param name="base64String"></param>
    /// <returns></returns>
    public static bool IsBase64StringValidLength(string base64String)
    {
        if (string.IsNullOrEmpty(base64String))
            return true;

        int base64StringLength = base64String.Length;

        int paddingCount;

        if (base64String.EndsWith('='))
        {
            paddingCount = base64String.EndsWith("==") ? 2 : 1;
        }
        else
        {
            paddingCount = base64String.EndsWith("==") ? 2 : 0;
        }

        long originalSize = (base64StringLength * 3 / 4) - paddingCount;

        double megabytes = (double)originalSize / (1024 * 1024);

        if (megabytes > 1)
            return false;

        return true;
    }

    /// <summary>
    /// Validates the base64 string by checking the file extension.
    /// </summary>
    /// <param name="base64String"></param>
    /// <returns></returns>
    public static bool IsBase64StringHasValidFileExtension(string base64String)
    {
        if (string.IsNullOrEmpty(base64String))
            return true;

        if (base64String.Contains("data:"))
        {
            var dataSection = base64String[..base64String.IndexOf(";base64")];

            if (dataSection.Contains(MimeTypeNames.ImageJpeg))
                return true;
            else if (dataSection.Contains(MimeTypeNames.ImagePng))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Determines whether the given string is a valid data uri base64 format.
    /// </summary>
    /// <param name="dataUri"></param>
    /// <returns></returns>
    public static bool IsValidDataUri(string dataUri) => DataUriRegex().IsMatch(dataUri);

    /// <summary>
    /// Converts data uri base64 string to plain text base64 string.
    /// </summary>
    /// <param name="dataUriBase64"></param>
    /// <returns></returns>
    public static byte[] DataUriToPlainText(string dataUriBase64)
    {
        if (string.IsNullOrEmpty(dataUriBase64))
            return [];

        var splitted = dataUriBase64.Split(";base64,");

        if (splitted.Length != 2)
            return [];

        var base64String = splitted[1];

        var array = Convert.FromBase64String(base64String);

        return array;
    }

    [GeneratedRegex(@"^data:(?<mediatype>[\w/+.-]+);base64,(?<data>[a-zA-Z0-9+/]+={0,2})$")]
    private static partial Regex DataUriRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex ReplaceAndToLowerRegex();

    [GeneratedRegex("^(ht|f)tp(s?)\\:\\/\\/[0-9a-zA-Z]([-.\\w]*[0-9a-zA-Z])*(:(0-9)*)*(\\/?)([a-zA-Z0-9\\-\\.\\?\\,\\'\\/\\\\\\+&%\\$#_]*)?$")]
    public static partial Regex UrlRegex();
}
