using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Exceptions;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HttpWorker.Jobs;

/// <summary>
/// Generic HTTP request sender job that can handle any HTTP request.
/// Uses IAsyncJobWithResult&lt;HttpJobData&gt; to define expected data schema.
/// Returns response summary as result for chaining/debugging.
/// Uses hybrid HttpClient strategy: IHttpClientFactory for standard requests, custom handler for special cases.
/// </summary>
public partial class HttpRequestSenderJob(IHttpClientFactory httpClientFactory) : IAsyncJobWithResult<HttpJobData>
{
    /// <summary>
    /// Named client for standard HTTP requests with connection pooling.
    /// </summary>
    public const string DefaultClientName = "MilvaionHttpWorker";

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly int[] _defaultRetryStatusCodes = [408, 429, 500, 502, 503, 504];

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        // 1. Deserialize job data (type-safe)
        var jobData = context.GetData<HttpJobData>() ?? throw new PermanentJobException("HttpJobData is required but was null");

        context.LogInformation($"Starting HTTP {jobData.Method} request to {jobData.Url}");

        // 2. Build the final URL with path and query parameters
        var finalUrl = BuildUrl(jobData);

        context.LogInformation($"Final URL: {finalUrl}");

        // 3. Get or create HttpClient based on requirements (Hybrid Strategy)
        HttpClient client;
        HttpClientHandler customHandler = null;
        var requiresCustomHandler = RequiresCustomHandler(jobData);

        if (requiresCustomHandler)
        {
            // Custom handler needed - create new HttpClient with handler
            customHandler = CreateHttpHandler(jobData);
            client = new HttpClient(customHandler);
            context.LogInformation("Using custom HttpClient (proxy/SSL/cert/cookies configured)");
        }
        else
        {
            // Standard request - use pooled HttpClient from factory
            client = _httpClientFactory.CreateClient(DefaultClientName);
            context.LogInformation("Using pooled HttpClient from factory");
        }

