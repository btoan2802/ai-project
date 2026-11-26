using Microsoft.EntityFrameworkCore;
using shop_senior_mvc.Data;
using shop_senior_mvc.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

string rawConn = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=shop_senior.db";
if (rawConn.Contains("Server=", StringComparison.OrdinalIgnoreCase) || !rawConn.Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
{
    rawConn = "Data Source=shop_senior.db";
}
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(rawConn));

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
try
{
    using (var scope = app.Services.CreateScope())
    {
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SeedData.Initialize(context);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[Startup] Database initialization notice: {ex.Message}");
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
