using System.ComponentModel.DataAnnotations;

namespace RedAJP.Models
{
    // SOLO dejamos el modelo de Categorías
    public class CategoriaViewModel
    {
        public int Id_Categoria { get; set; }

        [Required(ErrorMessage = "El nombre es obligatorio")]
        public string Nombre { get; set; }

        public string? Descripcion { get; set; }

        public string Icono { get; set; }

        public bool Activo { get; set; }

        [Required(ErrorMessage = "Debes definir las medidas por defecto")]
        public string Medidas_Default { get; set; }

        public int TotalProductos { get; set; }
    }
}