        try
        {
            ConfigureClient(client, jobData);

            // 4. Build the request
            using var request = await BuildRequestAsync(jobData, finalUrl, context);

            // 5. Execute with retry policy
            var response = await ExecuteWithRetryAsync(client, request, jobData, context);

            // 6. Process and validate response
            await ProcessResponseAsync(response, jobData, context);

            // 7. Read and return response body as result
            var responseBody = await response.Content.ReadAsStringAsync();

            context.LogInformation($"HTTP request completed successfully with status {(int)response.StatusCode} {response.StatusCode}");

            // Return response summary as result (truncate large responses)
            return JsonSerializer.Serialize(new
            {
                StatusCode = (int)response.StatusCode,
                Status = response.StatusCode.ToString(),
                ContentLength = responseBody.Length,
                Body = responseBody.Length > 2000 ? responseBody[..2000] + "...(truncated)" : responseBody
            }, _jsonOptions);
        }
        finally
        {
            // Dispose custom handler and client if we created them
            if (requiresCustomHandler)
            {
                client.Dispose();
                customHandler?.Dispose();
            }
        }
    }

    #region Hybrid HttpClient Strategy

    /// <summary>
    /// Determines if the job requires a custom HttpClientHandler (cannot use pooled client).
    /// </summary>
    private static bool RequiresCustomHandler(HttpJobData jobData) => jobData.IgnoreSslErrors
                                                                      || jobData.Proxy != null
                                                                      || jobData.ClientCertificate != null
                                                                      || jobData.Cookies?.Count > 0
                                                                      || !jobData.FollowRedirects
                                                                      || jobData.MaxRedirects != 5;

    #endregion

    #region URL Building

    private static string BuildUrl(HttpJobData jobData)
    {
        var url = jobData.Url;

        // Replace path parameters
        if (jobData.PathParameters?.Count > 0)
        {
            foreach (var (key, value) in jobData.PathParameters)
            {
                url = url.Replace($"{{{key}}}", Uri.EscapeDataString(value));
            }
        }

        // Append query parameters
        if (jobData.QueryParameters?.Count > 0)
        {
            var queryString = string.Join("&",
                jobData.QueryParameters.Select(kvp =>
                    $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

            url = url.Contains('?') ? $"{url}&{queryString}" : $"{url}?{queryString}";
        }

        return url;
    }

    #endregion

    #region HttpClient Configuration

    private static HttpClientHandler CreateHttpHandler(HttpJobData jobData)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = jobData.FollowRedirects,
            MaxAutomaticRedirections = jobData.MaxRedirects,
            AutomaticDecompression = DecompressionMethods.All,
        };

        // SSL/TLS settings
        if (jobData.IgnoreSslErrors)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        // Proxy configuration
        if (jobData.Proxy != null)
        {
            var proxy = new WebProxy(jobData.Proxy.Url);

            if (!string.IsNullOrEmpty(jobData.Proxy.Username))
            {
                proxy.Credentials = new NetworkCredential(jobData.Proxy.Username, jobData.Proxy.Password);
            }

            if (jobData.Proxy.BypassList?.Length > 0)
            {
                proxy.BypassList = jobData.Proxy.BypassList;
            }

            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        // Client certificate for mutual TLS
        if (jobData.ClientCertificate != null)
        {
            var cert = LoadClientCertificate(jobData.ClientCertificate);

            if (cert != null)
            {
                handler.ClientCertificates.Add(cert);
            }
        }

        // Cookies
        if (jobData.Cookies?.Count > 0)
        {
            handler.CookieContainer = new CookieContainer();

            var uri = new Uri(jobData.Url);

            foreach (var (name, value) in jobData.Cookies)
            {
                handler.CookieContainer.Add(uri, new Cookie(name, value));
            }
        }

        return handler;
    }

    private static X509Certificate2 LoadClientCertificate(HttpClientCertificate certConfig)
    {
        if (!string.IsNullOrEmpty(certConfig.CertificateBase64))
        {
            var certBytes = Convert.FromBase64String(certConfig.CertificateBase64);

            return X509CertificateLoader.LoadPkcs12(certBytes, certConfig.Password, X509KeyStorageFlags.EphemeralKeySet);
        }

        if (!string.IsNullOrEmpty(certConfig.Thumbprint))
        {
            var storeLocation = certConfig.StoreLocation?.ToLowerInvariant() == "localmachine" ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;

            using var store = new X509Store(StoreName.My, storeLocation);

            store.Open(OpenFlags.ReadOnly);

            var certs = store.Certificates.Find(X509FindType.FindByThumbprint, certConfig.Thumbprint, false);

            return certs.Count > 0 ? certs[0] : null;
        }

        return null;
    }

    private static void ConfigureClient(HttpClient client, HttpJobData jobData)
    {
        client.Timeout = TimeSpan.FromSeconds(jobData.TimeoutSeconds);

        if (!string.IsNullOrEmpty(jobData.UserAgent))
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(jobData.UserAgent);
        }
        else
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Milvaion-HttpWorker/1.0");
        }

        if (jobData.AcceptEncoding?.Length > 0)
        {
            foreach (var encoding in jobData.AcceptEncoding)
            {
                client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));
            }
        }
    }

    #endregion

    #region Request Building

    private static async Task<HttpRequestMessage> BuildRequestAsync(HttpJobData jobData, string url, IJobContext context)
    {
        var method = jobData.Method switch
        {
            HttpMethodType.GET => HttpMethod.Get,
            HttpMethodType.POST => HttpMethod.Post,
            HttpMethodType.PUT => HttpMethod.Put,
            HttpMethodType.DELETE => HttpMethod.Delete,
            HttpMethodType.PATCH => HttpMethod.Patch,
            HttpMethodType.HEAD => HttpMethod.Head,
            HttpMethodType.OPTIONS => HttpMethod.Options,
            _ => HttpMethod.Get
        };

        var request = new HttpRequestMessage(method, url);

        // Add headers
        if (jobData.Headers?.Count > 0)
        {
            foreach (var (key, value) in jobData.Headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        // Add authentication
        await AddAuthenticationAsync(request, jobData, context);

        // Add body
        if (jobData.Body != null && jobData.Body.Type != HttpBodyType.None)
        {
            request.Content = await BuildContentAsync(jobData.Body, context);
        }

        return request;
    }

    private static async Task AddAuthenticationAsync(HttpRequestMessage request, HttpJobData jobData, IJobContext context)
    {
        if (jobData.Authentication == null || jobData.Authentication.Type == HttpAuthType.None)
            return;

        switch (jobData.Authentication.Type)
        {
            case HttpAuthType.Basic:
                var basicCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{jobData.Authentication.Credential}:{jobData.Authentication.Secret}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCredentials);
                break;

            case HttpAuthType.Bearer:
                var prefix = jobData.Authentication.AuthorizationPrefix ?? "Bearer";
                request.Headers.Authorization = new AuthenticationHeaderValue(prefix, jobData.Authentication.Credential);
                break;

            case HttpAuthType.ApiKey:
                var keyName = jobData.Authentication.KeyName ?? "X-API-Key";
                var keyValue = jobData.Authentication.Credential;

                switch (jobData.Authentication.KeyLocation)
                {
                    case ApiKeyLocation.Header:
                        request.Headers.TryAddWithoutValidation(keyName, keyValue);
                        break;
                    case ApiKeyLocation.Query:
                        var uriBuilder = new UriBuilder(request.RequestUri!);
                        var query = uriBuilder.Query.TrimStart('?');
                        query = string.IsNullOrEmpty(query)
                            ? $"{keyName}={Uri.EscapeDataString(keyValue!)}"
                            : $"{query}&{keyName}={Uri.EscapeDataString(keyValue!)}";
                        uriBuilder.Query = query;
                        request.RequestUri = uriBuilder.Uri;
                        break;
                    case ApiKeyLocation.Cookie:
                        request.Headers.TryAddWithoutValidation("Cookie", $"{keyName}={keyValue}");
                        break;
                }

                break;

            case HttpAuthType.OAuth2:
                var token = await FetchOAuth2TokenAsync(jobData.Authentication, context);

                if (!string.IsNullOrEmpty(token))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                break;

            case HttpAuthType.Digest:
            case HttpAuthType.Ntlm:
                context.LogWarning($"Warning: {jobData.Authentication.Type} authentication requires handler-level configuration");
                break;
        }
    }

    private static async Task<string> FetchOAuth2TokenAsync(HttpAuthentication auth, IJobContext context)
    {
        if (string.IsNullOrEmpty(auth.TokenUrl))
            throw new PermanentJobException("OAuth2 requires TokenUrl - this is a configuration error");

        context.LogInformation($"Fetching OAuth2 token from {auth.TokenUrl}");

        using var client = new HttpClient();
        var parameters = new Dictionary<string, string>
        {
            ["grant_type"] = auth.GrantType switch
            {
                OAuth2GrantType.ClientCredentials => "client_credentials",
                OAuth2GrantType.Password => "password",
                OAuth2GrantType.RefreshToken => "refresh_token",
                OAuth2GrantType.AuthorizationCode => "authorization_code",
                _ => "client_credentials"
            },
            ["client_id"] = auth.ClientId ?? "",
            ["client_secret"] = auth.ClientSecret ?? ""
        };

        if (auth.Scopes?.Length > 0)
        {
            parameters["scope"] = string.Join(" ", auth.Scopes);
        }

        if (auth.GrantType == OAuth2GrantType.Password)
        {
            parameters["username"] = auth.Credential ?? "";
            parameters["password"] = auth.Secret ?? "";
        }

        var content = new FormUrlEncodedContent(parameters);
        var response = await client.PostAsync(auth.TokenUrl, content);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        return doc.RootElement.TryGetProperty("access_token", out var tokenElement)
            ? tokenElement.GetString()
            : throw new PermanentJobException("OAuth2 response did not contain access_token - check token endpoint configuration");
    }

    private static async Task<HttpContent> BuildContentAsync(HttpRequestBody body, IJobContext context)
    {
        var encoding = Encoding.GetEncoding(body.Encoding ?? "utf-8");

        switch (body.Type)
        {
            case HttpBodyType.Json:
                var jsonContent = body.Content switch
                {
                    string s => s,
                    JsonElement je => je.GetRawText(),
                    _ => JsonSerializer.Serialize(body.Content, _jsonOptions)
                };
                return new StringContent(jsonContent, encoding, new MediaTypeHeaderValue(body.ContentTypeOverride ?? "application/json"));

            case HttpBodyType.Xml:
                return new StringContent(body.Content?.ToString() ?? "", encoding, new MediaTypeHeaderValue(body.ContentTypeOverride ?? "application/xml"));

            case HttpBodyType.Text:
                return new StringContent(body.Content?.ToString() ?? "", encoding, new MediaTypeHeaderValue(body.ContentTypeOverride ?? "text/plain"));

            case HttpBodyType.Html:
                return new StringContent(body.Content?.ToString() ?? "", encoding, new MediaTypeHeaderValue(body.ContentTypeOverride ?? "text/html"));

            case HttpBodyType.FormUrlEncoded:
                if (body.FormData == null)
                    throw new PermanentJobException("FormUrlEncoded requires FormData - check job configuration");
                return new FormUrlEncodedContent(body.FormData);

            case HttpBodyType.Multipart:
                return await BuildMultipartContentAsync(body, context);

            case HttpBodyType.Binary:
                var bytes = body.Content switch
                {
                    string base64 => Convert.FromBase64String(base64),
                    byte[] b => b,
                    _ => throw new PermanentJobException("Binary content must be base64 string or byte array")
                };
                var binaryContent = new ByteArrayContent(bytes);
                binaryContent.Headers.ContentType = new MediaTypeHeaderValue(body.ContentTypeOverride ?? "application/octet-stream");
                return binaryContent;

            case HttpBodyType.GraphQL:
                var graphqlContent = body.Content switch
                {
                    string s => s,
                    _ => JsonSerializer.Serialize(body.Content, _jsonOptions)
                };
                return new StringContent(graphqlContent, encoding, new MediaTypeHeaderValue(body.ContentTypeOverride ?? "application/json"));

            default:
                return new StringContent("", encoding);
        }
    }

    private static async Task<MultipartFormDataContent> BuildMultipartContentAsync(HttpRequestBody body, IJobContext context)
    {
        var content = new MultipartFormDataContent();

        // Add form fields
        if (body.FormData?.Count > 0)
        {
            foreach (var (key, value) in body.FormData)
            {
                content.Add(new StringContent(value), key);
            }
        }

        // Add files
        if (body.Files?.Count > 0)
        {
            foreach (var file in body.Files)
            {
                byte[] fileBytes;

                if (!string.IsNullOrEmpty(file.ContentBase64))
                {
                    fileBytes = Convert.FromBase64String(file.ContentBase64);
                }
                else if (!string.IsNullOrEmpty(file.ContentUrl))
                {
                    context.LogInformation($"Downloading file from {file.ContentUrl}");
                    using var client = new HttpClient();
                    fileBytes = await client.GetByteArrayAsync(file.ContentUrl);
                }
                else
                {
                    throw new PermanentJobException($"File {file.FileName} has no content source (ContentBase64 or ContentUrl required)");
                }

                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
                content.Add(fileContent, file.FieldName, file.FileName);
            }
        }

        return content;
    }

    #endregion

    #region Retry Logic

    private static async Task<HttpResponseMessage> ExecuteWithRetryAsync(
        HttpClient client,
        HttpRequestMessage request,
        HttpJobData jobData,
        IJobContext context)
    {
        var retryPolicy = jobData.RetryPolicy;
        var maxRetries = retryPolicy?.MaxRetries ?? 0;
        var retryStatusCodes = retryPolicy?.RetryOnStatusCodes ?? _defaultRetryStatusCodes;
        var initialDelay = retryPolicy?.InitialDelayMs ?? 1000;
        var maxDelay = retryPolicy?.MaxDelayMs ?? 30000;
        var backoffMultiplier = retryPolicy?.BackoffMultiplier ?? 2.0;

        HttpResponseMessage response = null;
        Exception lastException = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var delay = Math.Min((int)(initialDelay * Math.Pow(backoffMultiplier, attempt - 1)), maxDelay);
                context.LogInformation($"Retry attempt {attempt}/{maxRetries} after {delay}ms delay");
                await Task.Delay(delay, context.CancellationToken);

                // Clone the request for retry (original request is disposed after first use)
                request = await CloneRequestAsync(request);
            }

            try
            {
                response = await client.SendAsync(request, context.CancellationToken);

                // Check if we should retry based on status code
                if (attempt < maxRetries && retryStatusCodes.Contains((int)response.StatusCode))
                {
                    context.LogInformation($"Received {(int)response.StatusCode} {response.StatusCode}, will retry...");
                    continue;
                }

                return response;
            }
            catch (TaskCanceledException ex) when (!context.CancellationToken.IsCancellationRequested)
            {
                // Timeout
                lastException = ex;
                if (attempt < maxRetries && (retryPolicy?.RetryOnTimeout ?? true))
                {
                    context.LogError($"Request timed out, will retry...");
                    continue;
                }

                throw new TimeoutException($"HTTP request timed out after {jobData.TimeoutSeconds} seconds", ex);
            }
            catch (HttpRequestException ex)
            {
                // Connection error
                lastException = ex;
                if (attempt < maxRetries && (retryPolicy?.RetryOnConnectionError ?? true))
                {
                    context.LogError($"Connection error: {ex.Message}, will retry...");
                    continue;
                }

                throw;
            }
        }

        // If we get here without a response, throw the last exception
        if (response == null && lastException != null)
            throw lastException;

        return response!;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);

        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (original.Content != null)
        {
            var content = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(content);

            foreach (var header in original.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    #endregion

    #region Response Validation

    private static async Task<string> ProcessResponseAsync(HttpResponseMessage response, HttpJobData jobData, IJobContext context)
    {
        var validation = jobData.Validation;
        var statusCode = (int)response.StatusCode;
        string responseBody = null;

        // Check for permanent failure status codes (should not retry)
        if (statusCode is 400 or 401 or 403 or 404 or 405 or 422)
        {
            responseBody = await response.Content.ReadAsStringAsync();
            throw new PermanentJobException($"HTTP {statusCode} {response.StatusCode}: {responseBody}");
        }

        if (validation == null)
        {
            // No validation required, just ensure success status code
            response.EnsureSuccessStatusCode();
            return responseBody;
        }

        // Validate status code
        if (validation.ExpectedStatusCodes?.Length > 0)
        {
            if (!validation.ExpectedStatusCodes.Contains(statusCode))
            {
                throw new HttpRequestException($"Unexpected status code {statusCode}. Expected one of: {string.Join(", ", validation.ExpectedStatusCodes)}");
            }
        }

        // Validate required headers
        if (validation.RequiredHeaders?.Count > 0)
        {
            foreach (var (headerName, expectedValue) in validation.RequiredHeaders)
            {
                if (!response.Headers.TryGetValues(headerName, out var values) && !response.Content.Headers.TryGetValues(headerName, out values))
                    throw new HttpRequestException($"Required header '{headerName}' not found in response");

                var actualValue = string.Join(", ", values);

                if (!string.IsNullOrEmpty(expectedValue) && actualValue != expectedValue)
                {
                    throw new HttpRequestException(
                        $"Header '{headerName}' has value '{actualValue}', expected '{expectedValue}'");
                }
            }
        }

        // Validate response size
        if (validation.MaxResponseSizeBytes.HasValue)
        {
            var contentLength = response.Content.Headers.ContentLength;

            if (contentLength.HasValue && contentLength.Value > validation.MaxResponseSizeBytes.Value)
            {
                throw new HttpRequestException($"Response size {contentLength} bytes exceeds maximum {validation.MaxResponseSizeBytes} bytes");
            }
        }

        // Body validations require reading the content
        if (!string.IsNullOrEmpty(validation.BodyContains) || !string.IsNullOrEmpty(validation.BodyNotContains) || !string.IsNullOrEmpty(validation.JsonPathExpression))
        {
            responseBody = await response.Content.ReadAsStringAsync();

            // Validate body contains
            if (!string.IsNullOrEmpty(validation.BodyContains) && !responseBody.Contains(validation.BodyContains))
                throw new HttpRequestException($"Response body does not contain expected text: '{validation.BodyContains}'");

            // Validate body not contains
            if (!string.IsNullOrEmpty(validation.BodyNotContains) && responseBody.Contains(validation.BodyNotContains))
                throw new HttpRequestException($"Response body contains forbidden text: '{validation.BodyNotContains}'");

            // Validate JSONPath
            if (!string.IsNullOrEmpty(validation.JsonPathExpression))
                ValidateJsonPath(responseBody, validation.JsonPathExpression, validation.JsonPathExpectedValue, context);
        }

        context.LogInformation("Response validation passed");

        return responseBody;
    }

    private static void ValidateJsonPath(string json, string jsonPath, string expectedValue, IJobContext context)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var element = doc.RootElement;

            // Simple JSONPath implementation for common cases like $.data.id or $.items[0].name
            var pathParts = jsonPath.TrimStart('$', '.').Split('.');

            foreach (var part in pathParts)
            {
                if (string.IsNullOrEmpty(part))
                    continue;

                // Handle array indexing like items[0]
                var arrayMatch = ArrayIndexRegex().Match(part);

                if (arrayMatch.Success)
                {
                    var propName = arrayMatch.Groups[1].Value;
                    var index = int.Parse(arrayMatch.Groups[2].Value);

                    if (!string.IsNullOrEmpty(propName))
                    {
                        element = element.GetProperty(propName);
                    }

                    element = element[index];
                }
                else
                {
                    element = element.GetProperty(part);
                }
            }

            var actualValue = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "null",
                _ => element.GetRawText()
            };

            if (!string.IsNullOrEmpty(expectedValue) && actualValue != expectedValue)
            {
                throw new HttpRequestException(
                    $"JSONPath '{jsonPath}' returned '{actualValue}', expected '{expectedValue}'");
            }

            context.LogInformation($"JSONPath validation passed: {jsonPath} = {actualValue}");
        }
        catch (KeyNotFoundException)
        {
            throw new HttpRequestException($"JSONPath '{jsonPath}' not found in response");
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException($"Failed to parse JSON response: {ex.Message}");
        }
    }

    [GeneratedRegex(@"^(\w*)\[(\d+)\]$")]
    private static partial Regex ArrayIndexRegex();

    #endregion
}

