using System;
using System.Collections.Generic; // Necesario para List<>
using System.ComponentModel.DataAnnotations;

namespace RedAJP.Models
{
    public class CuponViewModel
    {
        public int Id_Cupon { get; set; }

        [Required(ErrorMessage = "El código es obligatorio")]
        [StringLength(50, ErrorMessage = "Máximo 50 caracteres")]
        public string Codigo { get; set; }

        public string Descripcion { get; set; }

        public int Tipo_Descuento { get; set; } = 1;
        public decimal Valor { get; set; }
        public decimal? Tope_Maximo_Descuento { get; set; }
        public decimal Monto_Minimo_Compra { get; set; } = 0;
        public int Limite_Usos { get; set; } = 10;
        public int Conteo_Usados { get; set; }

        [Required]
        public DateTime Fecha_Inicio { get; set; } = DateTime.Now;

        [Required]
        public DateTime Fecha_Fin { get; set; } = DateTime.Now.AddMonths(1);

        public bool Activo { get; set; } = true;
        public bool Aplica_Eventos { get; set; }
        public bool Aplica_Tienda { get; set; }
        public int? Id_Evento_Restringido { get; set; }

        // Auditoría
        public string? CreadoPor { get; set; }
        public DateTime FechaCreacion { get; set; }
        public string? EditadoPor { get; set; }
        public DateTime? FechaEdicion { get; set; }

        public List<int> IdsUsuariosAutorizados { get; set; } = new List<int>();
        public int ConteoUsuariosRestringidos { get; set; }
    }
}