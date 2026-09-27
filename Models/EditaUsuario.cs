using System.ComponentModel.DataAnnotations;

namespace RedAJP.Models
{
    public class EditaUsuario
    {
        public int Id_Usuario { get; set; }

        [Required(ErrorMessage = "El nombre es requerido")]
        public string NombreCompleto { get; set; }

        [Required(ErrorMessage = "El email es requerido")]
        [EmailAddress]
        public string Email { get; set; }

        [Required(ErrorMessage = "El usuario es requerido")]
        public string Nombre_Usuario { get; set; }
        public string Telefono { get; set; }

        public int Id_Rol { get; set; }
        public string Nombre_Rol { get; set; } // Para mostrar en tabla

        public bool Activo { get; set; }
        public bool Email_Verificado { get; set; }

        // Indica si tiene historial (excluyendo su registro inicial)
        public bool TieneHistorial { get; set; }
    }
}