/// <summary>
/// Generic HTTP request job data model.
/// Supports all HTTP methods, authentication types, and request configurations.
/// </summary>
public class HttpJobData
{
    /// <summary>
    /// Target URL for the HTTP request. Can include path parameters like {id}.
    /// </summary>
    [Required]
    [Description("Target URL for the HTTP request. Supports path parameters like {userId}")]
    public string Url { get; set; }

    /// <summary>
    /// HTTP method to use.
    /// </summary>
    [DefaultValue(HttpMethodType.GET)]
    [Description("HTTP method (GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS)")]
    public HttpMethodType Method { get; set; } = HttpMethodType.GET;

    /// <summary>
    /// Request headers as key-value pairs.
    /// </summary>
    [Description("Custom request headers as key-value pairs")]
    public Dictionary<string, string> Headers { get; set; }

    /// <summary>
    /// Query string parameters. Will be appended to URL.
    /// </summary>
    [Description("Query string parameters to append to URL")]
    public Dictionary<string, string> QueryParameters { get; set; }

    /// <summary>
    /// Path parameters to replace in URL placeholders like {id}.
    /// </summary>
    [Description("Path parameters to replace URL placeholders like {userId}")]
    public Dictionary<string, string> PathParameters { get; set; }

    /// <summary>
    /// Request body configuration.
    /// </summary>
    [Description("Request body configuration (content, type, encoding)")]
    public HttpRequestBody Body { get; set; }

