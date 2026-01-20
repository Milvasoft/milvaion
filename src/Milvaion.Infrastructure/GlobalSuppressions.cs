// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("AsyncUsage", "AsyncFixer01:Unnecessary async/await usage", Justification = "<Pending>", Scope = "member", Target = "~M:Milvaion.Infrastructure.Services.DeveloperService.ResetDatabaseAsync~System.Threading.Tasks.Task{Milvasoft.Components.Rest.MilvaResponse.Response}")]
[assembly: SuppressMessage("AsyncUsage", "AsyncFixer01:Unnecessary async/await usage", Justification = "<Pending>", Scope = "member", Target = "~M:Milvaion.Infrastructure.Services.DeveloperService.InitDatabaseAsync~System.Threading.Tasks.Task{Milvasoft.Components.Rest.MilvaResponse.Response{System.String}}")]
[assembly: SuppressMessage("Performance", "CA1869:Cache and reuse 'JsonSerializerOptions' instances", Justification = "<Pending>", Scope = "member", Target = "~M:Milvaion.Infrastructure.Persistence.DatabaseMigrator.SeedUIRelatedDataAsync(System.Threading.CancellationToken)~System.Threading.Tasks.Task{Milvasoft.Components.Rest.MilvaResponse.Response}")]
[assembly: SuppressMessage("AsyncUsage", "AsyncFixer01:Unnecessary async/await usage", Justification = "<Pending>", Scope = "member", Target = "~M:Milvaion.Infrastructure.Services.Redis.JobCancellationService.PublishCancellationAsync(System.Guid,System.Guid,System.Guid,System.String,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Int64}")]
