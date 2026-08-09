using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.SabControllers.GetCategories;

public class GetCategoriesController(
    HttpContext httpContext,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override Task<IActionResult> Handle()
    {
        var categories = BuildCategories(Config);
        var response = new { categories };
        return Task.FromResult<IActionResult>(Ok(response));
    }

    internal static List<string> BuildCategories(ConfigManager Config)
    {
        return Config.GetApiCategories()
            .Where(category => category != "*")
            .Prepend("*")
            .ToList();
    }
}