    /// <summary>
    /// Authentication configuration.
    /// </summary>
    [Description("Authentication settings (Basic, Bearer, OAuth2, ApiKey)")]
    public HttpAuthentication Authentication { get; set; }

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    [DefaultValue(30)]
    [Description("Request timeout in seconds")]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to follow HTTP redirects.
    /// </summary>
    [DefaultValue(true)]
    [Description("Whether to automatically follow HTTP redirects")]
    public bool FollowRedirects { get; set; } = true;

    /// <summary>
    /// Maximum number of redirects to follow.
    /// </summary>
    [DefaultValue(5)]
    [Description("Maximum number of redirects to follow")]
    public int MaxRedirects { get; set; } = 5;

    /// <summary>
    /// Skip SSL/TLS certificate validation. Use with caution!
    /// </summary>
    [DefaultValue(false)]
    [Description("Skip SSL certificate validation (use with caution!)")]
    public bool IgnoreSslErrors { get; set; } = false;

    /// <summary>
    /// Retry configuration for failed requests.
    /// </summary>
    [Description("Retry policy for failed requests (max retries, backoff, etc.)")]
    public HttpRetryPolicy RetryPolicy { get; set; }

    /// <summary>
    /// Expected response validation rules.
    /// </summary>
    [Description("Response validation rules (status codes, body content, headers)")]
    public HttpResponseValidation Validation { get; set; }

