using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema; // Necesario para [NotMapped]

namespace RedAJP.Models
{
    public class MovimientoCaja
    {
        public int Id_Movimiento { get; set; }

        [Required(ErrorMessage = "Escribe un concepto")]
        public string Concepto { get; set; }

        [Required]
        [Range(0.01, 9999999, ErrorMessage = "El monto debe ser mayor a 0")]
        public decimal Monto { get; set; }

        public string Tipo { get; set; } // "Ingreso" o "Egreso"

        [Required(ErrorMessage = "La fecha es obligatoria")]
        public DateTime Fecha { get; set; } = DateTime.Now;

        // 1. ID de la tabla Rec_Archivos (Foreign Key)
        // no guardamos la ruta string "ComprobanteUrl", sino el ID numérico
        public int? Id_Archivo { get; set; }

        // 2. Objeto para recibir el archivo del formulario HTML
        // [NotMapped] significa que EF/SQL ignorarán este campo al leer la tabla Fin_Caja
        [NotMapped]
        [Display(Name = "Comprobante (Imagen/PDF)")]
        public IFormFile? ArchivoComprobante { get; set; }

        public int Id_Usuario { get; set; }

        // Campo auxiliar para mostrar el nombre en la tabla (JOIN)
        public string? NombreUsuario { get; set; }
        public bool Movimiento_En_Banco { get; set; }
        public string FolioBancario { get; set; }
        public int? Id_Evento { get; set; }

        public string NombreEvento { get; set; }
    }
}