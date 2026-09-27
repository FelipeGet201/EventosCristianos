namespace RedAJP.Models
{
    public class AlertaCorreoViewModel
    {
        public int IdAlerta { get; set; }
        public string ClaveEvento { get; set; } = "";
        public string Descripcion { get; set; } = "";
        public string CorreosDestino { get; set; } = "";
        public bool Activo { get; set; }
    }
}