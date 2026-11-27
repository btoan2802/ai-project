using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using BtoanAiNet.Models;

namespace BtoanAiNet.Controllers;

public class HomeController : Controller
{
    [HttpGet]
    [Route("")]
    [Route("Home")]
    [Route("Home/Index")]
    [Route("c/{id}")]
    public IActionResult Index(string? id = null)
    {
        ViewBag.ConversationId = id;
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
