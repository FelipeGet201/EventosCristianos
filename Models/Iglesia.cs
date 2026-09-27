using System.ComponentModel.DataAnnotations;

namespace RedAJP.Models
{
    // Clase para el Dropdown del buscador
    public class Municipio
    {
        public int Id { get; set; }
        public string Nombre { get; set; }
    }

    // Clase principal para mostrar la tarjeta
    public class Iglesia
    {
        public int Id { get; set; }
        public string Nombre { get; set; }
        public string Municipio { get; set; } // Nombre del municipio (JOIN)
        public string Calle { get; set; }
        public string Numero { get; set; }
        public string Colonia { get; set; }
        public string Localidad { get; set; }
        public string Referencia { get; set; }
        public string Horarios { get; set; } // Viene con saltos de línea \n
        public string MapaUrl { get; set; }
        public string FacebookUrl { get; set; }
        public decimal? Latitud { get; set; }
        public decimal? Longitud { get; set; }
        public string Slug { get; set; }
        public bool EsMiIglesia { get; set; }
        public bool TienePaginaWeb { get; set; }
        public bool SoyAdministrador { get; set; }
        public int Sector { get; set; }
        public int Zona { get; set; }
    }
}