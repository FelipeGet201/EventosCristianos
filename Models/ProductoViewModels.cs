using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace RedAJP.Models
{
    public class ProductoListadoItem
    {
        public int Id_Producto { get; set; }
        public string Nombre { get; set; }
        public string Categoria { get; set; }
        public decimal Precio { get; set; }
        public bool Es_Personalizable { get; set; }
        public int Stock { get; set; }
        public bool Activo { get; set; }
        public string? ImagenUrl { get; set; }
    }

    // --- CLASE AUXILIAR PARA EL STOCK POR TALLA ---
    public class MedidaStockItem
    {
        public string Nombre { get; set; }
        public int Stock { get; set; }
    }
    public class EditorProductoViewModel
    {
        public int Id_Producto { get; set; }

        [Required(ErrorMessage = "El nombre es obligatorio")]
        public string Nombre_Comercial { get; set; }

        [Required(ErrorMessage = "Selecciona una categoría")]
        public int Id_Categoria { get; set; }

        public string? Descripcion { get; set; }

        [Required]
        [Range(0.01, 100000, ErrorMessage = "El precio debe ser mayor a 0")]
        public decimal Precio_Venta { get; set; }

        public bool Es_Personalizable { get; set; }

        // Stock Total (Calculado, solo lectura o caché)
        public int Stock_Tienda { get; set; }

        public bool Activo { get; set; }

        // ====== SOLUCIÓN: AGREGAR ESTA LÍNEA ======
        public IFormFile? ArchivoImagen { get; set; }
        // ==========================================

        public List<string> GaleriaActualUrls { get; set; } = new List<string>();
        public List<IFormFile> ArchivosGaleria { get; set; }
        public string? ImagenActualUrl { get; set; }

        public bool Usa_Medidas_Categoria { get; set; }

        // Mantenemos por compatibilidad, pero ya no es la fuente principal
        public string? MedidasInput { get; set; }

        // --- NUEVA PROPIEDAD: LISTA DE TALLAS CON STOCK ---
        public List<MedidaStockItem> TallasStock { get; set; } = new List<MedidaStockItem>();

        public bool TieneVentas { get; set; }

        // Área de Impresión
        public int Area_X { get; set; } = 25;
        public int Area_Y { get; set; } = 20;
        public int Area_Ancho { get; set; } = 50;
        public int Area_Alto { get; set; } = 60;
        public bool Cobrar_Comision_Extra { get; set; } = true;

        // --- DISEÑO FRONTAL ---
        public string? ImagenImpresionUrl { get; set; }
        public IFormFile? ArchivoImagenImpresion { get; set; }
        public bool BorrarImagenImpresion { get; set; }

        // --- DISEÑO POSTERIOR ---
        public string? ImagenImpresionPosteriorUrl { get; set; }
        public IFormFile? ArchivoImagenImpresionPosterior { get; set; }
        public bool BorrarImagenImpresionPosterior { get; set; }
    }
}