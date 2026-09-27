using System;
using System.Collections.Generic;
using System.Linq;

namespace RedAJP.Models
{
    public class RifaIndexItem
    {
        public int Id_Rifa { get; set; }
        public string IdRifaEncriptado { get; set; }
        public string Titulo { get; set; }
        public string Estado { get; set; }
        public decimal CostoBoleto { get; set; }
        public int MetaBoletos { get; set; }
        public int Vendidos { get; set; }
        public string ImagenUrl { get; set; }
        public DateTime FechaSorteo { get; set; }

        public string FechaSorteoStr => FechaSorteo.ToString("dd MMM yyyy");
        public string FechaSorteoIso => FechaSorteo.ToString("yyyy-MM-ddTHH:mm:ss");
        public decimal Recaudado => Vendidos * CostoBoleto;
        public int Porcentaje => MetaBoletos > 0 ? (Vendidos * 100) / MetaBoletos : 0;

        public int? NumeroGanador { get; set; }
        public string NombreGanador { get; set; }
        public bool VisiblePublico { get; set; } = true;
    }

    public class NuevaRifaViewModel
    {
        public string Titulo { get; set; }
        public string Descripcion { get; set; }
        public string ImagenUrl { get; set; }
        public decimal Costo { get; set; }
        public int MetaBoletos { get; set; }
        public DateTime FechaSorteo { get; set; }
    }

    public class RifaPublicaViewModel
    {
        public int Id_Rifa { get; set; }
        public string IdRifaEncriptado { get; set; }
        public string Token { get; set; }
        public int IdUsuarioCreador { get; set; }

        public string Titulo { get; set; }
        public string Descripcion { get; set; }
        public decimal CostoBoleto { get; set; }
        public int MetaBoletos { get; set; }
        public DateTime FechaSorteo { get; set; }
        public string Estado { get; set; }
        public string ImagenUrl { get; set; }

        public decimal InversionPremio { get; set; }

        public string Banco { get; set; }
        public string CuentaClabe { get; set; }
        public string NumeroCuenta { get; set; }
        public string NumeroTarjeta { get; set; }
        public string TitularCuenta { get; set; }
        public string EnlaceCompra { get; set; }
        public string QrEnlaceCompraUrl { get; set; }

        public bool VisiblePublico { get; set; } = true;
        public List<int> NumerosDisponibles { get; set; } = new List<int>();
        public List<int> EncuestasAsociadas { get; set; } = new List<int>();
    }

    public class EstatusPedidoViewModel
    {
        public int IdVenta { get; set; }
        public string TituloRifa { get; set; }
        public string Estado { get; set; }
        public decimal Total { get; set; }
        public string RefStripe { get; set; }
        public string Token { get; set; }
        public string Email { get; set; }
        public decimal CostoUnitario { get; set; }
        public List<int> Boletos { get; set; }
    }

    public class CompraRifaViewModel
    {
        public int IdRifa { get; set; }
        public string TituloRifa { get; set; }
        public decimal CostoUnitario { get; set; }
        public int CantidadSolicitada { get; set; }
        public string IdsBoletosStr { get; set; }
        public string Nombre { get; set; }
        public string Telefono { get; set; }
        public string Email { get; set; }
        public string Ciudad { get; set; }
    }

    public class MisBoletosViewModel
    {
        public List<BoletoItem> MisCompras { get; set; } = new List<BoletoItem>();
        public List<BoletoGestionItem> MisAsignaciones { get; set; } = new List<BoletoGestionItem>();
        public List<PagoPromotorRifaViewModel> MisPagos { get; set; } = new List<PagoPromotorRifaViewModel>();

        public decimal DeudaTotal => MisAsignaciones
            .Where(x => x.Estado == "Vendido_Sin_Pagar")
            .Sum(x => x.Costo);
    }

    public class BoletoGestionItem
    {
        public int IdBoleto { get; set; }
        public int Numero { get; set; }
        public decimal Costo { get; set; }
        public string Estado { get; set; }
        public string NombreCliente { get; set; }
        public string TelefonoCliente { get; set; }

        public int IdRifa { get; set; }
        public string IdRifaEncriptado { get; set; }
        public string TituloRifa { get; set; }
        public string EstadoRifa { get; set; }
        public string Comentarios { get; set; }
    }

    public class GestionTableroViewModel
    {
        public RifaPublicaViewModel InfoRifa { get; set; } = new RifaPublicaViewModel();
        public EstadisticasRifa Stats { get; set; } = new EstadisticasRifa();
        public List<BoletoItem> BoletosRelevantes { get; set; } = new List<BoletoItem>();
        public List<PromotorStatItem> Promotores { get; set; } = new List<PromotorStatItem>();

        public List<PagoPromotorRifaViewModel> PagosPendientes { get; set; } = new List<PagoPromotorRifaViewModel>();
        public int BoletosLibresParaAsignar { get; set; }

        public List<BoletoItem> Boletos { get; set; } = new List<BoletoItem>();
        public List<PagoPromotorRifaViewModel> PagosRechazados { get; set; } = new List<PagoPromotorRifaViewModel>();
    }

