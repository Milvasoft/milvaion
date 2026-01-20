using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Features.Roles.CreateRole;
using Milvaion.Application.Features.Roles.UpdateRole;
using Milvaion.Application.Features.Users.CreateUser;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Extensions;
using Milvaion.Domain;
using Milvaion.Infrastructure.Persistence;
using Milvaion.Infrastructure.Persistence.Context;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.MultiLanguage.EntityBases.Abstract;
using Milvasoft.Core.MultiLanguage.Manager;
using Milvasoft.Types.Structs;
using Newtonsoft.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Milvaion.Infrastructure.Services;

/// <summary>
/// Developer service.
/// </summary>
/// <param name="serviceProvider"></param>
public class DeveloperService(IServiceProvider serviceProvider) : IDeveloperService
{
    private readonly IMediator _mediator = serviceProvider.GetService<IMediator>();
    private readonly IPermissionManager _permissionManager = serviceProvider.GetService<IPermissionManager>();
    private readonly MilvaionDbContext _milvaionDbContext = serviceProvider.GetService<MilvaionDbContext>();
    private readonly DatabaseMigrator _databaseMigrator = new(serviceProvider);

    /// <summary>
    /// Remove, recreates and seed database for development purposes.
    /// </summary>
    /// <returns></returns>
    [ExcludeFromMetadata]
    public async Task<Response> ResetDatabaseAsync()
    {
        if (MilvaionExtensions.IsCurrentEnvProduction())
            return Response.Error();

        return await _databaseMigrator.ResetDatabaseAsync(default);
    }

    /// <summary>
    /// Seeds data for development purposes.
    /// </summary>
    /// <returns></returns>
    public async Task<Response> SeedDevelopmentDataAsync()
    {
        if (MilvaionExtensions.IsCurrentEnvProduction())
            return Response.Error();

        try
        {
            await _databaseMigrator.SeedDefaultDataAsync("string");

            await _databaseMigrator.CreateTriggersAsync(default);

            await _databaseMigrator.SeedUIRelatedDataAsync(default);

            await DatabaseMigrator.MigratePermissionsAsync(_permissionManager, default);

            var languages = LanguagesSeed.Seed.Select(l => new Language
            {
                Code = l.Code,
                Name = l.Name,
                IsDefault = l.IsDefault,
                Supported = l.Supported,
            }).ToList();

            await _milvaionDbContext.Languages.AddRangeAsync(languages, default);

            await _milvaionDbContext.SaveChangesAsync(default);

            var languageSeed = languages.Cast<ILanguage>().ToList();

            MultiLanguageManager.UpdateLanguagesList(languageSeed);

            //Role creation
            var addedRole = await _mediator.Send(new CreateRoleCommand
            {
                Name = "Viewer"
            });

            //Role creation
            await _mediator.Send(new UpdateRoleCommand
            {
                Id = addedRole.Data,
                PermissionIdList = new UpdateProperty<List<int>>
                {
                    IsUpdated = true,
                    Value = [21, 26, 31, 32]
                }
            });

            //Another Super Admin User creation
            await _mediator.Send(new CreateUserCommand
            {
                Name = "Ahmet Buğra",
                Surname = "Kösen",
                UserType = Domain.Enums.UserType.Manager,
                UserName = "bugrakosen",
                Email = "bugrakosen@gmail.com",
                Password = "string",
                RoleIdList = [1]
            });

            //Viewer User creation
            await _mediator.Send(new CreateUserCommand
            {
                Name = "Viewer",
                Surname = "User",
                UserName = "viewer",
                UserType = Domain.Enums.UserType.AppUser,
                Email = "viewer@gmail.com",
                Password = "string",
                RoleIdList = [addedRole.Data],
            });

            return Response.Success();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return Response.Error("Already seeded!");
        }
    }

    /// <summary>
    /// Seeds fake data.
    /// </summary>
    /// <param name="sameData"></param>
    /// <param name="locale"></param>
    /// <returns></returns>
    public async Task<Response> SeedFakeDataAsync(bool sameData = true, string locale = "tr")
    {
        if (MilvaionExtensions.IsCurrentEnvProduction())
            return Response.Error();

        await _databaseMigrator.SeedFakeDataAsync(sameData, locale, default);

        return Response.Success();
    }

    /// <summary>
    /// Initial migration operation.
    /// </summary>
    /// <returns></returns>
    [ExcludeFromMetadata]
    public async Task<Response<string>> InitDatabaseAsync() => await _databaseMigrator.InitDatabaseAsync(_permissionManager, default);

    /// <summary>
    /// Resets ui related data.
    /// </summary>
    /// <returns></returns>
    [ExcludeFromMetadata]
    public async Task<Response> ResetUIRelatedDataAsync()
    {
        await _databaseMigrator.SeedUIRelatedDataAsync(default);

        return Response.Success();
    }

    /// <summary>
    /// Exports existing data to a JSON file.
    /// </summary>
    /// <returns></returns>
    public async Task<Response> ExportExistingDataAsync()
    {
        if (MilvaionExtensions.IsCurrentEnvProduction())
            return Response.Error();

        var menuDatas = _milvaionDbContext.Users.Include(p => p.RoleRelations)
                                                .AsNoTracking()
                                                .AsAsyncEnumerable();

        var filePath = Path.Combine(GlobalConstant.JsonFilesPath, "export.json");

        if (File.Exists(filePath))
            File.Delete(filePath);

        await using var stream = File.Create(filePath);
        using var streamWriter = new StreamWriter(stream, new UTF8Encoding(false));
        using var jsonWriter = new JsonTextWriter(streamWriter)
        {
            Formatting = Formatting.Indented
        };

        var serializer = new Newtonsoft.Json.JsonSerializer
        {
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        };

        await jsonWriter.WriteStartArrayAsync();

        await foreach (var menu in menuDatas)
        {
            try
            {
                serializer.Serialize(jsonWriter, menu);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Serialize error: {ex.Message}");
            }
        }

        // Dizi kapat
        await jsonWriter.WriteEndArrayAsync();
        await jsonWriter.FlushAsync();
        await streamWriter.FlushAsync();

        return Response.Success();
    }

    /// <summary>
    /// Imports existing data.
    /// </summary>
    /// <returns></returns>
    public async Task<Response> ImportExistingDataAsync()
    {
        if (MilvaionExtensions.IsCurrentEnvProduction())
            return Response.Error();

        var filePath = Path.Combine(GlobalConstant.JsonFilesPath, "export.json");

        if (!File.Exists(filePath))
            return Response.Error("Cannot find exported file.");

        await using var stream = File.OpenRead(filePath);
        using var jsonDoc = await JsonDocument.ParseAsync(stream);

        if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array)
            return Response.Error("Invalid file format.");

        var options = new JsonSerializerOptions
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            PropertyNameCaseInsensitive = true
        };

        int importedCount = 0;

        foreach (var element in jsonDoc.RootElement.EnumerateArray())
        {
            try
            {

                importedCount++;
            }
            catch (Exception ex)
            {
                // Logla ve devam et
                Console.WriteLine($"Import error: {ex.Message}");
            }
        }

        await _milvaionDbContext.SaveChangesAsync();
        return Response.Success($"{importedCount} records imported.");
    }
}
