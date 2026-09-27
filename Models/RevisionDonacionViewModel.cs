namespace RedAJP.Models
{
    public class RevisionDonacionViewModel
    {
        public int IdDonacion { get; set; }
        public DateTime Fecha { get; set; }
        public string Donante { get; set; }
        public string Email { get; set; }
        public decimal MontoOriginal { get; set; }
        public decimal? MontoValidado { get; set; }
        public bool Requiere2daValidacion { get; set; }
        public string PreRevisor { get; set; }
        public string Notas { get; set; }
        public int? IdUsuarioPreRevisor { get; set; }
        public string ComentarioDonante { get; set; }
        public string FolioBancario { get; set; }
    }
}