namespace RedAJP.Models
{
    public class GestionSolicitudItem
    {
        public int Id_Solicitud { get; set; }
        public string Usuario { get; set; }
        public string Producto { get; set; }
        public DateTime Fecha { get; set; }
        public int Estatus { get; set; } // 1:Pendiente, 2:Aprobado, 3:Rechazado, 4:Comprado
        public string Imagen_Original_Url { get; set; }
        public string Imagen_Previo_Url { get; set; }
        public string Comentarios_Admin { get; set; }
    }
}