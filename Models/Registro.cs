namespace RedAJP.Models
{
    public class Registro
    {
        public string NombreCompleto { get; set; }
        public string Nombre_Usuario { get; set; }
        public string Email { get; set; }
        public string Password { get; set; }
        public string ConfirmPassword { get; set; }

        public string Telefono { get; set; }
        public DateTime Fecha_Nacimiento { get; set; }
        public string Genero { get; set; }
    }
}