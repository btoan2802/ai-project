using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using shop_senior_mvc.Data;
using shop_senior_mvc.Models;

namespace shop_senior_mvc.Controllers
{
    public class AdminController : Controller
    {
        private readonly AppDbContext _context;

        public AdminController(AppDbContext context)
        {
            _context = context;
        }

        private bool IsAdmin()
        {
            return HttpContext.Session.GetString("UserRole") == "Admin";
        }

        // GET: /Admin
        public IActionResult Index()
        {
            if (!IsAdmin())
            {
                return RedirectToAction("Login", "Account");
            }

            ViewBag.TotalProducts = _context.Products.Count();
            ViewBag.TotalOrders = _context.Orders.Count();
            ViewBag.TotalRevenue = _context.Orders.Sum(o => o.TotalAmount);
            ViewBag.TotalUsers = _context.Users.Count();

            var recentOrders = _context.Orders.OrderByDescending(o => o.OrderDate).Take(5).ToList();
            return View(recentOrders);
        }

        // GET: /Admin/Products
        public IActionResult Products()
        {
            if (!IsAdmin()) return RedirectToAction("Login", "Account");

            var products = _context.Products.Include(p => p.Category).OrderByDescending(p => p.Id).ToList();
            return View(products);
        }

        // GET: /Admin/CreateProduct
        public IActionResult CreateProduct()
        {
            if (!IsAdmin()) return RedirectToAction("Login", "Account");

            ViewBag.Categories = _context.Categories.ToList();
            return View();
        }

        // POST: /Admin/CreateProduct
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CreateProduct(Product product)
        {
            if (!IsAdmin()) return RedirectToAction("Login", "Account");

            if (!ModelState.IsValid)
            {
                ViewBag.Categories = _context.Categories.ToList();
                return View(product);
            }

            if (string.IsNullOrWhiteSpace(product.ImageUrl))
            {
                product.ImageUrl = "https://images.unsplash.com/photo-1542291026-7eec264c27ff?w=600";
            }

            _context.Products.Add(product);
            _context.SaveChanges();
            return RedirectToAction("Products");
        }

        // GET: /Admin/EditProduct/{id}
        public IActionResult EditProduct(int id)
        {
            if (!IsAdmin()) return RedirectToAction("Login", "Account");

            var product = _context.Products.Find(id);
            if (product == null) return NotFound();

            ViewBag.Categories = _context.Categories.ToList();
            return View(product);
        }

        // POST: /Admin/EditProduct/{id}
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult EditProduct(Product product)
        {
            if (!IsAdmin()) return RedirectToAction("Login", "Account");

            if (!ModelState.IsValid)
            {
                ViewBag.Categories = _context.Categories.ToList();
                return View(product);
            }

            _context.Products.Update(product);
            _context.SaveChanges();
            return RedirectToAction("Products");
        }

        // POST: /Admin/DeleteProduct/{id}
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteProduct(int id)
        {
            if (!IsAdmin()) return RedirectToAction("Login", "Account");

            var product = _context.Products.Find(id);
            if (product != null)
            {
                _context.Products.Remove(product);
                _context.SaveChanges();
            }
            return RedirectToAction("Products");
        }

        // GET: /Admin/Orders
        public IActionResult Orders()
        {
            if (!IsAdmin()) return RedirectToAction("Login", "Account");

            var orders = _context.Orders.Include(o => o.OrderItems).OrderByDescending(o => o.OrderDate).ToList();
            return View(orders);
        }

        // POST: /Admin/UpdateOrderStatus
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult UpdateOrderStatus(int orderId, string status)
        {
            if (!IsAdmin()) return RedirectToAction("Login", "Account");

            var order = _context.Orders.Find(orderId);
            if (order != null && !string.IsNullOrWhiteSpace(status))
            {
                order.Status = status;
                _context.SaveChanges();
            }
            return RedirectToAction("Orders");
        }
    }
}