    /// <summary>
    /// Proxy configuration for the request.
    /// </summary>
    [Description("HTTP proxy configuration")]
    public HttpProxyConfig Proxy { get; set; }

    /// <summary>
    /// Client certificate for mutual TLS authentication.
    /// </summary>
    [Description("Client certificate for mutual TLS (mTLS) authentication")]
    public HttpClientCertificate ClientCertificate { get; set; }

    /// <summary>
    /// Custom user agent string.
    /// </summary>
    [Description("Custom User-Agent header value")]
    public string UserAgent { get; set; }

    /// <summary>
    /// Accept-Encoding header values (gzip, deflate, br).
    /// </summary>
    [Description("Accept-Encoding values: gzip, deflate, br")]
    public string[] AcceptEncoding { get; set; }

    /// <summary>
    /// Cookies to send with the request.
    /// </summary>
    [Description("Cookies to send with the request as key-value pairs")]
    public Dictionary<string, string> Cookies { get; set; }
}

/// <summary>
/// HTTP methods supported.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HttpMethodType
{
    GET,
    POST,
    PUT,
    DELETE,
    PATCH,
    HEAD,
    OPTIONS
}

/// <summary>
/// Request body configuration.
/// </summary>
public class HttpRequestBody
{
    /// <summary>
    /// Content type of the body.
    /// </summary>
    [DefaultValue(HttpBodyType.Json)]
    [Description("Body content type (Json, Xml, Text, FormUrlEncoded, Multipart, Binary)")]
    public HttpBodyType Type { get; set; } = HttpBodyType.Json;

