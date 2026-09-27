namespace RedAJP.Models
{
    public class PedidoDetalleViewModel
    {
        // Cabecera del Pedido
        public int IdPedido { get; set; }
        public DateTime Fecha { get; set; }
        public decimal Total { get; set; }
        public int IdEstatus { get; set; }
        public string NombreEstatus { get; set; }
        public string CodigoEntrega { get; set; } // El código secreto
        public string NombreCliente { get; set; } // Para el Admin
        public string EmailCliente { get; set; }  // Para el Admin
        public string IdPagoMP { get; set; }
        public string Telefono { get; set; }
        public string Comentarios { get; set; }
        public DateTime FechaEstimada { get; set; }

        // Lista de Productos
        public List<ItemDetalle> Productos { get; set; } = new List<ItemDetalle>();
        public string CodigoCupon { get; set; }
        public decimal MontoDescuento { get; set; }
        public List<MensajePedidoViewModel> Mensajes { get; set; } = new List<MensajePedidoViewModel>();

        public int MensajesSinLeer { get; set; }
    }

    public class ItemDetalle
    {
        public string Nombre { get; set; }
        public int Cantidad { get; set; }
        public decimal Precio { get; set; }
        public string Especificaciones { get; set; } // Talla, etc.
        public bool EsPersonalizado { get; set; }
        public string ImagenUrl { get; set; }
        public decimal Subtotal => Cantidad * Precio;
    }

    public class MensajePedidoViewModel
    {
        public int Id_Mensaje { get; set; }
        public string NombreUsuario { get; set; }
        public string Mensaje { get; set; }
        public DateTime Fecha { get; set; }
        public bool Es_Admin { get; set; }
        public bool Es_Propio { get; set; } // Para saber si alinear la burbuja a la derecha
    }
}
