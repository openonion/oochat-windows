namespace ConnectOnion.IntegrationTests.Database;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabaseCollection : ICollectionFixture<TempDatabaseFixture>
{
    public const string Name = "isolated database";
}
