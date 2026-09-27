using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace RedAJP.Models
{
    // ViewModel para el listado en el Index
    public class FormatoViewModel
    {
        public int Id { get; set; }
        public string Titulo { get; set; }
        public string Descripcion { get; set; }
        public string IconoClase { get; set; }
        public string ColorClase { get; set; }
        public string UsuarioCarga { get; set; }
        public DateTime Fecha { get; set; }
        public bool EsEnlace { get; set; }
        public string UrlEnlace { get; set; }
        public string Categoria { get; set; }
        public int? IdGrupoAcceso { get; set; }
        public string NombreGrupoAcceso { get; set; }
        public string TipoMime { get; set; }
        public int IdUsuarioCarga { get; set; }
        public string Origen { get; set; } // "Recursos" o "Formatos"
        public string UrlPortada { get; set; }
    }

    // ViewModel para la vista de Edición/Carga con Data Annotations
    public class EditorFormatoViewModel
    {
        public int Id_Archivo { get; set; }

        [Required(ErrorMessage = "El título es obligatorio.")]
        [StringLength(150, ErrorMessage = "El título no puede exceder los 150 caracteres.")]
        [Display(Name = "Título del Documento o Enlace")]
        public string Titulo { get; set; }

        [StringLength(500, ErrorMessage = "La descripción no puede exceder los 500 caracteres.")]
        [Display(Name = "Descripción breve")]
        public string Descripcion { get; set; }

        [Display(Name = "Archivo a subir")]
        public IFormFile Archivo { get; set; }

        public string NombreArchivoActual { get; set; }

        [Display(Name = "¿Es un enlace externo?")]
        public bool EsEnlace { get; set; }

        [Url(ErrorMessage = "Por favor, ingresa una URL válida (ej. https://...).")]
        [Display(Name = "URL del Enlace")]
        public string UrlEnlace { get; set; }

        [Required(ErrorMessage = "Debes seleccionar una categoría.")]
        [Display(Name = "Nivel de Acceso")]
        public string Categoria { get; set; }

        [Display(Name = "Grupo de Usuarios (Solo si es restringido)")]
        public int? IdGrupoAcceso { get; set; }

        public List<SelectListItem> ListaGrupos { get; set; } = new List<SelectListItem>();
        public string Origen { get; set; } = "Formatos";
        public IFormFile Portada { get; set; } // Archivo físico de la imagen
        public bool EsVideo { get; set; }
        public string UrlPortadaActual { get; set; }
    }
}