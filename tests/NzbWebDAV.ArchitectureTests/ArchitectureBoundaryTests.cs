using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using NzbWebDAV.Config;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace NzbWebDAV.ArchitectureTests;

public class ArchitectureBoundaryTests
{
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(typeof(ConfigManager).Assembly)
        .Build();

    private static readonly IObjectProvider<IType> ApiControllers = Types().That()
        .ResideInNamespaceMatching(@"^NzbWebDAV\.Api\.Controllers(\.|$)")
        .Or()
        .ResideInNamespaceMatching(@"^NzbWebDAV\.Api\.SabControllers(\.|$)")
        .As("API controllers");

    private static readonly IObjectProvider<IType> NonApiCode = Types().That()
        .DoNotResideInNamespaceMatching(@"^NzbWebDAV\.Api(\.|$)")
        .And()
        .DoNotHaveName("Program")
        .And()
        .DoNotHaveFullNameContaining("<")
        .As("non-API application code");

    private static readonly IObjectProvider<IType> Clients = Types().That()
        .ResideInNamespaceMatching(@"^NzbWebDAV\.Clients(\.|$)")
        .As("Clients");

    private static readonly IObjectProvider<IType> ClientForbidden = Types().That()
        .ResideInNamespaceMatching(@"^NzbWebDAV\.Api(\.|$)")
        .Or()
        .ResideInNamespaceMatching(@"^NzbWebDAV\.Queue(\.|$)")
        .Or()
        .ResideInNamespaceMatching(@"^NzbWebDAV\.Tasks(\.|$)")
        .Or()
        .ResideInNamespaceMatching(@"^NzbWebDAV\.WebDav(\.|$)")
        .Or()
        .ResideInNamespaceMatching(@"^NzbWebDAV\.UsenetMigration(\.|$)")
        .As("API, Queue, Tasks, WebDAV, or migration orchestration");

    private static readonly IObjectProvider<IType> Database = Types().That()
        .ResideInNamespaceMatching(@"^NzbWebDAV\.Database(\.|$)")
        .And()
        .DoNotResideInNamespaceMatching(
            @"^NzbWebDAV\.Database\.(Migrations|PostgresMigrations|MetricsMigrations|UsenetMigrations)(\.|$)")
        .And()
        .DoNotHaveFullNameContaining("<")
        .As("Database (excluding EF migrations)");

    private static readonly IObjectProvider<IType> DatabaseForbidden = Types().That()
        .ResideInNamespaceMatching(@"^NzbWebDAV\.Api(\.|$)")
        .Or()
        .ResideInNamespaceMatching(@"^NzbWebDAV\.Queue(\.|$)")
        .Or()
        .ResideInNamespaceMatching(@"^NzbWebDAV\.Tasks(\.|$)")
        .As("API, Queue, or Tasks");

    [Fact]
    public void NonApiCodeDoesNotDependOnControllers()
    {
        Types().That().Are(NonApiCode)
            .Should()
            .NotDependOnAny(ApiControllers)
            .Because(
                "Queue, WebDAV, clients, and other non-API types must not reference " +
                "NzbWebDAV.Api.Controllers or NzbWebDAV.Api.SabControllers. Extract a " +
                "transport-neutral helper or service and keep controllers as thin adapters. " +
                "Program is the composition root and may depend on API types.")
            .Check(Architecture);
    }

    [Fact]
    public void ClientsDoNotDependOnApiQueueTasksWebDavOrMigration()
    {
        Types().That().Are(Clients)
            .Should()
            .NotDependOnAny(ClientForbidden)
            .Because(
                "Clients may use configuration, database models, utilities, and metrics/" +
                "observability contracts, but not API, Queue, Tasks, WebDAV, or migration types.")
            .Check(Architecture);
    }

    [Fact]
    public void DatabaseDoesNotDependOnApiQueueOrTasks()
    {
        Types().That().Are(Database)
            .Should()
            .NotDependOnAny(DatabaseForbidden)
            .Because(
                "Database code must not depend on API, Queue, or Tasks. EF migrations are excluded.")
            .Check(Architecture);
    }
}
