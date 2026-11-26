using System.Collections.Generic;
using System.Linq;
using shop_senior_mvc.Models;

namespace shop_senior_mvc.Data
{
    public static class SeedData
    {
        public static void Initialize(AppDbContext context)
        {
            try
            {
                context.Database.EnsureCreated();
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"[SeedData] EnsureCreated notice: {ex.Message}");
            }

            try
            {
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
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"[SeedData] Seeding notice: {ex.Message}");
            }
        }
    }
}
