using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace RedAJP.Models
{
    public class ComunidadIndexViewModel
    {
        public int Id_Mensaje { get; set; }
        public string Tipo_Mensaje { get; set; }
        public string Destinatario { get; set; }
        public string Estado { get; set; }
        public string Contenido { get; set; }
        public DateTime Fecha_Creacion { get; set; }
        public DateTime Fecha_Expiracion { get; set; }
        public bool Editado { get; set; }
        public bool Oracion_Atendida { get; set; }
        public bool Es_Anonimo { get; set; }
        public string NombreAutor { get; set; }
        public bool EsMio { get; set; }
        public bool EsLeido { get; set; }
        public int TotalReacciones { get; set; }
        public bool UsuarioReacciono { get; set; }
    }
    public class TipoMensajeItem
    {
        public string Clave { get; set; }
        public string Nombre { get; set; }
        public string Icono { get; set; }
        public bool Solo_Directiva { get; set; }
    }
    public class ComunidadFormViewModel
    {
        public int Id_Mensaje { get; set; }

        [Required(ErrorMessage = "El tipo de mensaje es obligatorio")]
        public string Tipo_Mensaje { get; set; }

        [Required(ErrorMessage = "El destinatario es obligatorio")]
        public string Destinatario { get; set; }

        public bool Es_Anonimo { get; set; }

        [Required(ErrorMessage = "El contenido es obligatorio")]
        public string Contenido { get; set; }

        [Required(ErrorMessage = "La fecha de expiración es obligatoria")]
        public DateTime Fecha_Expiracion { get; set; }
        public bool EsDirectivo { get; set; }
    }

    public class ComunidadDetalleViewModel
    {
        public int Id_Mensaje { get; set; }
        public int Id_Usuario_Autor { get; set; }
        public string Tipo_Mensaje { get; set; }
        public string Destinatario { get; set; }
        public string Contenido { get; set; }
        public string Estado { get; set; }
        public DateTime Fecha_Creacion { get; set; }
        public bool Es_Anonimo { get; set; }
        public bool Editado { get; set; }
        public bool Oracion_Atendida { get; set; }
        public string Motivo_Rechazo { get; set; }
        public string NombreAutor { get; set; }
        public int TotalReacciones { get; set; }
        public bool UsuarioReacciono { get; set; }
        public List<ComunidadRespuestaItem> Respuestas { get; set; } = new List<ComunidadRespuestaItem>();
    }

    public class ComunidadRespuestaItem
    {
        public int Id_Respuesta { get; set; }
        public string Contenido { get; set; }
        public DateTime Fecha_Creacion { get; set; }
        public string Nombre_Responde { get; set; }
        public int? Id_Usuario_Hilo { get; set; }
        public bool EsDeDirectiva { get; set; }
    }

    public class ComunidadAuditoriaViewModel
    {
        public int Id_Auditoria { get; set; }
        public string Contenido_Anterior { get; set; }
        public string Contenido_Nuevo { get; set; }
        public DateTime Fecha_Edicion { get; set; }
        public string NombreEditor { get; set; }
    }
}