    /// <summary>
    /// Raw content for JSON, XML, Text, or GraphQL body types.
    /// </summary>
    [Description("Raw body content - JSON object, XML string, or plain text")]
    public object Content { get; set; }

    /// <summary>
    /// Form data for FormUrlEncoded or Multipart body types.
    /// </summary>
    [Description("Form fields as key-value pairs for form submissions")]
    public Dictionary<string, string> FormData { get; set; }

    /// <summary>
    /// File uploads for Multipart body type.
    /// </summary>
    [Description("Files to upload in multipart requests")]
    public List<HttpFileUpload> Files { get; set; }

    /// <summary>
    /// Content encoding.
    /// </summary>
    [DefaultValue("utf-8")]
    [Description("Character encoding for the body content")]
    public string Encoding { get; set; } = "utf-8";

    /// <summary>
    /// Custom content type header override.
    /// </summary>
    [Description("Override the default Content-Type header")]
    public string ContentTypeOverride { get; set; }
}

/// <summary>
/// Body content types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HttpBodyType
{
    /// <summary>
    /// application/json
    /// </summary>
    Json,

    /// <summary>
    /// application/xml or text/xml
    /// </summary>
    Xml,

    /// <summary>
    /// text/plain
    /// </summary>
    Text,

    /// <summary>
    /// application/x-www-form-urlencoded
    /// </summary>
    FormUrlEncoded,

    /// <summary>
    /// multipart/form-data
    /// </summary>
    Multipart,

    /// <summary>
    /// application/octet-stream (binary)
    /// </summary>
    Binary,

    /// <summary>
    /// application/graphql or application/json with GraphQL query
    /// </summary>
    GraphQL,

    /// <summary>
    /// text/html
    /// </summary>
    Html,

    /// <summary>
    /// No body
    /// </summary>
    None
}

