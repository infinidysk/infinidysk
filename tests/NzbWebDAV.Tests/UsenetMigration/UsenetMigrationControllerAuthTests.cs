using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.UsenetMigration;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.UsenetMigration;
using NzbWebDAV.UsenetMigration.Runner;
using NzbWebDAV.UsenetMigration.Source;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.UsenetMigration;

[Collection(nameof(ConfigPathCollection))]
public sealed class UsenetMigrationControllerAuthTests
{
    [Fact]
    public async Task DetectPaths_RequiresBodyButAllowsExplicitDefaultRoot()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        using var queueManager = CreateQueueManager();
        var runner = new UsenetMigrationRunner(
            h.Store, queueManager, new ConfigManager(), new WebsocketManager());

        const string apiKey = "migration-detect-contract-key";
        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", apiKey);
        try
        {
            var config = new ConfigManager();
            using var services = new ServiceCollection()
                .AddSingleton(config)
                .AddSingleton(h.Store)
                .BuildServiceProvider();
            var httpContext = new DefaultHttpContext { RequestServices = services };
            httpContext.Request.Headers["x-api-key"] = apiKey;
            var controller = new UsenetMigrationController(h.Store, runner)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext },
            };

            var missingBody = await controller.DetectPaths(null!);

            var badRequest = Assert.IsType<BadRequestObjectResult>(missingBody);
            var error = Assert.IsType<BaseApiResponse>(badRequest.Value);
            Assert.Equal("Request body is required.", error.Error);

            var explicitDefault = await controller.DetectPaths(new DetectPathsRequest(null));

            var ok = Assert.IsType<OkObjectResult>(explicitDefault);
            var json = JsonSerializer.Serialize(
                ok.Value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var document = JsonDocument.Parse(json);
            Assert.Equal(
                new[] { "status", "detected", "root", "metadataRoot", "configPath", "storeRoot", "reason" },
                document.RootElement.EnumerateObject().Select(property => property.Name));
            var expectedRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(AltmountPathDetector.DefaultRoot));
            Assert.Equal(expectedRoot, document.RootElement.GetProperty("root").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
        }
    }

    [Fact]
    public async Task Connect_SeedsCategoriesFromDumbStyleConfig()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var root = Directory.CreateTempSubdirectory("altmig-connect-");
        try
        {
            var metadataRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "metadata"));
            var configPath = Path.Combine(root.FullName, "config.yaml");
            await File.WriteAllTextAsync(configPath, string.Join('\n',
            [
                "sabnzbd:",
                "  complete_dir: '/'",
                "  categories:",
                "  - name: 'movies'",
                "    dir: 'movies'",
                "    type: 'radarr'",
                "  - name: 'tv'",
                "    dir: 'tv'",
                "    type: 'sonarr'",
            ]));

            await WithAuthorizedControllerAsync(h, async controller =>
            {
                var result = await controller.Connect(new ConnectRequest(
                    metadataRoot.FullName, configPath, root.FullName, 20, 1));

                Assert.IsType<OkObjectResult>(result);
            });

            var categories = await h.Store.GetCategoryMapAsync();
            Assert.Equal(new[] { "movies", "tv" },
                categories.Select(category => category.AltmountCategory));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Connect_RejectsSuppliedConfigWithoutCategories()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var root = Directory.CreateTempSubdirectory("altmig-connect-");
        try
        {
            var metadataRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "metadata"));
            var configPath = Path.Combine(root.FullName, "config.yaml");
            await File.WriteAllTextAsync(configPath, "sabnzbd:\n  categories:\n");

            await WithAuthorizedControllerAsync(h, async controller =>
            {
                var result = await controller.Connect(new ConnectRequest(
                    metadataRoot.FullName, configPath, root.FullName, 20, 1));

                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                var response = Assert.IsType<BaseApiResponse>(badRequest.Value);
                Assert.Equal(AltmountPathDetector.NoCategoriesReason, response.Error);
            });

            var session = await h.Store.GetSessionAsync();
            Assert.Equal("idle", session.Status);
            Assert.Empty(await h.Store.GetCategoryMapAsync());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Connect_RejectsUnsupportedConfigWithoutEchoingPath()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var root = Directory.CreateTempSubdirectory("altmig-connect-");
        try
        {
            var metadataRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "metadata"));
            var configPath = Path.Combine(root.FullName, "config.yaml");
            await File.WriteAllTextAsync(
                configPath,
                "sabnzbd:\n  categories: [{ name: 'movies' }]\n");

            await WithAuthorizedControllerAsync(h, async controller =>
            {
                var result = await controller.Connect(new ConnectRequest(
                    metadataRoot.FullName, configPath, root.FullName, 20, 1));

                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                var response = Assert.IsType<BaseApiResponse>(badRequest.Value);
                Assert.Equal(AltmountPathDetector.InvalidConfigReason, response.Error);
                Assert.DoesNotContain(root.FullName, response.Error);
            });
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Connect_AllowsAdvancedConnectionWithoutConfigPath()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var root = Directory.CreateTempSubdirectory("altmig-connect-");
        try
        {
            var metadataRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "metadata"));

            await WithAuthorizedControllerAsync(h, async controller =>
            {
                var result = await controller.Connect(new ConnectRequest(
                    metadataRoot.FullName, null, root.FullName, 20, 1));

                Assert.IsType<OkObjectResult>(result);
            });

            var session = await h.Store.GetSessionAsync();
            Assert.Equal("connected", session.Status);
            Assert.Null(session.AltmountConfigPath);
            Assert.Empty(await h.Store.GetCategoryMapAsync());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EveryHttpAction_RejectsMissingApiKeyWith401()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        using var queueManager = CreateQueueManager();
        var runner = new UsenetMigrationRunner(
            h.Store, queueManager, new ConfigManager(), new WebsocketManager());

        const string apiKey = "migration-auth-pin-key";
        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", apiKey);
        try
        {
            var config = new ConfigManager();
            using var services = new ServiceCollection()
                .AddSingleton(config)
                .AddSingleton(h.Store)
                .BuildServiceProvider();

            var controller = new UsenetMigrationController(h.Store, runner)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestServices = services },
                },
            };

            var actions = typeof(UsenetMigrationController)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes()
                    .Any(a => a.GetType().Name.StartsWith("Http", StringComparison.Ordinal)
                              && a.GetType().Name.EndsWith("Attribute", StringComparison.Ordinal)))
                .ToList();

            Assert.NotEmpty(actions);

            foreach (var method in actions)
            {
                var args = method.GetParameters()
                    .Select(CreateDefaultArgument)
                    .ToArray();

                var result = method.Invoke(controller, args);
                Assert.NotNull(result);

                IActionResult actionResult;
                if (result is Task<IActionResult> task)
                    actionResult = await task;
                else if (result is Task taskObj)
                {
                    await taskObj;
                    var resultProperty = taskObj.GetType().GetProperty("Result");
                    actionResult = Assert.IsAssignableFrom<IActionResult>(resultProperty!.GetValue(taskObj));
                }
                else
                    actionResult = Assert.IsAssignableFrom<IActionResult>(result);

                var unauthorized = Assert.IsType<UnauthorizedObjectResult>(actionResult);
                var body = Assert.IsType<BaseApiResponse>(unauthorized.Value);
                Assert.False(body.Status);
                Assert.False(string.IsNullOrWhiteSpace(body.Error),
                    $"{method.Name} returned an empty unauthorized error.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
        }
    }

    [Fact]
    public void EveryHttpAction_DelegatesThroughGuardedAsync()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "backend",
            "Api",
            "Controllers",
            "UsenetMigration",
            "UsenetMigrationController.cs"));

        var actions = typeof(UsenetMigrationController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes()
                .Any(a => a.GetType().Name.StartsWith("Http", StringComparison.Ordinal)
                          && a.GetType().Name.EndsWith("Attribute", StringComparison.Ordinal)))
            .Select(m => m.Name)
            .Distinct()
            .ToList();

        Assert.NotEmpty(actions);
        foreach (var name in actions)
        {
            var index = source.IndexOf($" {name}(", StringComparison.Ordinal);
            Assert.True(index >= 0, $"Could not find action {name} in controller source.");
            var window = source.Substring(index, Math.Min(400, source.Length - index));
            Assert.Contains("GuardedAsync", window);
        }
    }

    private static object? CreateDefaultArgument(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue)
            return parameter.DefaultValue;

        var type = parameter.ParameterType;
        if (!type.IsValueType)
            return null;
        return Activator.CreateInstance(type);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "NzbWebDAV.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    private static async Task WithAuthorizedControllerAsync(
        MigrationTestHarness harness,
        Func<UsenetMigrationController, Task> test)
    {
        const string apiKey = "migration-connect-contract-key";
        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", apiKey);
        try
        {
            using var queueManager = CreateQueueManager();
            var config = new ConfigManager();
            var runner = new UsenetMigrationRunner(
                harness.Store, queueManager, config, new WebsocketManager());
            using var services = new ServiceCollection()
                .AddSingleton(config)
                .AddSingleton(harness.Store)
                .BuildServiceProvider();
            var httpContext = new DefaultHttpContext { RequestServices = services };
            httpContext.Request.Headers["x-api-key"] = apiKey;
            var controller = new UsenetMigrationController(harness.Store, runner)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext },
            };

            await test(controller);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
        }
    }

    private static QueueManager CreateQueueManager()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig()),
            },
        ]);
        var websocket = new WebsocketManager();
        var usenet = new UsenetStreamingClient(
            config,
            websocket,
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());
        return new QueueManager(
            usenet,
            config,
            websocket,
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate(),
            startLoop: false);
    }
}
