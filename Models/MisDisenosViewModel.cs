namespace RedAJP.Models
{
    public class MisDisenosItem
    {
        public int Id_Solicitud { get; set; }
        public int Id_Producto_Base { get; set; }
        public string Nombre_Producto { get; set; }
        public string Imagen_Previo_Url { get; set; }
        public DateTime Fecha { get; set; }
        public int Id_Estatus { get; set; }
        public string Comentarios_Admin { get; set; }

        // NUEVO: Para saber si aún se puede fabricar
        public int StockBase { get; set; }
    }

    public class MisDisenosViewModel
    {
        public List<MisDisenosItem> Lista { get; set; } = new List<MisDisenosItem>();
    }
}