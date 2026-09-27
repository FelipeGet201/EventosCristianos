using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace RedAJP.Models
{
    public class TianguisIndexViewModel
    {
        public int? CategoriaActual { get; set; }
        public List<CategoriaTianguis> Categorias { get; set; } = new List<CategoriaTianguis>();
        public List<TianguisItemViewModel> Productos { get; set; } = new List<TianguisItemViewModel>();
    }

    public class CategoriaTianguis
    {
        public int IdCategoria { get; set; }
        public string sIdCategoria { get; set; }
        public string Nombre { get; set; }
    }

    public class TianguisItemViewModel
    {
        public int IdPublicacion { get; set; }
        public string sIdPublicacion { get; set; }
        public string Titulo { get; set; }
        public string Categoria { get; set; }
        public decimal PrecioBase { get; set; }
        public decimal PrecioActual { get; set; }
        public string TipoVenta { get; set; } // SUB o TIE
        public string Estado { get; set; } // ACT, RES, VEN, AGO, CAN
        public string ImagenUrl { get; set; }
    }

    public class TianguisDetalleViewModel
    {
        public int IdPublicacion { get; set; }
        public string sIdPublicacion { get; set; }
        public string Titulo { get; set; }
        public string Descripcion { get; set; }
        public decimal PrecioBase { get; set; }
        public decimal PrecioActual { get; set; }
        public string TipoVenta { get; set; }
        public string Estado { get; set; }
        public string Categoria { get; set; }
        public string VendedorAlias { get; set; }
        public DateTime? FechaExpiracion { get; set; }
        public List<string> ImagenesUrls { get; set; } = new List<string>();

        // Lógica de sesión y participación
        public bool EstaAutenticado { get; set; }
        public bool EsMio { get; set; }
        public bool Participando { get; set; }
        public bool YaComproAntes { get; set; }
        public decimal MiOferta { get; set; }
        public int? MiIdInteresado { get; set; }
        public string sMiIdInteresado { get; set; }
        public bool EsMejorOfertaMia { get; set; }
        public string MiEstadoTrato { get; set; } // INT, RES, COM

        // Para Venta Directa
        public List<TallaTianguis> Tallas { get; set; } = new List<TallaTianguis>();

        // Para el Dueño
        public List<InteresadoTianguis> Interesados { get; set; } = new List<InteresadoTianguis>();

        // Chat Activo
        public List<MensajeTianguis> Mensajes { get; set; } = new List<MensajeTianguis>();

        // Resultado Final
        public string CompradorFinalAlias { get; set; }
    }

    public class TallaTianguis
    {
        public int IdTalla { get; set; }
        public string NombreTalla { get; set; }
        public int Stock { get; set; }
    }

    public class InteresadoTianguis
    {
        public int IdInteresado { get; set; }
        public string sIdInteresado { get; set; }
        public string AliasAnonimo { get; set; }
        public decimal Oferta { get; set; }
        public string TallaSolicitada { get; set; }
        public int? Cantidad { get; set; }
        public string EstadoTrato { get; set; } // INT, RES, COM
        public bool EsMejorOferta { get; set; }
        public string UltimoMensaje { get; set; }
        public int? IdTalla { get; set; }
        public string Talla { get; set; }
    }

    public class MensajeTianguis
    {
        public string Autor { get; set; }
        public string Texto { get; set; }
        public DateTime Fecha { get; set; }
        public bool EsMio { get; set; }
    }

    public class TianguisCrearViewModel
    {
        [Required]
        public string TipoVenta { get; set; } // SUB o TIE
        [Required]
        public string Titulo { get; set; }
        [Required]
        public int IdCategoria { get; set; }
        [Required]
        public decimal PrecioBase { get; set; }

        // El signo de interrogación le dice a .NET: "Es opcional, puede ser nulo"
        public string? Descripcion { get; set; }
        public DateTime? FechaExpiracion { get; set; }
        public bool TieneTallas { get; set; }

        // El signo de interrogación evita la validación estricta oculta
        public string? TallasJson { get; set; }

        public List<IFormFile>? Fotos { get; set; }
    }

    // Clase auxiliar para deserializar el JSON de tallas al guardar
    public class TallaInput
    {
        public string Nombre { get; set; }
        public int Cantidad { get; set; }
    }
    // ==========================================
    // MODELOS PARA EL PANEL DE CONTROL
    // ==========================================
    public class TianguisPanelViewModel
    {
        public List<MisPublicacionesItem> MisPublicaciones { get; set; } = new List<MisPublicacionesItem>();
        public List<MisOfertasItem> MisOfertas { get; set; } = new List<MisOfertasItem>();
    }

    public class MisPublicacionesItem
    {
        public int IdPublicacion { get; set; }
        public string sIdPublicacion { get; set; }
        public string Titulo { get; set; }
        public decimal PrecioBase { get; set; }
        public string Estado { get; set; } // ACT, RES, VEN, AGO, CAN
        public string TipoVenta { get; set; }
        public int CantidadInteresados { get; set; }
        public DateTime FechaPublicacion { get; set; }
        public string ImagenUrl { get; set; }
    }

    public class MisOfertasItem
    {
        public int IdPublicacion { get; set; }
        public string sIdPublicacion { get; set; } 
        public int IdInteresado { get; set; }
        public string sIdInteresado { get; set; }
        public string Titulo { get; set; }
        public string VendedorAlias { get; set; }
        public decimal MiOferta { get; set; }
        public string EstadoTrato { get; set; } // INT, RES, COM
        public string EstadoPublicacion { get; set; }
        public string ImagenUrl { get; set; }
        public string TipoVenta { get; set; }
        public string Talla { get; set; }
        public int Cantidad { get; set; }
    }
}