using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace RedAJP.Models
{
    // =========================================================
    // 1. MODELO PARA EL PANEL PRINCIPAL (DASHBOARD)
    // =========================================================
    public class MisIglesiasPanelViewModel
    {
        public int IdIglesia { get; set; }
        public string sIdIglesia { get; set; }
        public string Nombre { get; set; }
        public string Slug { get; set; }
        public string Municipio { get; set; }
        public bool EstaActiva { get; set; }
        public bool TieneBorradorPendiente { get; set; }
        public bool TieneBorradorDatos { get; set; }
        public bool TieneBorradorDiseno { get; set; }
        public DateTime? UltimaActualizacion { get; set; }
        public bool EsModular { get; set; }
    }

    // =========================================================
    // 2. MODELO PRINCIPAL DE EDICIÓN (CONTIENE TODAS LAS PESTAÑAS)
    // =========================================================
    public class IglesiaWebEdicionViewModel
    {
        public int IdIglesia { get; set; }
        public string sIdIglesia { get; set; }
        public string NombreIglesia { get; set; }
        public string? HorariosIglesia { get; set; }

        [Display(Name = "URL Personalizada (Slug)")]
        [Required(ErrorMessage = "El enlace de la página es obligatorio")]
        [RegularExpression(@"^[a-z0-9\-]+$", ErrorMessage = "El enlace solo puede contener letras minúsculas, números y guiones medios (sin espacios ni acentos).")]
        public string Slug { get; set; }

        [Display(Name = "Plantilla de Diseño")]
        public int PlantillaId { get; set; } = 1;

        [Display(Name = "¿Página Pública Activa?")]
        public bool Activa { get; set; }

        [Url(ErrorMessage = "Debe ser un enlace válido (ej. https://facebook.com/...)")]
        public string? FacebookUrl { get; set; }

        [Url(ErrorMessage = "Debe ser un enlace válido")]
        public string? InstagramUrl { get; set; }

        [Url(ErrorMessage = "Debe ser un enlace válido")]
        public string? YoutubeUrl { get; set; }

        [RegularExpression(@"^\d+$", ErrorMessage = "El WhatsApp debe contener solo números, sin espacios ni guiones.")]
        public string? Whatsapp { get; set; }

        [RegularExpression(@"^\d+$", ErrorMessage = "El Teléfono debe contener solo números, sin espacios ni guiones.")]
        public string? Telefono { get; set; }

        public string? Historia { get; set; }

        public List<AvisoWebViewModel> Avisos { get; set; } = new List<AvisoWebViewModel>();
        public List<EventoWebViewModel> Eventos { get; set; } = new List<EventoWebViewModel>();
        public List<GaleriaWebViewModel> Galeria { get; set; } = new List<GaleriaWebViewModel>();
        public List<MultimediaWebViewModel> Multimedia { get; set; } = new List<MultimediaWebViewModel>();

        public List<IFormFile>? NuevasImagenesGaleria { get; set; }
    }

    // =========================================================
    // 3. SUB-MODELOS PARA LAS LISTAS DINÁMICAS
    // =========================================================

    public class AvisoWebViewModel
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "El título del aviso es obligatorio")]
        public string Titulo { get; set; }

        public string? Descripcion { get; set; }

        [Required(ErrorMessage = "Fecha de inicio obligatoria")]
        public DateTime FechaInicio { get; set; }

        [Required(ErrorMessage = "Fecha de expiración obligatoria")]
        public DateTime FechaExpiracion { get; set; }
    }

    public class EventoWebViewModel
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "El título del evento es obligatorio")]
        public string Titulo { get; set; }

        [Required(ErrorMessage = "La fecha es obligatoria")]
        public DateTime Fecha { get; set; }

        [Required(ErrorMessage = "Especifique al menos un horario (ej. 10:00 AM)")]
        public string Horarios { get; set; }

        public string? Descripcion { get; set; } // ¡NUEVO: Opcional!

        public string? ImagenUrl { get; set; } // ¡NUEVO: Opcional, arregla tu error!

        public IFormFile? NuevaImagen { get; set; } // ¡NUEVO: Opcional!
    }

    public class GaleriaWebViewModel
    {
        public int Id { get; set; }
        public string UrlImagen { get; set; }
        public short Orden { get; set; }
        public bool Eliminar { get; set; }
        public string? Titulo { get; set; }
    }

    public class MultimediaWebViewModel
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "El enlace del video es obligatorio")]
        [Url(ErrorMessage = "Debe ser un enlace válido")]
        public string UrlEmbed { get; set; }
        public string? Titulo { get; set; }

        public short Orden { get; set; }
    }
}