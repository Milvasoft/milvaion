namespace Milvaion.IntegrationTests.TestBase;

/// <summary>
/// Host with projects enabled (<c>MilvaionConfig:Projects:Enabled=true</c>), for the cross-project isolation tests.
/// Every other collection runs with projects disabled, the default.
/// </summary>
public class ProjectsWebApplicationFactory : CustomWebApplicationFactory
{
    private const string _enabledVariable = "MilvaionConfig__Projects__Enabled";
    private const string _cacheVariable = "MilvaionConfig__Projects__CacheSeconds";

    protected override string DatabaseName => "testDb_projects";

    protected override void ConfigureAdditionalEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable(_enabledVariable, "true");

        // Memberships are seeded per test; a long cache would carry them into the next test.
        Environment.SetEnvironmentVariable(_cacheVariable, "1");
    }

    protected override void ClearAdditionalEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable(_enabledVariable, null);
        Environment.SetEnvironmentVariable(_cacheVariable, null);
    }
}

[CollectionDefinition(nameof(ProjectsTestCollection))]
public class ProjectsTestCollection : ICollectionFixture<ProjectsWebApplicationFactory>;
