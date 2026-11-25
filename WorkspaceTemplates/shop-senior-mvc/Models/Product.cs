using System.ComponentModel.DataAnnotations;
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
