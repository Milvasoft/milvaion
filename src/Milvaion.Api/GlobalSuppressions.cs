// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "<Pending>", Scope = "member", Target = "~M:Milvaion.Api.AppStartup.StartupExtensions.AddOpenApi(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Reflection.Assembly[])~Microsoft.Extensions.DependencyInjection.IServiceCollection")]
[assembly: SuppressMessage("Minor Code Smell", "S2094:Classes should not be empty", Justification = "<Pending>", Scope = "namespace", Target = "~N:Milvaion.Api")]
[assembly: SuppressMessage("Major Code Smell", "S3358:Ternary operators should not be nested", Justification = "<Pending>", Scope = "member", Target = "~M:Milvaion.Api.Middlewares.ExceptionMiddleware.Invoke(Microsoft.AspNetCore.Http.HttpContext)~System.Threading.Tasks.Task")]
[assembly: SuppressMessage("Critical Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "<Pending>", Scope = "member", Target = "~M:Milvaion.Api.Middlewares.ExceptionMiddleware.Invoke(Microsoft.AspNetCore.Http.HttpContext)~System.Threading.Tasks.Task")]
[assembly: SuppressMessage("AsyncUsage", "AsyncFixer01:Unnecessary async/await usage", Justification = "<Pending>", Scope = "member", Target = "~M:Milvaion.Api.Controllers.DeveloperController.ResetUIRelatedDataAsync~System.Threading.Tasks.Task{Milvasoft.Components.Rest.MilvaResponse.Response}")]