/// <summary>
/// File upload configuration for multipart requests.
/// </summary>
public class HttpFileUpload
{
    /// <summary>
    /// Form field name for the file.
    /// </summary>
    [Required]
    [Description("Form field name for the file upload")]
    public string FieldName { get; set; }

    /// <summary>
    /// File name to send.
    /// </summary>
    [Required]
    [Description("File name to include in the upload")]
    public string FileName { get; set; }

    /// <summary>
    /// Base64 encoded file content.
    /// </summary>
    [Description("File content as Base64 encoded string")]
    public string ContentBase64 { get; set; }

    /// <summary>
    /// URL to download file content from.
    /// </summary>
    [Description("URL to download file content from (alternative to ContentBase64)")]
    public string ContentUrl { get; set; }

    /// <summary>
    /// MIME type of the file.
    /// </summary>
    [DefaultValue("application/octet-stream")]
    [Description("MIME type of the file (e.g., image/png, application/pdf)")]
    public string ContentType { get; set; } = "application/octet-stream";
}

/// <summary>
/// Authentication configuration.
/// </summary>
public class HttpAuthentication
{
    /// <summary>
    /// Authentication type.
    /// </summary>
    [DefaultValue(HttpAuthType.None)]
    [Description("Authentication type (None, Basic, Bearer, ApiKey, OAuth2)")]
    public HttpAuthType Type { get; set; }

    /// <summary>
    /// For Basic auth: username. For Bearer/ApiKey: the token/key value.
    /// </summary>
    [Description("Username for Basic auth, or token/key for Bearer/ApiKey")]
    public string Credential { get; set; }

    /// <summary>
    /// For Basic auth: password.
    /// </summary>
    [Description("Password for Basic auth")]
    public string Secret { get; set; }

    /// <summary>
    /// For ApiKey: header name or query parameter name.
    /// </summary>
    [DefaultValue("X-API-Key")]
    [Description("Header or query parameter name for API key")]
    public string KeyName { get; set; }

    /// <summary>
    /// For ApiKey: where to send the key (Header or Query).
    /// </summary>
    [DefaultValue(ApiKeyLocation.Header)]
    [Description("Where to send the API key (Header, Query, Cookie)")]
    public ApiKeyLocation KeyLocation { get; set; } = ApiKeyLocation.Header;

    /// <summary>
    /// For OAuth2: token endpoint URL.
    /// </summary>
    [Description("OAuth2 token endpoint URL")]
    public string TokenUrl { get; set; }

    /// <summary>
    /// For OAuth2: client ID.
    /// </summary>
    [Description("OAuth2 client ID")]
    public string ClientId { get; set; }

    /// <summary>
    /// For OAuth2: client secret.
    /// </summary>
    [Description("OAuth2 client secret")]
    public string ClientSecret { get; set; }

    /// <summary>
    /// For OAuth2: requested scopes.
    /// </summary>
    [Description("OAuth2 scopes to request (space-separated in token request)")]
    public string[] Scopes { get; set; }

    /// <summary>
    /// For OAuth2: grant type.
    /// </summary>
    [DefaultValue(OAuth2GrantType.ClientCredentials)]
    [Description("OAuth2 grant type")]
    public OAuth2GrantType GrantType { get; set; } = OAuth2GrantType.ClientCredentials;

    /// <summary>
    /// Custom Authorization header prefix.
    /// </summary>
    [DefaultValue("Bearer")]
    [Description("Custom Authorization header prefix (default: Bearer)")]
    public string AuthorizationPrefix { get; set; }
}

/// <summary>
/// Authentication types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HttpAuthType
{
    /// <summary>
    /// No authentication.
    /// </summary>
    None,

    /// <summary>
    /// HTTP Basic authentication (username:password base64 encoded).
    /// </summary>
    Basic,

    /// <summary>
    /// Bearer token authentication.
    /// </summary>
    Bearer,

    /// <summary>
    /// API key authentication.
    /// </summary>
    ApiKey,

    /// <summary>
    /// OAuth 2.0 authentication (will fetch token automatically).
    /// </summary>
    OAuth2,

    /// <summary>
    /// Digest authentication.
    /// </summary>
    Digest,

    /// <summary>
    /// NTLM/Windows authentication.
    /// </summary>
    Ntlm
}

/// <summary>
/// Where to send the API key.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiKeyLocation
{
    Header,
    Query,
    Cookie
}

/// <summary>
/// OAuth2 grant types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OAuth2GrantType
{
    ClientCredentials,
    Password,
    AuthorizationCode,
    RefreshToken
}

