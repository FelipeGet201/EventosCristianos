using RedAJP.Globales;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace RedAJP.Models
{
    public class RifaPersonalViewModel
    {
        public int IdRifa { get; set; }
        public string Token => Funciones.EncriptarId(IdRifa);

        public int IdUsuarioCreador { get; set; }
        [Required(ErrorMessage = "El título es obligatorio.")]
        public string Titulo { get; set; }
        public string Descripcion { get; set; }
        public decimal CostoBoleto { get; set; }
        public int MetaBoletos { get; set; }
        [Required]
        public DateTime FechaSorteo { get; set; }
        public string ImagenUrl { get; set; }
        public string Estado { get; set; }
        public string Banco { get; set; }
        public string CuentaClabe { get; set; }
        public string NumeroCuenta { get; set; }
        public string NumeroTarjeta { get; set; }
        public string TitularCuenta { get; set; }
        public string EnlaceGrupoWhatsapp { get; set; }
        public string QrGrupoWhatsappUrl { get; set; }
        public decimal InversionPremio { get; set; }
    }

    public class PromotorPersonalStat
    {
        public int? IdUsuario { get; set; }
        public string NombrePromotorExterno { get; set; } // NUEVO
        public string Nombre { get; set; }
        public int Asignados { get; set; }
        public int Vendidos { get; set; }
        public int Disponibles { get; set; }
        public int PorcentajeVenta => Asignados > 0 ? (Vendidos * 100) / Asignados : 0;
        public decimal DeudaTotal { get; set; }
        public decimal TotalPagado { get; set; }
        public decimal SaldoPendiente => DeudaTotal - TotalPagado;
    }
    public class PagoVendedorViewModel
    {
        public int IdPago { get; set; }
        public int IdRifa { get; set; }
        public int? IdUsuarioVendedor { get; set; }
        public string NombrePromotorExterno { get; set; }
        public decimal Monto { get; set; }
        public string MetodoPago { get; set; }
        public string ComprobanteUrl { get; set; }
        public string Estado { get; set; }
        public string Origen { get; set; }
        public string Nota { get; set; }
        public DateTime FechaPago { get; set; }
    }

    public class BoletoPersonalViewModel
    {
        public int IdBoleto { get; set; }
        public int IdRifa { get; set; }
        public string TokenRifa => Funciones.EncriptarId(IdRifa); // NUEVO
        public int Numero { get; set; }
        public string Estado { get; set; }

        public int? IdUsuarioAsignado { get; set; }
        public string NombrePromotorExterno { get; set; } // NUEVO
        public string NombrePromotor { get; set; }

        public string NombreComprador { get; set; }
        public string TelefonoComprador { get; set; }
        public string Comentarios { get; set; }
        public bool MarcadorPersonal { get; set; }
        public DateTime? FechaVenta { get; set; }
        public DateTime? FechaAsignacion { get; set; }
    }

    // =========================================================
    // 3. MODELO PARA EL TABLERO Y LA VISTA DE RESULTADOS
    // =========================================================
    public class TableroRifaPersonalViewModel
    {
        public RifaPersonalViewModel InfoRifa { get; set; }
        public List<BoletoPersonalViewModel> Boletos { get; set; }

        // Estadísticas calculadas en el controlador
        public int TotalBoletos { get; set; }
        public int BoletosVendidos { get; set; }
        public int BoletosDisponibles { get; set; }
        public decimal RecaudacionTotal { get; set; }

        // Para gestionar los envíos de WhatsApp
        public int ContactosConWhatsapp { get; set; }

        // Para mostrar al ganador en la vista de Resultados
        public string BoletoGanadorTexto { get; set; }
        public string NombreGanador { get; set; }
        public List<PromotorPersonalStat> Promotores { get; set; } = new List<PromotorPersonalStat>();
        public int BoletosLibresParaAsignar { get; set; }
    }

    // =========================================================
    // 4. MODELO PARA EL LOG DE NOTIFICACIONES (USO INTERNO TWILIO)
    // =========================================================
    public class NotificacionPersonalViewModel
    {
        public int IdNotificacion { get; set; }
        public string TelefonoDestino { get; set; }
        public string NombreCliente { get; set; }
        public string TipoMensaje { get; set; }
        public string EstadoEnvio { get; set; }
        public DateTime FechaEnvio { get; set; }
        public string RespuestaAPI { get; set; }
    }
}