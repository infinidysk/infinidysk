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
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.UsenetMigration;

[Collection(nameof(ConfigPathCollection))]
public sealed class UsenetMigrationControllerAuthTests
{
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
