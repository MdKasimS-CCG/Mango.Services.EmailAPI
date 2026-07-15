using System.ComponentModel.DataAnnotations;

namespace Mango.Services.EmailAPI.Models.Dto
{
    public class ProductDto
    {
        //Note: Tutor copied thsi from Mango.Web
        public int Id { get; set; }
        public string Name { get; set; }
        public double Price { get; set; }
        //TODO: Image uploading part is missing. Do yourself
        public string ImageUrl { get; set; }
        public string CategoryName { get; set; }
        public string Description { get; set; } = string.Empty;
        public int Count { get; set; } = 1;
    }
}
