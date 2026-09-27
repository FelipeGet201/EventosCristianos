using System.ComponentModel.DataAnnotations;

namespace RedAJP.Models
{
    // 1. Para la tarjeta en el listado (Index)
    public class RolItem
    {
        public int Id_Rol { get; set; }
        public string Nombre { get; set; }
        public string Descripcion { get; set; }
        public bool Activo { get; set; }
        public bool Es_Predeterminado { get; set; }
        public int UsuariosCount { get; set; }
        public string Icono { get; set; } // El ícono visual (ej: fa-shield)

        // Lista de nombres de módulos para los "badges" en la tarjeta
        public List<string> ModulosAcceso { get; set; } = new List<string>();
    }

    // 2. Para cada fila de la matriz de permisos (Lo que pediste)
    public class PermisoItem
    {
        public int Id_Modulo { get; set; }
        public string Nombre_Modulo { get; set; }

        // Los 5 checkboxes booleanos
        public bool P_Leer { get; set; }
        public bool P_Crear { get; set; }
        public bool P_Editar { get; set; }
        public bool P_Borrar { get; set; }
        public bool P_Admin { get; set; }
    }

    // 3. Para el formulario de Edición (Editor)
    public class EditorRolViewModel
    {
        public int Id_Rol { get; set; }

        [Required(ErrorMessage = "El nombre es obligatorio")]
        public string Nombre { get; set; }

        [Required(ErrorMessage = "La descripción es obligatoria")]
        public string Descripcion { get; set; }

        public bool Activo { get; set; }
        public bool Es_Predeterminado { get; set; } // <--- NUEVO CAMPO

        public string IconoSeleccionado { get; set; }

        public List<PermisoItem> Permisos { get; set; } = new List<PermisoItem>();
    }
}