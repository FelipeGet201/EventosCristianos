using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;

namespace RedAJP.Models
{
    public class ImportacionMiembrosViewModel
    {
        public int Id_Grupo { get; set; }
        public string NombreGrupo { get; set; }

        public List<ItemMiembroImportacion> MiembrosNuevos { get; set; } = new List<ItemMiembroImportacion>();
        public List<UsuarioComboItem> ListaUsuariosSistema { get; set; } = new List<UsuarioComboItem>();
    }

    public class ItemMiembroImportacion
    {
        public int? IdUsuario { get; set; }
        public string Nombre { get; set; }
        public string Telefono { get; set; }
    }

    public class UsuarioComboItem
    {
        public int Id { get; set; }
        public string Nombre { get; set; }
        public string Telefono { get; set; }
        public string Email { get; set; }
        public bool Verificado { get; set; } // <--- NUEVO CAMPO
    }

    // ==========================================
    // 1. MODELO PARA EL LISTADO PRINCIPAL (INDEX)
    // ==========================================
    public class GrupoIndexViewModel
    {
        public int Id_Grupo { get; set; }
        public string Nombre { get; set; }
        public string Descripcion { get; set; }
        public DateTime FechaCreacion { get; set; }

        // Estadísticas
        public int TotalMiembros { get; set; }
        public int Vinculados { get; set; }
        public int Pendientes { get; set; }
    }

    // ==========================================
    // 2. MODELO PARA CREAR/EDITAR GRUPO
    // ==========================================
    public class GrupoFormViewModel
    {
        public int Id_Grupo { get; set; }
        public string Nombre { get; set; }
        public string Descripcion { get; set; }
    }

    // ==========================================
    // 3. MODELO PARA VER DETALLE Y MIEMBROS
    // ==========================================
    public class GrupoDetalleViewModel
    {
        public int Id_Grupo { get; set; }
        public string Nombre { get; set; }
        public string Descripcion { get; set; }

        // Para controlar el borrado
        public int EventosAsignados { get; set; }
        public bool EsBorrable => EventosAsignados == 0;

        public List<MiembroDetalleItem> Miembros { get; set; } = new List<MiembroDetalleItem>();
    }

    public class MiembroDetalleItem
    {
        public int Id_Miembro { get; set; }
        public string TelefonoOrigen { get; set; }
        public string NombreImportado { get; set; }
        public int? Id_Usuario { get; set; }
        public string NombreSistema { get; set; }
        public bool EstaVinculado => Id_Usuario.HasValue;
    }

    // Clase auxiliar para recibir el JSON en el controlador
    public class ItemMiembroJson
    {
        public int IdUsuario { get; set; }
        public string Telefono { get; set; }
        public string Nombre { get; set; }
    }
}