    public class EstadisticasRifa
    {
        public int TotalBoletos { get; set; }
        public int Pagados { get; set; }
        public int PendientesPago { get; set; }
        public int EnMano { get; set; }
        public decimal DineroRecaudado { get; set; }
        public decimal DineroPorCobrar { get; set; }
    }

    public class PagoPromotorRifaViewModel
    {
        public int IdPago { get; set; }
        public int IdRifa { get; set; }
        public string IdRifaEncriptado { get; set; }
        public int? IdUsuarioVendedor { get; set; }
        public string NombrePromotorExterno { get; set; }
        public decimal Monto { get; set; }
        public string MetodoPago { get; set; }
        public string ComprobanteUrl { get; set; }
        public string Estado { get; set; }
        public string Origen { get; set; }
        public string Nota { get; set; }
        public DateTime FechaPago { get; set; }
        public string BoletosPagados { get; set; }
    }

    public class PromotorStatItem
    {
        public int? IdUsuario { get; set; }
        public string NombrePromotorExterno { get; set; }
        public string Nombre { get; set; }

        public int Asignados { get; set; }
        public int VendidosPagados { get; set; }
        public int VendidosSinPagar { get; set; }
        public int Restantes { get; set; }
        public decimal DeudaActual { get; set; }
        public decimal DeudaTotal => (VendidosPagados + VendidosSinPagar) * CostoBoletoUnitario;
        public decimal TotalPagado { get; set; }
        public decimal SaldoPendiente => (VendidosSinPagar * CostoBoletoUnitario) - TotalPagado;

        public decimal CostoBoletoUnitario { get; set; }
        public int PorcentajeVenta => Asignados > 0 ? (int)((VendidosPagados + VendidosSinPagar) * 100 / Asignados) : 0;
    }

    public class BoletoItem
    {
        public int Id_Boleto { get; set; }
        public int Id_Rifa { get; set; }
        public string IdRifaEncriptado { get; set; }
        public int Numero { get; set; }
        public string Estado { get; set; }

        public int? IdUsuarioAsignado { get; set; }
        public string NombrePromotorExterno { get; set; }

        public string Comprador { get; set; }
        public string Telefono { get; set; }
        public string Comentarios { get; set; }

        public bool MarcadorPersonal { get; set; }

        public DateTime? FechaVenta { get; set; }
        public DateTime? FechaAsignacion { get; set; }
    }

    public class RifasIndexViewModel
    {
        public List<RifaIndexItem> Rifas { get; set; } = new List<RifaIndexItem>();
        public int TotalComprados { get; set; }
        public int TotalPorVender { get; set; }
    }

    // =========================================================
    // IMPRESIÓN DE BOLETOS — Tómbola física
    // =========================================================

    public class BoletoImpresionItem
    {
        public int Id_Boleto { get; set; }
        public int Numero { get; set; }
        public string NombreTitular { get; set; }
        public string Telefono { get; set; }
    }

    public class ImpresionBoletosViewModel
    {
        public RifaPublicaViewModel InfoRifa { get; set; } = new RifaPublicaViewModel();
        public List<BoletoImpresionItem> Boletos { get; set; } = new List<BoletoImpresionItem>();
        public string IdRifaEncriptado { get; set; }
    }

    // =========================================================
    // SORTEO FORMAL CON DIRECTIVOS AJP
    // =========================================================

    public class ConfirmacionSorteoItem
    {
        public int IdUsuario { get; set; }
        public string NombreUsuario { get; set; }
        public DateTime FechaConfirmacion { get; set; }
        public int IdBoletoCandidato { get; set; }
    }

    public class SorteoDirectivosViewModel
    {
        public RifaPublicaViewModel InfoRifa { get; set; } = new RifaPublicaViewModel();
        public string IdRifaEncriptado { get; set; }

        // Estado del proceso
        public string Modalidad { get; set; }               // null, 'MANUAL', 'PLATAFORMA'
        public bool YaTieneGanador { get; set; }
        public bool HayProcesoEnCurso { get; set; }

        // Candidato actual (cuando HayProcesoEnCurso = true)
        public int IdBoletoCandidato { get; set; }
        public int NumeroCandidato { get; set; }
        public string NombreCandidato { get; set; }

        // Confirmaciones acumuladas
        public List<ConfirmacionSorteoItem> Confirmaciones { get; set; } = new List<ConfirmacionSorteoItem>();
        public int TotalConfirmaciones => Confirmaciones.Count;
        public bool UsuarioActualYaConfirmo { get; set; }

        // Ganador final (cuando YaTieneGanador = true)
        public int NumeroGanador { get; set; }
        public string NombreGanador { get; set; }
        public DateTime? FechaConfirmadoGanador { get; set; }

        // Listado de boletos disponibles para seleccionar (solo cuando no hay proceso en curso)
        public List<BoletoItem> BoletosDisponibles { get; set; } = new List<BoletoItem>();
    }
}