using System.ComponentModel.DataAnnotations;

namespace RedAJP.Models
{
    public class IglesiaRegistroViewModel
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "El nombre es obligatorio")]
        public string Nombre { get; set; }

        [Required(ErrorMessage = "Selecciona un municipio")]
        [Display(Name = "Municipio")]
        public int MunicipioId { get; set; } // Para mappear a 'municipio_id'

        [Required(ErrorMessage = "La localidad es obligatoria")]
        public string Localidad { get; set; }

        [Required(ErrorMessage = "La colonia es obligatoria")]
        public string Colonia { get; set; }

        [Required(ErrorMessage = "La calle es obligatoria")]
        public string Calle { get; set; }

        [Required(ErrorMessage = "El número es obligatorio (pon S/N si no tiene)")]
        public string Numero { get; set; }


        [Required(ErrorMessage = "La Referencia es Obligatoria")]
        public string Referencia { get; set; }

        [Required(ErrorMessage = "Escribe los horarios")]
        public string Horarios { get; set; }

        [Url(ErrorMessage = "Debe ser un link válido")]
        [Required(ErrorMessage = "La URL es Obligatoria")]
        public string MapaUrl { get; set; }

        [Url(ErrorMessage = "Debe ser un link válido")]
        public string FacebookUrl { get; set; }

        [Display(Name = "Latitud")]
        public decimal? Latitud { get; set; }

        [Display(Name = "Longitud")]
        public decimal? Longitud { get; set; }

        [Display(Name = "¿Activar Página Web Personalizada?")]
        public bool ActivarPaginaWeb { get; set; }

        [Display(Name = "Administradores de la Página")]
        public List<int> AdministradoresIds { get; set; } = new List<int>();

        public int Sector { get; set; }
        public int Zona { get; set; }
    }
}