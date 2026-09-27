using static RedAJP.Globales.Parametros;

namespace RedAJP.Globales
{
    public static class Parametros
    {
        // CLASES WRAPPER: Al tener constructor 'internal', nadie fuera de este 
        // namespace puede inventar acciones o módulos nuevos.
        public class AccionBitacora
        {
            public string Valor { get; }
            internal AccionBitacora(string v) => Valor = v;
            public override string ToString() => Valor;

            // Permite comparar Accion == Accion de forma sencilla
            public static bool operator ==(AccionBitacora a, AccionBitacora b) => a?.Valor == b?.Valor;
            public static bool operator !=(AccionBitacora a, AccionBitacora b) => !(a == b);
        }

        public class Modulo
        {
            public string Valor { get; }
            internal Modulo(string v) => Valor = v;

            public override string ToString() => Valor;

            public static implicit operator string(Modulo m) => m.Valor;

            public static bool operator ==(Modulo a, Modulo b) => a?.Valor == b?.Valor;
            public static bool operator !=(Modulo a, Modulo b) => !(a == b);
        }

        public class Permiso
        {
            public string Valor { get; }
            internal Permiso(string v) => Valor = v;
            public override string ToString() => Valor;

            public static bool operator ==(Permiso a, Permiso b) => a?.Valor == b?.Valor;
            public static bool operator !=(Permiso a, Permiso b) => !(a == b);
        }

        public static class AccionesBitacora
        {
            public static readonly AccionBitacora Leer = new AccionBitacora("Leer");
            public static readonly AccionBitacora Crear = new AccionBitacora("Crear");
            public static readonly AccionBitacora Editar = new AccionBitacora("Editar");
            public static readonly AccionBitacora Borrar = new AccionBitacora("Borrar");
            public static readonly AccionBitacora Error = new AccionBitacora("Error");
            public static readonly AccionBitacora PagoTienda = new AccionBitacora("PagoTienda");
            public static readonly AccionBitacora PagoTiendaConfirmado = new AccionBitacora("PagoTiendaConfirmado");
            public static readonly AccionBitacora PedidoEntregado = new AccionBitacora("PedidoEntregado");
            public static readonly AccionBitacora VerificaCorreo = new AccionBitacora("VerificaCorreo");
            public static readonly AccionBitacora RegistroUsuario = new AccionBitacora("RegistroUsuario");
            public static readonly AccionBitacora ReenvioCorreoRegistro = new AccionBitacora("ReenvioCorreoRegistro");
            public static readonly AccionBitacora SolicitudDiseño = new AccionBitacora("SolicitudDiseño");
            public static readonly AccionBitacora CambiaContraseña = new AccionBitacora("CambiaContraseña");
            public static readonly AccionBitacora EliminaDiseño = new AccionBitacora("EliminaDiseño");
            public static readonly AccionBitacora SolicitaCambioCorreo = new AccionBitacora("SolicitaCambioCorreo");
            public static readonly AccionBitacora Exportar = new AccionBitacora("Exportar");
        }

        public static class Permisos
        {
            public static readonly Permiso Leer = new Permiso("L");
            public static readonly Permiso Crear = new Permiso("C");
            public static readonly Permiso Editar = new Permiso("E");
            public static readonly Permiso Borrar = new Permiso("B");
            public static readonly Permiso Admin = new Permiso("A");
        }

        public static class Modulos
        {
            public static readonly Modulo Caja = new Modulo("Fin_Caja");
            public static readonly Modulo Rifas = new Modulo("Fin_Rifas");
            public static readonly Modulo RifasPersonales = new Modulo("RifasPersonales");
            public static readonly Modulo Usuarios = new Modulo("Seguridad_Usuarios");
            public static readonly Modulo Roles = new Modulo("Seguridad_Roles");
            public static readonly Modulo Eventos = new Modulo("Inst_Eventos");
            public static readonly Modulo Reuniones = new Modulo("Inst_Reuniones");
            public static readonly Modulo Material = new Modulo("Rec_Materiales");
            public static readonly Modulo Tienda = new Modulo("Tienda");
            public static readonly Modulo Tianguis = new Modulo("Tianguis");
            public static readonly Modulo Entregas = new Modulo("Tienda_Entregas");
            public static readonly Modulo TiendaConfig = new Modulo("Tienda_Config");
            public static readonly Modulo Visitas = new Modulo("Visitas");
            public static readonly Modulo Registro = new Modulo("Registro");
            public static readonly Modulo Grupos = new Modulo("Grupos");
            public static readonly Modulo Encuestas = new Modulo("Encuestas");
            public static readonly Modulo Cupones = new Modulo("Cupones");
            public static readonly Modulo GeneradorQR = new Modulo("GeneradorQR");
            public static readonly Modulo AlertasCorreos = new Modulo("AlertasCorreos");
            public static readonly Modulo Biblioteca = new Modulo("Biblioteca");
            public static readonly Modulo Colores = new Modulo("Colores");
            public static readonly Modulo Comunidad = new Modulo("Comunidad");
            public static readonly Modulo Devoluciones = new Modulo("Devoluciones");
            public static readonly Modulo CuentasStripe = new Modulo("CuentasStripe");
            public static readonly Modulo Estadisticas = new Modulo("Estadisticas");
        }

        public static readonly List<string> IconosPermitidos = new List<string>
        {
            // Seguridad / Admin
            "fa-user-shield", "fa-shield-halved", "fa-lock", "fa-key", "fa-id-badge",
            // Finanzas
            "fa-coins", "fa-sack-dollar", "fa-credit-card", "fa-file-invoice-dollar", "fa-chart-line",
            // Personas
            "fa-user", "fa-users", "fa-user-tie", "fa-people-group", "fa-child-reaching",
            // Sistema / General
            "fa-gear", "fa-database", "fa-server", "fa-network-wired", "fa-sitemap",
            // Náutico / Tema
            "fa-anchor", "fa-fish", "fa-water", "fa-ship", "fa-compass",
            // Extras
            "fa-book-bible", "fa-star", "fa-crown", "fa-bullhorn", "fa-print"
        };
        public struct TipoMensaje
        {
            public const string Exito = "exito";
            public const string Error = "error";
            public const string Alerta = "alerta";
            public const string Info = "info";
        }
        public static class EstatusTienda
        {
            public const int Carrito = 0;
            public const int PendienteAuth = 10;
            public const int Aprobado = 20;
            public const int EnProduccion = 30;
            public const int Listo = 40;
            public const int Entregado = 50;
            public const int Rechazado = 99;
        }
    }
}