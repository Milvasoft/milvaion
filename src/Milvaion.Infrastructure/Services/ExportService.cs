using ClosedXML.Excel;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Dtos.ExportDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Extensions;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions.Localization;
using Milvasoft.Core.Helpers;
using Milvasoft.Core.Utils.Constants;
using Milvasoft.Interception.Interceptors.Response;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Milvaion.Infrastructure.Services;

/// <summary>
/// Used to export data. 
/// </summary>
/// <param name="serviceProvider"></param>
public partial class ExportService(IServiceProvider serviceProvider) : IExportService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IMilvaLocalizer _localizer = serviceProvider.GetService<IMilvaLocalizer>();
    private readonly IHttpContextAccessor _httpContextAccessor = serviceProvider.GetService<IHttpContextAccessor>();
    private readonly IMediator _mediator = serviceProvider.GetService<IMediator>();

    /// <summary>
    /// Dynamically creates an excel file according to the export type.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<Response<ExportResult>> DynamicExportToExcelAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        _httpContextAccessor.HttpContext.ThrowIfCurrentUserNotAuthorized(request.GetRequiredPermissions());

        if (!request.IsValid())
            return Response<ExportResult>.Error(null, MessageKey.InvalidParameterException);

        var query = request.ListRequest == null
            ? Activator.CreateInstance(request.GetQueryType())
            : JsonSerializer.Deserialize(JsonSerializer.Serialize(request.ListRequest), request.GetQueryType());

        var queryResponse = await _mediator.Send(query, cancellationToken);

        var fileName = _localizer[$"ExportType.{request.ExportType}"];

        var excelStream = ExportToExcel((IHasMetadata)queryResponse, fileName);

        if (excelStream == null)
            return Response<ExportResult>.Error(null, MessageKey.NoDataToExport);

        var fileNameWithExtension = $"{fileName}_{DateTime.UtcNow:dd-MM-yyyy-HH-mm}.xlsx";

        var exportDto = new ExportResult
        {
            FileStream = excelStream,
            MimeType = FileHelper.MimeTypeHelper.GetMimeType(fileNameWithExtension),
            FileName = FileHelper.GetFileName(fileNameWithExtension)
        };

        return Response<ExportResult>.Success(exportDto);
    }

    /// <summary>
    /// Exports the data to excel.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="response"></param>
    /// <param name="pageName"></param>
    /// <returns></returns>
    public MemoryStream ExportToExcel<T>(ListResponse<T> response, string pageName = null) where T : class => ExportToExcel(hasMetadataResponse: response, pageName);

    /// <summary>
    /// Exports the data to excel.
    /// </summary>
    /// <param name="hasMetadataResponse"></param>
    /// <param name="pageName"></param>
    /// <returns></returns>
    public MemoryStream ExportToExcel(IHasMetadata hasMetadataResponse, string pageName = null)
    {
        var (responseData, responseDataType) = hasMetadataResponse.GetResponseDataTypePair();

        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet(pageName ?? "Sheet1");

        _httpContextAccessor.HttpContext.Request.Headers.Remove(GlobalConstant.GenerateMetadataHeaderKey);
        _httpContextAccessor.HttpContext.Request.Headers.TryAdd(GlobalConstant.GenerateMetadataHeaderKey, "true");

        var generator = new ResponseMetadataGenerator(_serviceProvider);
        generator.GenerateMetadata(hasMetadataResponse);

        _httpContextAccessor.HttpContext.Request.Headers.Remove(GlobalConstant.GenerateMetadataHeaderKey);

        var list = (IList)responseData;

        if (list.IsNullOrEmpty())
            return null;

        var metadatas = hasMetadataResponse.Metadatas;

        var actualType = responseDataType.GenericTypeArguments.FirstOrDefault() ?? responseDataType;

        var properties = actualType.GetProperties(BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

        var visibleMetadata = metadatas.Where(m => m.Display);
        var visibleProperties = properties.Where(p => visibleMetadata.Any(v => v.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))).ToList();

        visibleProperties.RemoveAll(i => i.Name == EntityPropertyNames.Id);

        // Header row
        for (var i = 0; i < visibleProperties.Count; i++)
        {
            var property = visibleProperties[i];
            var relatedMetadata = visibleMetadata.FirstOrDefault(m => m.Name.Equals(property.Name, StringComparison.OrdinalIgnoreCase));

            var headerCell = worksheet.Cell(1, i + 1);
            headerCell.Value = relatedMetadata?.LocalizedName ?? property.Name;
            headerCell.Style.Font.Bold = true;
            headerCell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        // Add datas
        for (var rowIndex = 0; rowIndex < list.Count; rowIndex++)
        {
            var item = list[rowIndex];

            for (var colIndex = 0; colIndex < visibleProperties.Count; colIndex++)
            {
                var property = visibleProperties[colIndex];
                var relatedMetadata = visibleMetadata.FirstOrDefault(m => m.Name.Equals(property.Name, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(relatedMetadata?.DisplayFormat) && property.PropertyType != typeof(decimal) && property.PropertyType != typeof(decimal?))
                {
                    var formattedValue = relatedMetadata.DisplayFormat;

                    var matches = DisplayFormatRegex().Matches(relatedMetadata.DisplayFormat);

                    foreach (Match match in matches)
                    {
                        var propPath = match.Groups[1].Value;

                        var propValue = GetNestedPropertyValue(item, propPath);

                        if (propValue == null)
                            formattedValue = relatedMetadata.DefaultValue?.ToString();
                        else
                            formattedValue = formattedValue?.Replace(match.Value, propValue.ToString());
                    }

                    worksheet.Cell(rowIndex + 2, colIndex + 1).Value = formattedValue;
                }
                else
                {
                    var value = property.GetValue(item);

                    worksheet.Cell(rowIndex + 2, colIndex + 1).Value = value?.ToString() ?? string.Empty;
                }
            }
        }

        // DateTime format
        var dateColumnIndexes = FindDateTimeColumns(visibleProperties);

        foreach (var item in dateColumnIndexes)
            worksheet.Column(item + 1).Cells().Style.NumberFormat.Format = "yyyy-MM-dd HH:mm:ss";

        worksheet.Columns().AdjustToContents();

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        return stream;
    }

    private static object GetNestedPropertyValue(object obj, string propPath)
    {
        if (obj == null || string.IsNullOrWhiteSpace(propPath))
            return null;

        var props = propPath.Split('.');

        var currentObject = obj;

        foreach (var prop in props)
        {
            if (currentObject == null)
                return null;

            var propertyInfo = currentObject.GetType().GetProperty(prop, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

            if (propertyInfo == null)
                return null;

            currentObject = propertyInfo.GetValue(currentObject);
        }

        return currentObject;
    }

    private static IEnumerable<int> FindDateTimeColumns(List<PropertyInfo> visibleProperties)
        => visibleProperties.Select((p, index) => new { Property = p, Index = index })
                            .Where(o => o.Property.PropertyType == typeof(DateTime) ||
                                        o.Property.PropertyType == typeof(DateTimeOffset) ||
                                        o.Property.PropertyType == typeof(DateTimeOffset?) ||
                                        o.Property.PropertyType == typeof(DateTime?))
                            .Select(o => o.Index);

    [GeneratedRegex(@"\{(.*?)\}")]
    private static partial Regex DisplayFormatRegex();
}
