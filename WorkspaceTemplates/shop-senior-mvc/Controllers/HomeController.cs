using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using shop_senior_mvc.Data;
using shop_senior_mvc.Models.ViewModels;

namespace shop_senior_mvc.Controllers
{
    public class HomeController : Controller
    {
        private readonly AppDbContext _context;

        public HomeController(AppDbContext context)
        {
            _context = context;
        }

        public IActionResult Index()
        {
            var vm = new HomeViewModel
            {
                FeaturedProducts = _context.Products.Include(p => p.Category).Take(6).ToList(),
                Categories = _context.Categories.ToList()
            };
            return View(vm);
        }
    }
}
