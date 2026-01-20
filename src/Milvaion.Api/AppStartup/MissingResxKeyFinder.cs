using Bogus.DataSets;
using Milvaion.Application;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain;
using Milvaion.Domain.Enums;
using Milvasoft.Core.Helpers;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Milvaion.Api.AppStartup;

/// <summary>
/// Finds missing resx keys.
/// </summary>
public static partial class MissingResxKeyFinder
{
    /// <summary>
    /// Finds missing keys in .resx files.
    /// </summary>
    public static void FindAndPrintToConsole()
    {
#if !DEBUG
return;
#endif
        string projectFolderPath = Directory.GetParent(Directory.GetCurrentDirectory()).FullName;
        string resxFolderProjectPath = Directory.GetCurrentDirectory();
        string resxFolderPath = Path.Combine(resxFolderProjectPath, "LocalizationResources", "Resources");

        if (string.IsNullOrWhiteSpace(projectFolderPath) || string.IsNullOrWhiteSpace(resxFolderPath))
            return;

        var nameofReferences = FindNameofReferencesInLocalizer(projectFolderPath);

        HashSet<string> keysInNameOfReferences = [];

        var resxKeys = GetResxKeys(resxFolderPath);

        #region Find for MessageKey.cs

        foreach (var reference in nameofReferences)
        {
            var propertyName = ParseNameofExpressionAndGetPropertyName(reference);

            if (!string.IsNullOrWhiteSpace(propertyName))
                keysInNameOfReferences.Add(propertyName);
        }

        #endregion

        #region Find for Enum types

        var enumTypes = ApplicationAssembly.Assembly.GetExportedTypes().Where(t => t.IsEnum).Concat(DomainAssembly.Assembly.GetExportedTypes().Where(t => t.IsEnum));

        foreach (var enumType in enumTypes.Where(t => t != typeof(Currency) && t != typeof(UserActivity)))
        {
            var enumValues = Enum.GetNames(enumType);

            foreach (var enumValue in enumValues)
            {
                var enumKey = $"{enumType.Name}.{enumValue}";

                if (!resxKeys.Contains(enumType.Name))
                    keysInNameOfReferences.Add(enumType.Name);

                if (!resxKeys.Contains(enumKey))
                    keysInNameOfReferences.Add(enumKey);
            }
        }

        #endregion

        #region Find for PermissionCatalog sub classes

        var permissionCatalogType = typeof(PermissionCatalog);

        var permissionSubClasses = permissionCatalogType.GetNestedTypes().Where(t => t.IsClass && !t.IsValueType);

        foreach (var subClass in permissionSubClasses)
        {
            var permissionGroupKey = $"PG.{subClass.Name}";

            if (!resxKeys.Contains(permissionGroupKey))
                keysInNameOfReferences.Add(permissionGroupKey);
        }

        #endregion

        #region Find for Domain Entities

        var entityTypes = DomainAssembly.Assembly.GetExportedTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.TableAttribute>() != null);

        foreach (var entityType in entityTypes)
        {
            var entityKey = $"Global.{entityType.Name}";

            if (!resxKeys.Contains(entityKey))
                keysInNameOfReferences.Add(entityKey);
        }

        #endregion

        var constantKeys = GetConstantsFromClass(typeof(MessageKey));

        var missingKeys = constantKeys.Concat(keysInNameOfReferences).Except(resxKeys);

        if (!missingKeys.IsNullOrEmpty())
        {
            var warnMessage = "(Milva Framework Warning) Missing resx keys! The following key values were not found in any of the resx files:";
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(warnMessage);
            Console.ForegroundColor = ConsoleColor.Red;
            foreach (var missingKey in missingKeys)
                Console.WriteLine(missingKey);
            Console.ResetColor();
            Console.WriteLine();
        }
        else
        {
            var successMessage = "(Milva Framework Info) No missing resx keys found. All good!";
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(successMessage);
            Console.ResetColor();
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Gets all contanst strings from the given classes.
    /// </summary>
    static IEnumerable<string> GetConstantsFromClass(params Type[] types)
        => types.SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                                  .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
                                  .Select(f => f.GetRawConstantValue()?.ToString())
                                  .Where(value => value != null));

    static HashSet<string> GetResxKeys(string folderPath)
    {
        var keys = new HashSet<string>();

        var resxData = ResxReader.GetAllKeysFromResxFiles(folderPath);

        foreach (var kvp in resxData)
            keys.Add(kvp);

        return keys;
    }

    /// <summary>
    /// Finds all references to `nameof` in the project with localizer.
    /// </summary>
    static List<string> FindNameofReferencesInLocalizer(string folderPath)
    {
        var references = new List<string>();

        var regex = NameofInLocalizerRegex();

        foreach (var file in Directory.GetFiles(folderPath, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            var matches = regex.Matches(content);

            foreach (Match match in matches)
            {
                references.Add(match.Groups[1].Value);
            }
        }

        return references;
    }

    /// <summary>
    /// `nameof(Class.Property)` ifadesini parçalar ve sınıf ve özelliğin adını döndürür.
    /// </summary>
    static string ParseNameofExpressionAndGetPropertyName(string nameofExpression)
    {
        var parts = nameofExpression.Split('.');

        if (parts.Length == 2)
        {
            return parts[1];
        }

        return null;
    }

    [GeneratedRegex(@"zer\[nameof\(([\w\d_\.]+)\)\]", RegexOptions.Compiled)]
    public static partial Regex NameofInLocalizerRegex();
}

/// <summary>
/// Reads .resx files and extracts key-value pairs.
/// </summary>
public static class ResxReader
{
    /// <summary>
    /// Belirtilen bir .resx dosyasındaki tüm anahtar-değer çiftlerini okur.
    /// </summary>
    /// <param name="resxFilePath">.resx dosyasının tam yolu.</param>
    /// <returns>Anahtar ve değerlerden oluşan bir sözlük.</returns>
    public static Dictionary<string, string> ReadResxFile(string resxFilePath)
    {
        if (!File.Exists(resxFilePath))
            throw new FileNotFoundException("Cannot find resx file.", resxFilePath);

        var keyValuePairs = new Dictionary<string, string>();

        var xdoc = XDocument.Load(resxFilePath);

        // Read all data elements
        foreach (var dataElement in xdoc.Descendants("data"))
        {
            var key = dataElement.Attribute("name")?.Value;
            var value = dataElement.Element("value")?.Value;

            if (key != null && value != null)
            {
                keyValuePairs[key] = value;
            }
        }

        return keyValuePairs;
    }

    /// <summary>
    /// Bir klasördeki tüm .resx dosyalarını okur ve anahtarları toplar.
    /// </summary>
    /// <param name="folderPath">.resx dosyalarının bulunduğu klasör.</param>
    /// <returns>Anahtarların bir kümesi.</returns>
    public static HashSet<string> GetAllKeysFromResxFiles(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("Folder not found : " + folderPath);

        var keys = new HashSet<string>();

        foreach (var resxFile in Directory.GetFiles(folderPath, "*.resx", SearchOption.AllDirectories))
        {
            var resxKeys = ReadResxFile(resxFile);

            foreach (var key in resxKeys.Keys)
                keys.Add(key);
        }

        return keys;
    }
}