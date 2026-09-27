using System.ComponentModel.DataAnnotations;

namespace RedAJP.Models
{
    // Para la tarjeta del catálogo
    public class ProductoFrontItem
    {
        public int Id_Producto { get; set; }
        public string Nombre { get; set; }
        public string Categoria { get; set; }
        public decimal Precio { get; set; }
        public bool Es_Personalizable { get; set; }
        public int Stock { get; set; }
        public string ImagenUrl { get; set; }
        public bool Cobrar_Comision_Extra { get; set; } = true;
    }

    // Para la vista de todos los productos con filtros
    public class CatalogoViewModel
    {
        public List<ProductoFrontItem> Productos { get; set; } = new List<ProductoFrontItem>();
        public List<dynamic> Categorias { get; set; } = new List<dynamic>();
        public int? CategoriaActual { get; set; }
    }

    public class TallaInfo
    {
        public string Nombre { get; set; }
        public int Stock { get; set; }
    }
     
    // Para ver un producto específico y elegir talla
    public class DetalleProductoViewModel
    {
        public int Id_Producto { get; set; }
        public string Nombre { get; set; }
        public string Descripcion { get; set; }
        public decimal Precio { get; set; }

        // Este será el stock TOTAL (suma de todas) para la vista inicial
        public int Stock { get; set; }

        public bool Es_Personalizable { get; set; }
        public string ImagenUrl { get; set; }

        // CAMBIO: En lugar de List<string>, usamos List<TallaInfo>
        public List<TallaInfo> TallasInfo { get; set; } = new List<TallaInfo>();

        public string TallaSeleccionada { get; set; }
        public int Cantidad { get; set; }
        public int? Id_Solicitud_Diseno { get; set; }
        public List<HiloPreguntaViewModel> Preguntas { get; set; } = new List<HiloPreguntaViewModel>();
        public List<string> GaleriaUrls { get; set; } = new List<string>();
    }
    public class HiloPreguntaViewModel
    {
        public int Id_Pregunta { get; set; }
        public string Mensaje { get; set; }
        public DateTime Fecha { get; set; }
        public bool Es_Propia { get; set; } // Para saber si el usuario actual fue el que preguntó
        public string NombreUsuario { get; set; }
        public List<RespuestaViewModel> Respuestas { get; set; } = new List<RespuestaViewModel>();
    }

    public class RespuestaViewModel
    {
        public string Mensaje { get; set; }
        public DateTime Fecha { get; set; }
        public bool Es_Admin { get; set; }
        public string NombreUsuario { get; set; }
    }
    public class ItemCarrito
    {
        public int IdDetalle { get; set; }
        public int IdProducto { get; set; }
        public string Nombre { get; set; }
        public int Cantidad { get; set; }
        public decimal Precio { get; set; }
        public bool EsPersonalizado { get; set; }
        public string Instrucciones { get; set; }
        public string ImagenUrl { get; set; }
        public bool Cobrar_Comision_Extra { get; set; } = true;
        public int StockMaximo { get; set; } // Para el atributo max="" del input
        public List<TallaInfo> TallasDisponibles { get; set; } = new List<TallaInfo>();


        // Helper para extraer la talla actual del texto "Talla: M"
        public string TallaActual
        {
            get
            {
                if (string.IsNullOrEmpty(Instrucciones)) return "";
                if (Instrucciones.Contains("Talla: ")) return Instrucciones.Replace("Talla: ", "").Trim();
                return "N/A";
            }
        }

        public decimal Subtotal => Cantidad * Precio;
    }

    public class CarritoViewModel
    {
        public int IdPedido { get; set; }
        public List<ItemCarrito> Items { get; set; } = new List<ItemCarrito>();
        public decimal Total => Items.Sum(x => x.Subtotal);
    }
}