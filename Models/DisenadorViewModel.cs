using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations;

namespace RedAJP.Models
{
    public class DisenadorViewModel
    {
        // ==========================================
        // DATOS DEL PRODUCTO BASE (Lectura)
        // ==========================================
        public int Id_Producto_Base { get; set; }
        public string Nombre_Base { get; set; }
        public string Imagen_Base_Url { get; set; } // La foto de la playera/termo

        // ==========================================
        // CONFIGURACIÓN DEL ÁREA (Vienen de la BD)
        // ==========================================
        // Estas coordenadas definen dónde se puede imprimir (el recuadro invisible)
        public int Area_X { get; set; }      // Left %
        public int Area_Y { get; set; }      // Top %
        public int Area_Ancho { get; set; }  // Width %
        public int Area_Alto { get; set; }   // Height %

        // ==========================================
        // INPUTS DEL USUARIO (Escritura)
        // ==========================================
        [Required(ErrorMessage = "Por favor sube una imagen para tu diseño.")]
        public IFormFile? ArchivoOriginal { get; set; } // La imagen original (Alta Calidad)

        public string? ImagenPrevioBase64 { get; set; } // La captura compuesta (Para el carrito)

        public string? ConfiguracionJson { get; set; } // Guardamos posición/escala por si acaso
    }
}