using System.Collections.Generic;

namespace RedAJP.Models
{
    public class CheckoutViewModel
    {
        // Datos del Usuario (pre-llenados)
        public string TelefonoContacto { get; set; }
        public string Comentarios { get; set; }

        // Resumen de la compra (Solo lectura)
        public List<ItemCarrito> ItemsResumen { get; set; } = new List<ItemCarrito>();
        public decimal TotalPagar { get; set; }
        public int CantidadArticulos { get; set; }
        public string CodigoCupon { get; set; }
    }
}