using Microsoft.AspNetCore.Mvc;

namespace EmbeddedSass.Net.Sample.AspNetCore.Controllers;

public sealed class HomeController : Controller
{
    public IActionResult Index() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View();
}