/// <summary>
/// Retry policy configuration.
/// </summary>
public class HttpRetryPolicy
{
    /// <summary>
    /// Maximum number of retry attempts.
    /// </summary>
    [DefaultValue(3)]
    [Description("Maximum number of retry attempts")]
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Initial delay between retries in milliseconds.
    /// </summary>
    [DefaultValue(1000)]
    [Description("Initial delay between retries in milliseconds")]
    public int InitialDelayMs { get; set; } = 1000;

    /// <summary>
    /// Maximum delay between retries in milliseconds.
    /// </summary>
    [DefaultValue(30000)]
    [Description("Maximum delay between retries in milliseconds")]
    public int MaxDelayMs { get; set; } = 30000;

    /// <summary>
    /// Backoff multiplier for exponential backoff.
    /// </summary>
    [DefaultValue(2.0)]
    [Description("Multiplier for exponential backoff (e.g., 2.0 = delay doubles each retry)")]
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// HTTP status codes that should trigger a retry.
    /// </summary>
    [Description("HTTP status codes that trigger retry (default: 408, 429, 500, 502, 503, 504)")]
    public int[] RetryOnStatusCodes { get; set; }

    /// <summary>
    /// Whether to retry on timeout.
    /// </summary>
    [DefaultValue(true)]
    [Description("Retry when request times out")]
    public bool RetryOnTimeout { get; set; } = true;

    /// <summary>
    /// Whether to retry on connection errors.
    /// </summary>
    [DefaultValue(true)]
    [Description("Retry on connection errors (network failures)")]
    public bool RetryOnConnectionError { get; set; } = true;
}

/// <summary>
/// Response validation rules.
/// </summary>
public class HttpResponseValidation
{
    /// <summary>
    /// Expected HTTP status codes. Request fails if response doesn't match.
    /// </summary>
    [Description("Expected status codes - fails if response doesn't match (e.g., 200, 201, 204)")]
    public int[] ExpectedStatusCodes { get; set; }

    /// <summary>
    /// Response body must contain this string.
    /// </summary>
    [Description("Fail if response body doesn't contain this text")]
    public string BodyContains { get; set; }

    /// <summary>
    /// Response body must not contain this string.
    /// </summary>
    [Description("Fail if response body contains this forbidden text")]
    public string BodyNotContains { get; set; }

    /// <summary>
    /// JSONPath expression to validate response body.
    /// </summary>
    [Description("JSONPath expression to extract value from response (e.g., $.data.id)")]
    public string JsonPathExpression { get; set; }

    /// <summary>
    /// Expected value for JSONPath expression.
    /// </summary>
    [Description("Expected value at JSONPath location")]
    public string JsonPathExpectedValue { get; set; }

    /// <summary>
    /// Required response headers.
    /// </summary>
    [Description("Headers that must be present in response")]
    public Dictionary<string, string> RequiredHeaders { get; set; }

    /// <summary>
    /// Maximum response time in milliseconds. Fails if exceeded.
    /// </summary>
    [Description("Maximum allowed response time in milliseconds")]
    public int? MaxResponseTimeMs { get; set; }

    /// <summary>
    /// Maximum response size in bytes. Fails if exceeded.
    /// </summary>
    [Description("Maximum allowed response size in bytes")]
    public long? MaxResponseSizeBytes { get; set; }
}

/// <summary>
/// Proxy configuration.
/// </summary>
public class HttpProxyConfig
{
    /// <summary>
    /// Proxy server URL.
    /// </summary>
    [Required]
    [Description("Proxy server URL (e.g., http://proxy.company.com:8080)")]
    public string Url { get; set; }

    /// <summary>
    /// Proxy username for authentication.
    /// </summary>
    [Description("Username for proxy authentication")]
    public string Username { get; set; }

    /// <summary>
    /// Proxy password for authentication.
    /// </summary>
    [Description("Password for proxy authentication")]
    public string Password { get; set; }

    /// <summary>
    /// Bypass proxy for these hosts.
    /// </summary>
    [Description("Hosts that should bypass the proxy (e.g., localhost, *.internal.com)")]
    public string[] BypassList { get; set; }
}

/// <summary>
/// Client certificate configuration for mutual TLS.
/// </summary>
public class HttpClientCertificate
{
    /// <summary>
    /// Base64 encoded PFX/PKCS12 certificate.
    /// </summary>
    [Description("Base64 encoded PFX/PKCS12 certificate content")]
    public string CertificateBase64 { get; set; }

    /// <summary>
    /// Certificate password.
    /// </summary>
    [Description("Password for the PFX certificate")]
    public string Password { get; set; }

    /// <summary>
    /// Certificate thumbprint (for Windows certificate store).
    /// </summary>
    [Description("Certificate thumbprint for Windows certificate store lookup")]
    public string Thumbprint { get; set; }

    /// <summary>
    /// Certificate store location.
    /// </summary>
    [DefaultValue("CurrentUser")]
    [Description("Certificate store location (CurrentUser or LocalMachine)")]
    public string StoreLocation { get; set; }
}