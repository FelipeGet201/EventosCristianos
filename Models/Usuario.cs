using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RedAJP.Models
{
    public class Usuario
    {
        // ID: Lo necesitamos para el WHERE Id_Usuario = @id
        public int Id_Usuario { get; set; }

        // Validaciones para el Formulario (Aunque uses SQL directo, esto ayuda al HTML)
        [Required(ErrorMessage = "El nombre es obligatorio")]
        [Display(Name = "Nombre Completo")]
        public string NombreCompleto { get; set; }

        [Required(ErrorMessage = "El usuario es obligatorio")]
        [Display(Name = "Usuario")]
        public string Nombre_Usuario { get; set; }

        [Required(ErrorMessage = "El correo es obligatorio")]
        [EmailAddress(ErrorMessage = "Formato de correo inválido")]
        public string Email { get; set; }

        [NotMapped]
        public bool CambiarPassword { get; set; }

        [NotMapped]
        public string? CurrentPassword { get; set; }

        [NotMapped]
        public string? NewPassword { get; set; }

        [NotMapped]
        public string? ConfirmPassword { get; set; }

        // El teléfono puede ser nulo en la BD, así que ponemos '?'
        [Display(Name = "Teléfono")]
        public string? Telefono { get; set; }

        // --- CAMPOS DE SISTEMA (Se leen pero no se editan en Perfil) ---

        // La contraseña la mantienes aquí para el Login, pero en Perfil puede ir vacía
        public string? PasswordHash { get; set; }

        public int Id_Rol { get; set; }

        public bool Activo { get; set; } = true;

        public DateTime FechaRegistro { get; set; } = DateTime.UtcNow;

        public string Email_Temporal { get; set; }
    }
}