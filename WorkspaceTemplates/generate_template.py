import os

base_dir = r"D:\ai.btoan.com\BtoanAiNet\App_Data\WorkspaceTemplates\shop-senior-mvc"
os.makedirs(base_dir, exist_ok=True)

files = {
    "shop-senior-mvc.csproj": """<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>shop_senior_mvc</RootNamespace>
    <NoWarn>$(NoWarn);NU1902;NU1903</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.0" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>

</Project>
""",

    "appsettings.json": """{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=shop_senior.db"
  },
  "AllowedHosts": "*"
}
""",

    "Program.cs": """using Microsoft.EntityFrameworkCore;
using shop_senior_mvc.Data;
using shop_senior_mvc.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=shop_senior.db"));

builder.Services.AddHttpContextAccessor();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});
builder.Services.AddScoped<CartService>();

var app = builder.Build();

// Seed database on startup
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    SeedData.Initialize(context);
}

// Configure HTTP pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
""",

    "Models/Category.cs": """using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace shop_senior_mvc.Models
{
    public class Category
    {
        public int Id { get; set; }

        [Required, MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public List<Product> Products { get; set; } = new List<Product>();
    }
}
""",

    "Models/Product.cs": """using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace shop_senior_mvc.Models
{
    public class Product
    {
        public int Id { get; set; }

        [Required, MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,2)")]
        public decimal Price { get; set; }

        public string Description { get; set; } = string.Empty;

        public string ImageUrl { get; set; } = "https://images.unsplash.com/photo-1542291026-7eec264c27ff?w=600";

        public int CategoryId { get; set; }
        public Category Category { get; set; }
    }
}
""",

    "Models/Order.cs": """using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace shop_senior_mvc.Models
{
    public class Order
    {
        public int Id { get; set; }

        [Required, MaxLength(100)]
        public string CustomerName { get; set; } = string.Empty;

        [Required, Phone]
        public string Phone { get; set; } = string.Empty;

        [Required, MaxLength(250)]
        public string Address { get; set; } = string.Empty;

        public string Note { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalAmount { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public List<OrderItem> Items { get; set; } = new List<OrderItem>();
    }

    public class OrderItem
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public Order Order { get; set; }

        public int ProductId { get; set; }
        public Product Product { get; set; }

        public int Quantity { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal UnitPrice { get; set; }
    }
}
""",

    "Models/CartItem.cs": """namespace shop_senior_mvc.Models
{
    public class CartItem
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public int Quantity { get; set; }
        public decimal Total => Price * Quantity;
    }
}
""",

    "Models/ViewModels/CartViewModel.cs": """using System.Collections.Generic;
using System.Linq;

namespace shop_senior_mvc.Models.ViewModels
{
    public class CartViewModel
    {
        public List<CartItem> Items { get; set; } = new List<CartItem>();
        public decimal GrandTotal => Items.Sum(x => x.Total);
    }
}
""",

    "Models/ViewModels/CheckoutViewModel.cs": """using System.ComponentModel.DataAnnotations;

namespace shop_senior_mvc.Models.ViewModels
{
    public class CheckoutViewModel
    {
        [Required(ErrorMessage = "Vui lòng nhập họ tên")]
        [Display(Name = "Họ tên người nhận")]
        public string CustomerName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Vui lòng nhập số điện thoại")]
        [Phone(ErrorMessage = "Số điện thoại không hợp lệ")]
        [Display(Name = "Số điện thoại")]
        public string Phone { get; set; } = string.Empty;

        [Required(ErrorMessage = "Vui lòng nhập địa chỉ giao hàng")]
        [Display(Name = "Địa chỉ nhận hàng")]
        public string Address { get; set; } = string.Empty;

        [Display(Name = "Ghi chú đơn hàng")]
        public string Note { get; set; } = string.Empty;
    }

    public class HomeViewModel
    {
        public List<shop_senior_mvc.Models.Product> FeaturedProducts { get; set; } = new List<shop_senior_mvc.Models.Product>();
        public List<shop_senior_mvc.Models.Category> Categories { get; set; } = new List<shop_senior_mvc.Models.Category>();
    }
}
""",

    "Data/AppDbContext.cs": """using Microsoft.EntityFrameworkCore;
using shop_senior_mvc.Models;

namespace shop_senior_mvc.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Product> Products { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<Order> Orders { get; set; }
        public DbSet<OrderItem> OrderItems { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
        }
    }
}
""",

    "Data/SeedData.cs": """using System.Collections.Generic;
using System.Linq;
using shop_senior_mvc.Models;

namespace shop_senior_mvc.Data
{
    public static class SeedData
    {
        public static void Initialize(AppDbContext context)
        {
            context.Database.EnsureCreated();

            if (context.Products.Any()) return;

            var c1 = new Category { Name = "Sneakers Nam", Description = "Giày thể thao nam cao cấp, êm ái" };
            var c2 = new Category { Name = "Sneakers Nữ", Description = "Thiết kế năng động, thời trang" };
            var c3 = new Category { Name = "Running & Training", Description = "Chuyên dụng chạy bộ đường dài" };

            context.Categories.AddRange(c1, c2, c3);
            context.SaveChanges();

            context.Products.AddRange(
                new Product { Name = "Nike Air Max 270 React", Price = 2490000, CategoryId = c1.Id, Description = "Đệm khí Air êm ái tối đa, phối màu phong cách tương lai.", ImageUrl = "https://images.unsplash.com/photo-1542291026-7eec264c27ff?w=600" },
                new Product { Name = "Adidas Ultraboost Light 2026", Price = 2890000, CategoryId = c3.Id, Description = "Hạt Boost phản hồi năng lượng siêu nhẹ thế hệ mới.", ImageUrl = "https://images.unsplash.com/photo-1551107696-a4b0c5a0d9a2?w=600" },
                new Product { Name = "Jordan 1 Retro High Chicago", Price = 3850000, CategoryId = c1.Id, Description = "Huyền thoại bóng rổ phong cách đường phố kinh điển.", ImageUrl = "https://images.unsplash.com/photo-1584735935682-2f2b69dff9d2?w=600" },
                new Product { Name = "New Balance 550 Vintage White", Price = 2150000, CategoryId = c2.Id, Description = "Cổ điển thập niên 90, da mềm cao cấp ôm chân.", ImageUrl = "https://images.unsplash.com/photo-1539185441755-769473a23570?w=600" },
                new Product { Name = "Puma Nitro Elite Marathon", Price = 1990000, CategoryId = c3.Id, Description = "Tấm carbon trợ lực tốc độ cho cự ly dài.", ImageUrl = "https://images.unsplash.com/photo-1608231387042-66d1773070a5?w=600" },
                new Product { Name = "Vans Old Skool Pro Black/White", Price = 1250000, CategoryId = c2.Id, Description = "Chống mài mòn, phong cách ván trượt trẻ trung vượt thời gian.", ImageUrl = "https://images.unsplash.com/photo-1525966222134-fcfa99b8ae77?w=600" }
            );
            context.SaveChanges();
        }
    }
}
""",

    "Helpers/SessionExtensions.cs": """using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;

namespace shop_senior_mvc.Helpers
{
    public static class SessionExtensions
    {
        public static void SetObjectAsJson(this ISession session, string key, object value)
        {
            session.SetString(key, JsonConvert.SerializeObject(value));
        }

        public static T GetObjectFromJson<T>(this ISession session, string key)
        {
            var value = session.GetString(key);
            return value == null ? default(T) : JsonConvert.DeserializeObject<T>(value);
        }
    }
}
""",

    "Services/CartService.cs": """using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using shop_senior_mvc.Helpers;
using shop_senior_mvc.Models;

namespace shop_senior_mvc.Services
{
    public class CartService
    {
        private readonly ISession _session;
        private const string CartSessionKey = "SeniorShop_Cart";

        public CartService(IHttpContextAccessor httpContextAccessor)
        {
            _session = httpContextAccessor.HttpContext?.Session;
        }

        public List<CartItem> GetCartItems()
        {
            if (_session == null) return new List<CartItem>();
            return _session.GetObjectFromJson<List<CartItem>>(CartSessionKey) ?? new List<CartItem>();
        }

        public void AddToCart(Product product, int quantity = 1)
        {
            if (_session == null || product == null) return;
            var cart = GetCartItems();
            var existing = cart.FirstOrDefault(x => x.ProductId == product.Id);
            if (existing != null)
            {
                existing.Quantity += quantity;
            }
            else
            {
                cart.Add(new CartItem
                {
                    ProductId = product.Id,
                    ProductName = product.Name,
                    Price = product.Price,
                    ImageUrl = product.ImageUrl,
                    Quantity = quantity
                });
            }
            _session.SetObjectAsJson(CartSessionKey, cart);
        }

        public void UpdateQuantity(int productId, int quantity)
        {
            if (_session == null) return;
            var cart = GetCartItems();
            var item = cart.FirstOrDefault(x => x.ProductId == productId);
            if (item != null)
            {
                if (quantity <= 0) cart.Remove(item);
                else item.Quantity = quantity;
                _session.SetObjectAsJson(CartSessionKey, cart);
            }
        }

        public void RemoveFromCart(int productId)
        {
            if (_session == null) return;
            var cart = GetCartItems();
            var item = cart.FirstOrDefault(x => x.ProductId == productId);
            if (item != null)
            {
                cart.Remove(item);
                _session.SetObjectAsJson(CartSessionKey, cart);
            }
        }

        public void ClearCart()
        {
            _session?.Remove(CartSessionKey);
        }

        public decimal GetTotal() => GetCartItems().Sum(x => x.Total);
        public int GetTotalItems() => GetCartItems().Sum(x => x.Quantity);
    }
}
""",

    "Controllers/HomeController.cs": """using System.Linq;
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
""",

    "Controllers/ProductsController.cs": """using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using shop_senior_mvc.Data;

namespace shop_senior_mvc.Controllers
{
    public class ProductsController : Controller
    {
        private readonly AppDbContext _context;

        public ProductsController(AppDbContext context)
        {
            _context = context;
        }

        public IActionResult Index(int? categoryId, string search)
        {
            var query = _context.Products.Include(p => p.Category).AsQueryable();

            if (categoryId.HasValue && categoryId.Value > 0)
            {
                query = query.Where(p => p.CategoryId == categoryId.Value);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(p => p.Name.ToLower().Contains(search.ToLower()));
            }

            ViewBag.Categories = _context.Categories.ToList();
            ViewBag.CurrentCategory = categoryId;
            ViewBag.Search = search;

            return View(query.ToList());
        }

        public IActionResult Details(int id)
        {
            var product = _context.Products.Include(p => p.Category).FirstOrDefault(p => p.Id == id);
            if (product == null) return NotFound();
            return View(product);
        }
    }
}
""",

    "Controllers/CartController.cs": """using Microsoft.AspNetCore.Mvc;
using shop_senior_mvc.Data;
using shop_senior_mvc.Models.ViewModels;
using shop_senior_mvc.Services;

namespace shop_senior_mvc.Controllers
{
    public class CartController : Controller
    {
        private readonly CartService _cartService;
        private readonly AppDbContext _context;

        public CartController(CartService cartService, AppDbContext context)
        {
            _cartService = cartService;
            _context = context;
        }

        public IActionResult Index()
        {
            var vm = new CartViewModel
            {
                Items = _cartService.GetCartItems()
            };
            return View(vm);
        }

        [HttpPost]
        public IActionResult Add(int productId, int quantity = 1)
        {
            var product = _context.Products.Find(productId);
            if (product != null)
            {
                _cartService.AddToCart(product, quantity);
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public IActionResult Update(int productId, int quantity)
        {
            _cartService.UpdateQuantity(productId, quantity);
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public IActionResult Remove(int productId)
        {
            _cartService.RemoveFromCart(productId);
            return RedirectToAction(nameof(Index));
        }
    }
}
""",

    "Controllers/OrdersController.cs": """using System.Linq;
using Microsoft.AspNetCore.Mvc;
using shop_senior_mvc.Data;
using shop_senior_mvc.Models;
using shop_senior_mvc.Models.ViewModels;
using shop_senior_mvc.Services;

namespace shop_senior_mvc.Controllers
{
    public class OrdersController : Controller
    {
        private readonly CartService _cartService;
        private readonly AppDbContext _context;

        public OrdersController(CartService cartService, AppDbContext context)
        {
            _cartService = cartService;
            _context = context;
        }

        public IActionResult Checkout()
        {
            var items = _cartService.GetCartItems();
            if (items.Count == 0) return RedirectToAction("Index", "Cart");

            ViewBag.CartTotal = _cartService.GetTotal();
            ViewBag.CartItems = items;
            return View(new CheckoutViewModel());
        }

        [HttpPost]
        public IActionResult Checkout(CheckoutViewModel model)
        {
            var items = _cartService.GetCartItems();
            if (items.Count == 0) return RedirectToAction("Index", "Cart");

            if (!ModelState.IsValid)
            {
                ViewBag.CartTotal = _cartService.GetTotal();
                ViewBag.CartItems = items;
                return View(model);
            }

            var order = new Order
            {
                CustomerName = model.CustomerName,
                Phone = model.Phone,
                Address = model.Address,
                Note = model.Note ?? "",
                TotalAmount = _cartService.GetTotal(),
                Items = items.Select(i => new OrderItem
                {
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    UnitPrice = i.Price
                }).ToList()
            };

            _context.Orders.Add(order);
            _context.SaveChanges();
            _cartService.ClearCart();

            return RedirectToAction(nameof(Success), new { id = order.Id });
        }

        public IActionResult Success(int id)
        {
            var order = _context.Orders.Find(id);
            if (order == null) return RedirectToAction("Index", "Home");
            return View(order);
        }
    }
}
""",

    "Views/_ViewImports.cshtml": """@using shop_senior_mvc
@using shop_senior_mvc.Models
@using shop_senior_mvc.Models.ViewModels
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
""",

    "Views/_ViewStart.cshtml": """@{
    Layout = "_Layout";
}
""",

    "Views/Shared/_Layout.cshtml": """@inject shop_senior_mvc.Services.CartService CartSvc
<!DOCTYPE html>
<html lang="vi">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>@ViewData["Title"] - Sneaker Shop Senior</title>
    <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" rel="stylesheet" />
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.3/font/bootstrap-icons.min.css" />
    <style>
        body { background: #0f172a; color: #f8fafc; font-family: system-ui, -apple-system, sans-serif; }
        .navbar { background: #1e293b !important; border-bottom: 1px solid #334155; }
        .card { background: #1e293b; border: 1px solid #334155; color: #f8fafc; border-radius: 1rem; }
        .btn-primary { background: #3b82f6; border-color: #3b82f6; }
        .btn-primary:hover { background: #2563eb; }
        .footer { background: #0f172a; border-top: 1px solid #1e293b; color: #94a3b8; }
        .price-badge { font-weight: 700; color: #38bdf8; font-size: 1.15rem; }
    </style>
</head>
<body>
    <nav class="navbar navbar-expand-lg navbar-dark sticky-top">
        <div class="container">
            <a class="navbar-brand fw-bold text-primary" asp-controller="Home" asp-action="Index">
                <i class="bi bi-lightning-charge-fill me-1"></i>SNEAKER SHOP
            </a>
            <button class="navbar-toggler" type="button" data-bs-toggle="collapse" data-bs-target="#navMain">
                <span class="navbar-toggler-icon"></span>
            </button>
            <div class="collapse navbar-collapse" id="navMain">
                <ul class="navbar-nav me-auto mb-2 mb-lg-0">
                    <li class="nav-item"><a class="nav-link text-white" asp-controller="Home" asp-action="Index">Trang chủ</a></li>
                    <li class="nav-item"><a class="nav-link text-white" asp-controller="Products" asp-action="Index">Sản phẩm</a></li>
                </ul>
                <div class="d-flex align-items-center gap-3">
                    <a asp-controller="Cart" asp-action="Index" class="btn btn-outline-light position-relative rounded-pill px-3 py-1.5">
                        <i class="bi bi-cart3 me-1"></i>Giỏ hàng
                        <span class="position-absolute top-0 start-100 translate-middle badge rounded-pill bg-danger">
                            @(CartSvc.GetTotalItems())
                        </span>
                    </a>
                </div>
            </div>
        </div>
    </nav>

    <div class="container py-4">
        @RenderBody()
    </div>

    <footer class="footer py-4 mt-5 text-center">
        <div class="container">
            <p class="mb-0">© 2026 Sneaker Shop Senior — Xây dựng trên ASP.NET Core & SQLite bởi Btoan Studio IDE</p>
        </div>
    </footer>
    <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js"></script>
</body>
</html>
""",

    "Views/Home/Index.cshtml": """@model shop_senior_mvc.Models.ViewModels.HomeViewModel
@{
    ViewData["Title"] = "Trang Chủ";
}

<div class="p-5 mb-5 rounded-4 bg-gradient shadow-lg" style="background: linear-gradient(135deg, #1e293b 0%, #0f172a 100%); border: 1px solid #334155;">
    <div class="row align-items-center">
        <div class="col-lg-7">
            <span class="badge bg-primary px-3 py-2 rounded-pill mb-3">BST Sneakers 2026</span>
            <h1 class="display-4 fw-bold text-white mb-3">Đẳng Cấp Từng Bước Chạy</h1>
            <p class="lead text-secondary mb-4">Bộ sưu tập giày thể thao nam nữ chính hãng cao cấp, công nghệ đệm khí tiên tiến và thiết kế thời thượng.</p>
            <a asp-controller="Products" asp-action="Index" class="btn btn-primary btn-lg rounded-pill px-4 shadow">
                <i class="bi bi-bag-check me-2"></i>Khám Phá Ngay
            </a>
        </div>
        <div class="col-lg-5 text-center mt-4 mt-lg-0">
            <img src="https://images.unsplash.com/photo-1542291026-7eec264c27ff?w=600" class="img-fluid rounded-4 shadow-lg hover-scale" alt="Sneaker" style="max-height: 320px; object-fit: cover;" />
        </div>
    </div>
</div>

<div class="d-flex justify-content-between align-items-center mb-4">
    <h3 class="fw-bold text-white mb-0">Sản Phẩm Nổi Bật</h3>
    <a asp-controller="Products" asp-action="Index" class="text-primary text-decoration-none fw-semibold">Xem tất cả <i class="bi bi-arrow-right"></i></a>
</div>

<div class="row g-4">
    @foreach (var p in Model.FeaturedProducts)
    {
        <div class="col-12 col-md-6 col-lg-4">
            <div class="card h-100 shadow-sm">
                <img src="@p.ImageUrl" class="card-img-top" style="height: 240px; object-fit: cover;" alt="@p.Name">
                <div class="card-body d-flex flex-column">
                    <span class="badge bg-secondary mb-2 align-self-start">@p.Category?.Name</span>
                    <h5 class="card-title fw-bold text-white">@p.Name</h5>
                    <p class="card-text text-secondary small flex-grow-1">@p.Description</p>
                    <div class="d-flex justify-content-between align-items-center mt-3 pt-3 border-top border-secondary">
                        <span class="price-badge">@p.Price.ToString("N0") đ</span>
                        <div class="d-flex gap-2">
                            <a asp-controller="Products" asp-action="Details" asp-route-id="@p.Id" class="btn btn-outline-light btn-sm rounded-pill">Chi tiết</a>
                            <form asp-controller="Cart" asp-action="Add" method="post">
                                <input type="hidden" name="productId" value="@p.Id" />
                                <button type="submit" class="btn btn-primary btn-sm rounded-pill"><i class="bi bi-cart-plus"></i></button>
                            </form>
                        </div>
                    </div>
                </div>
            </div>
        </div>
    }
</div>
""",

    "Views/Products/Index.cshtml": """@model List<shop_senior_mvc.Models.Product>
@{
    ViewData["Title"] = "Danh Sách Sản Phẩm";
    var categories = ViewBag.Categories as List<shop_senior_mvc.Models.Category> ?? new List<shop_senior_mvc.Models.Category>();
    int? currentCat = ViewBag.CurrentCategory as int?;
    string search = ViewBag.Search as string;
}

<div class="row mb-4">
    <div class="col-md-8">
        <h2 class="fw-bold text-white">Bộ Sưu Tập Giày Sneaker</h2>
        <p class="text-secondary">Tìm thấy @Model.Count sản phẩm phù hợp</p>
    </div>
    <div class="col-md-4">
        <form asp-action="Index" method="get" class="d-flex gap-2">
            <input type="text" name="search" value="@search" class="form-control bg-dark border-secondary text-white rounded-pill" placeholder="Tìm tên giày..." />
            <button type="submit" class="btn btn-primary rounded-pill px-3"><i class="bi bi-search"></i></button>
        </form>
    </div>
</div>

<div class="mb-4 d-flex gap-2 flex-wrap">
    <a asp-action="Index" class="btn btn-sm rounded-pill @(!currentCat.HasValue ? "btn-primary" : "btn-outline-secondary text-white")">Tất cả</a>
    @foreach (var c in categories)
    {
        <a asp-action="Index" asp-route-categoryId="@c.Id" class="btn btn-sm rounded-pill @(currentCat == c.Id ? "btn-primary" : "btn-outline-secondary text-white")">@c.Name</a>
    }
</div>

<div class="row g-4">
    @foreach (var p in Model)
    {
        <div class="col-12 col-md-6 col-lg-4">
            <div class="card h-100 shadow-sm">
                <img src="@p.ImageUrl" class="card-img-top" style="height: 240px; object-fit: cover;" alt="@p.Name">
                <div class="card-body d-flex flex-column">
                    <span class="badge bg-secondary mb-2 align-self-start">@p.Category?.Name</span>
                    <h5 class="card-title fw-bold text-white">@p.Name</h5>
                    <p class="card-text text-secondary small flex-grow-1">@p.Description</p>
                    <div class="d-flex justify-content-between align-items-center mt-3 pt-3 border-top border-secondary">
                        <span class="price-badge">@p.Price.ToString("N0") đ</span>
                        <div class="d-flex gap-2">
                            <a asp-action="Details" asp-route-id="@p.Id" class="btn btn-outline-light btn-sm rounded-pill">Chi tiết</a>
                            <form asp-controller="Cart" asp-action="Add" method="post">
                                <input type="hidden" name="productId" value="@p.Id" />
                                <button type="submit" class="btn btn-primary btn-sm rounded-pill"><i class="bi bi-cart-plus"></i> Mua</button>
                            </form>
                        </div>
                    </div>
                </div>
            </div>
        </div>
    }
</div>
""",

    "Views/Products/Details.cshtml": """@model shop_senior_mvc.Models.Product
@{
    ViewData["Title"] = Model.Name;
}

<div class="row g-5 align-items-center py-4">
    <div class="col-lg-6">
        <img src="@Model.ImageUrl" class="img-fluid rounded-4 shadow-lg w-100" style="max-height: 480px; object-fit: cover;" alt="@Model.Name">
    </div>
    <div class="col-lg-6">
        <span class="badge bg-primary px-3 py-2 rounded-pill mb-3">@Model.Category?.Name</span>
        <h1 class="fw-bold text-white mb-3">@Model.Name</h1>
        <div class="price-badge fs-2 mb-4">@Model.Price.ToString("N0") đ</div>
        <p class="lead text-secondary mb-4">@Model.Description</p>
        
        <form asp-controller="Cart" asp-action="Add" method="post" class="d-flex gap-3 align-items-center">
            <input type="hidden" name="productId" value="@Model.Id" />
            <div style="width: 100px;">
                <input type="number" name="quantity" value="1" min="1" max="10" class="form-control bg-dark border-secondary text-white text-center rounded-pill" />
            </div>
            <button type="submit" class="btn btn-primary btn-lg rounded-pill px-4 shadow">
                <i class="bi bi-cart-plus me-2"></i>Thêm Vào Giỏ Hàng
            </button>
            <a asp-action="Index" class="btn btn-outline-secondary rounded-pill px-3">Quay lại</a>
        </form>
    </div>
</div>
""",

    "Views/Cart/Index.cshtml": """@model shop_senior_mvc.Models.ViewModels.CartViewModel
@{
    ViewData["Title"] = "Giỏ Hàng";
}

<h2 class="fw-bold text-white mb-4"><i class="bi bi-cart3 me-2"></i>Giỏ Hàng Của Bạn</h2>

@if (Model.Items.Count == 0)
{
    <div class="text-center py-5">
        <i class="bi bi-cart-x fs-1 text-secondary mb-3 d-block"></i>
        <h4 class="text-secondary">Giỏ hàng của bạn đang trống</h4>
        <a asp-controller="Products" asp-action="Index" class="btn btn-primary rounded-pill px-4 mt-3">Tiếp tục mua sắm</a>
    </div>
}
else
{
    <div class="row g-4">
        <div class="col-lg-8">
            <div class="card p-3 shadow-sm">
                <div class="table-responsive">
                    <table class="table table-dark table-borderless align-middle mb-0">
                        <thead>
                            <tr class="border-bottom border-secondary text-secondary">
                                <th>Sản phẩm</th>
                                <th>Đơn giá</th>
                                <th style="width: 120px;">Số lượng</th>
                                <th>Tổng</th>
                                <th></th>
                            </tr>
                        </thead>
                        <tbody>
                            @foreach (var item in Model.Items)
                            {
                                <tr>
                                    <td>
                                        <div class="d-flex align-items-center gap-3">
                                            <img src="@item.ImageUrl" class="rounded-3" style="width: 60px; height: 60px; object-fit: cover;" alt="@item.ProductName" />
                                            <span class="fw-semibold text-white">@item.ProductName</span>
                                        </div>
                                    </td>
                                    <td>@item.Price.ToString("N0") đ</td>
                                    <td>
                                        <form asp-action="Update" method="post" class="d-flex gap-1">
                                            <input type="hidden" name="productId" value="@item.ProductId" />
                                            <input type="number" name="quantity" value="@item.Quantity" min="1" max="99" class="form-control form-control-sm bg-dark border-secondary text-white text-center" onchange="this.form.submit()" />
                                        </form>
                                    </td>
                                    <td class="fw-bold text-info">@item.Total.ToString("N0") đ</td>
                                    <td>
                                        <form asp-action="Remove" method="post">
                                            <input type="hidden" name="productId" value="@item.ProductId" />
                                            <button type="submit" class="btn btn-link text-danger p-0"><i class="bi bi-trash"></i></button>
                                        </form>
                                    </td>
                                </tr>
                            }
                        </tbody>
                    </table>
                </div>
            </div>
        </div>
        <div class="col-lg-4">
            <div class="card p-4 shadow-sm">
                <h5 class="fw-bold text-white mb-3">Tóm Tắt Đơn Hàng</h5>
                <div class="d-flex justify-content-between mb-2">
                    <span class="text-secondary">Tạm tính:</span>
                    <span class="fw-semibold text-white">@Model.GrandTotal.ToString("N0") đ</span>
                </div>
                <div class="d-flex justify-content-between mb-3">
                    <span class="text-secondary">Phí vận chuyển:</span>
                    <span class="text-success fw-semibold">Miễn phí</span>
                </div>
                <div class="border-top border-secondary pt-3 d-flex justify-content-between mb-4">
                    <span class="fw-bold fs-5 text-white">Tổng cộng:</span>
                    <span class="fw-bold fs-5 text-primary">@Model.GrandTotal.ToString("N0") đ</span>
                </div>
                <a asp-controller="Orders" asp-action="Checkout" class="btn btn-primary w-100 rounded-pill py-2.5 fw-bold shadow">
                    Tiến Hành Đặt Hàng <i class="bi bi-arrow-right ms-1"></i>
                </a>
            </div>
        </div>
    </div>
}
""",

    "Views/Orders/Checkout.cshtml": """@model shop_senior_mvc.Models.ViewModels.CheckoutViewModel
@{
    ViewData["Title"] = "Thanh Toán";
    var items = ViewBag.CartItems as List<shop_senior_mvc.Models.CartItem> ?? new List<shop_senior_mvc.Models.CartItem>();
    decimal total = ViewBag.CartTotal is decimal d ? d : 0;
}

<h2 class="fw-bold text-white mb-4"><i class="bi bi-credit-card me-2"></i>Thông Tin Giao Hàng & Đặt Hàng</h2>

<div class="row g-5">
    <div class="col-lg-7">
        <div class="card p-4 shadow-sm">
            <form asp-action="Checkout" method="post">
                <div class="mb-3">
                    <label asp-for="CustomerName" class="form-label text-secondary fw-semibold"></label>
                    <input asp-for="CustomerName" class="form-control bg-dark border-secondary text-white rounded-3" placeholder="Nguyễn Văn A" />
                    <span asp-validation-for="CustomerName" class="text-danger small"></span>
                </div>

                <div class="mb-3">
                    <label asp-for="Phone" class="form-label text-secondary fw-semibold"></label>
                    <input asp-for="Phone" class="form-control bg-dark border-secondary text-white rounded-3" placeholder="0912345678" />
                    <span asp-validation-for="Phone" class="text-danger small"></span>
                </div>

                <div class="mb-3">
                    <label asp-for="Address" class="form-label text-secondary fw-semibold"></label>
                    <input asp-for="Address" class="form-control bg-dark border-secondary text-white rounded-3" placeholder="123 Đường ABC, Quận 1, TP. HCM" />
                    <span asp-validation-for="Address" class="text-danger small"></span>
                </div>

                <div class="mb-4">
                    <label asp-for="Note" class="form-label text-secondary fw-semibold"></label>
                    <textarea asp-for="Note" class="form-control bg-dark border-secondary text-white rounded-3" rows="3" placeholder="Ghi chú giao hàng (giờ nhận hàng, v.v.)"></textarea>
                </div>

                <button type="submit" class="btn btn-primary btn-lg w-100 rounded-pill py-2.5 fw-bold shadow">
                    <i class="bi bi-check2-circle me-1"></i> Xác Nhận Đặt Hàng (@total.ToString("N0") đ)
                </button>
            </form>
        </div>
    </div>

    <div class="col-lg-5">
        <div class="card p-4 shadow-sm">
            <h5 class="fw-bold text-white mb-3">Đơn Hàng Của Bạn (@items.Count món)</h5>
            <div class="d-flex flex-column gap-3 mb-4">
                @foreach (var item in items)
                {
                    <div class="d-flex justify-content-between align-items-center">
                        <div class="d-flex align-items-center gap-2">
                            <img src="@item.ImageUrl" class="rounded-2" style="width: 44px; height: 44px; object-fit: cover;" alt="@item.ProductName" />
                            <div>
                                <div class="text-white small fw-semibold">@item.ProductName</div>
                                <div class="text-secondary extra-small">x @item.Quantity</div>
                            </div>
                        </div>
                        <span class="text-info fw-semibold small">@item.Total.ToString("N0") đ</span>
                    </div>
                }
            </div>
            <div class="border-top border-secondary pt-3 d-flex justify-content-between">
                <span class="fw-bold fs-5 text-white">Tổng cộng:</span>
                <span class="fw-bold fs-5 text-primary">@total.ToString("N0") đ</span>
            </div>
        </div>
    </div>
</div>
""",

    "Views/Orders/Success.cshtml": """@model shop_senior_mvc.Models.Order
@{
    ViewData["Title"] = "Đặt Hàng Thành Công";
}

<div class="text-center py-5">
    <div class="mb-4">
        <i class="bi bi-patch-check-fill text-success" style="font-size: 4rem;"></i>
    </div>
    <h1 class="fw-bold text-white mb-3">Đặt Hàng Thành Công!</h1>
    <p class="lead text-secondary mb-4">Cảm ơn bạn đã tin tưởng. Mã đơn hàng của bạn là <span class="badge bg-primary fs-6">#@Model.Id</span>.</p>

    <div class="card mx-auto p-4 mb-4 text-start shadow-sm" style="max-width: 520px;">
        <h5 class="fw-bold text-white mb-3 border-bottom border-secondary pb-2">Chi Tiết Đơn Hàng</h5>
        <div class="mb-2"><span class="text-secondary">Người nhận:</span> <b class="text-white">@Model.CustomerName</b></div>
        <div class="mb-2"><span class="text-secondary">Số điện thoại:</span> <b class="text-white">@Model.Phone</b></div>
        <div class="mb-2"><span class="text-secondary">Địa chỉ:</span> <b class="text-white">@Model.Address</b></div>
        <div class="mb-2"><span class="text-secondary">Tổng thanh toán:</span> <b class="text-primary fs-5">@Model.TotalAmount.ToString("N0") đ</b></div>
    </div>

    <a asp-controller="Home" asp-action="Index" class="btn btn-primary rounded-pill px-4 shadow">
        <i class="bi bi-house me-1"></i> Về Trang Chủ
    </a>
</div>
"""
}

for rel_path, content in files.items():
    full_path = os.path.join(base_dir, rel_path.replace("/", os.sep))
    os.makedirs(os.path.dirname(full_path), exist_ok=True)
    with open(full_path, "w", encoding="utf-8") as f:
        f.write(content)

print(f"Generated {len(files)} files successfully in {base_dir}")
