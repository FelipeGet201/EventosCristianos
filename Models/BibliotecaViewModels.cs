using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations;

namespace RedAJP.Models
{
    public class LibroMetadata
    {
        public string Titulo { get; set; }
        public string Autor { get; set; }
        public string Descripcion { get; set; }
        public int Descargas { get; set; } = 0;
    }

    public class LibroViewModel
    {
        public string IdCarpeta { get; set; }
        public string Titulo { get; set; }
        public string Autor { get; set; }
        public string Descripcion { get; set; }
        public string RutaArchivo { get; set; }
        public string RutaPortada { get; set; }
        public int Descargas { get; set; }
    }

    public class LibroFormViewModel
    {
        public string IdCarpeta { get; set; } // Vacío si es nuevo

        [Required(ErrorMessage = "El título es obligatorio.")]
        public string Titulo { get; set; }

        [Required(ErrorMessage = "El autor es obligatorio.")]
        public string Autor { get; set; }

        public string Descripcion { get; set; }

        public IFormFile ArchivoPdf { get; set; }
        public IFormFile ArchivoPortada { get; set; }
    }
}