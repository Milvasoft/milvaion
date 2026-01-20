namespace Milvaion.IntegrationTests.TestBase;

[CollectionDefinition(nameof(MilvaionTestCollection))]
public class MilvaionTestCollection : ICollectionFixture<CustomWebApplicationFactory>;