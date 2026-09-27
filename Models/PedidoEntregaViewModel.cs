using System;

namespace RedAJP.Models
{
    public class PedidoEntregaViewModel
    {
        public int IdPedido { get; set; }
        public DateTime Fecha { get; set; }
        public decimal Total { get; set; }
        public int IdEstatus { get; set; }
        public string NombreEstatus { get; set; }
        public string CodigoEntrega { get; set; }
        public string NombreCliente { get; set; }
        public string ImagenPreviewUrl { get; set; }
        public string ResumenCompra { get; set; }
        public int CantidadArticulos { get; set; }
        public string RefPasarela { get; set; }
        public string Telefono { get; set; }
        public string Comentarios { get; set; }
        public string DetalleProductos { get; set; }
        public DateTime FechaEstimada { get; set; }

        public int MensajesSinLeer { get; set; }
    }
}