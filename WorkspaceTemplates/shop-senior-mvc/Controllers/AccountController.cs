using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using shop_senior_mvc.Data;
using shop_senior_mvc.Helpers;
using shop_senior_mvc.Models;

namespace shop_senior_mvc.Controllers
{
    public class AccountController : Controller
    {
        private readonly AppDbContext _context;

        public AccountController(AppDbContext context)
        {
            _context = context;
        }

        // GET: /Account/Login
        public IActionResult Login()
        {
            if (HttpContext.Session.IsUserLoggedIn())
            {
                return RedirectToAction("Index", "Home");
            }
            return View();
        }

        // POST: /Account/Login
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Login(string email, string password)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                ViewBag.Error = "Vui lòng nhập đầy đủ Email và Mật khẩu.";
                return View();
            }

            var user = _context.Users.FirstOrDefault(u => u.Email.ToLower() == email.Trim().ToLower() && u.Password == password);
            if (user == null)
            {
                ViewBag.Error = "Email hoặc Mật khẩu không chính xác.";
                return View();
            }

            HttpContext.Session.SetUserLoggedIn(user.Id.ToString(), user.Username);
            HttpContext.Session.SetString("UserEmail", user.Email);
            HttpContext.Session.SetString("UserRole", user.Role ?? "Customer");

            if (user.Role == "Admin")
            {
                return RedirectToAction("Index", "Admin");
            }
            return RedirectToAction("Index", "Home");
        }

        // GET: /Account/Register
        public IActionResult Register()
        {
            if (HttpContext.Session.IsUserLoggedIn())
            {
                return RedirectToAction("Index", "Home");
            }
            return View();
        }

        // POST: /Account/Register
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Register(User model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            if (_context.Users.Any(u => u.Email.ToLower() == model.Email.Trim().ToLower()))
            {
                ViewBag.Error = "Email này đã được sử dụng. Vui lòng chọn email khác.";
                return View(model);
            }

            model.Role = "Customer";
            model.CreatedAt = System.DateTime.UtcNow;
            _context.Users.Add(model);
            _context.SaveChanges();

            HttpContext.Session.SetUserLoggedIn(model.Id.ToString(), model.Username);
            HttpContext.Session.SetString("UserEmail", model.Email);
            HttpContext.Session.SetString("UserRole", model.Role);

            return RedirectToAction("Index", "Home");
        }

        // POST / GET: /Account/Logout
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Index", "Home");
        }

        // GET: /Account/Profile
        public IActionResult Profile()
        {
            if (!HttpContext.Session.IsUserLoggedIn())
            {
                return RedirectToAction("Login");
            }

            string userEmail = HttpContext.Session.GetString("UserEmail") ?? "";
            var user = _context.Users.FirstOrDefault(u => u.Email == userEmail);
            var orders = _context.Orders.Where(o => o.CustomerEmail == userEmail).OrderByDescending(o => o.OrderDate).ToList();

            ViewBag.Orders = orders;
            return View(user);
        }
    }
}
