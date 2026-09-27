using Npgsql;
using Microsoft.Extensions.Configuration;
using System;
using System.Threading.Tasks;

namespace RedAJP.Services
{
    // =========================================================
    // 1. LA INTERFAZ (El contrato actualizado)
    // =========================================================
    public interface ISolicitudesService
    {
        // Contador 1: Transferencias pendientes de revisión (Admin)
        Task<int> ObtenerContadorTransferenciasPendAsync();
    }

    // =========================================================
    // 2. LA CLASE (La implementación)
    // =========================================================
    public class SolicitudesService : ISolicitudesService
    {
        private readonly IConfiguration _config;
        private readonly string _connectionString;

        public SolicitudesService(IConfiguration config)
        {
            _config = config;
            _connectionString = _config.GetConnectionString("MiConexion");
        }

        // --- 1. TRANSFERENCIAS PENDIENTES DE REVISIÓN (ADMIN) ---
        public async Task<int> ObtenerContadorTransferenciasPendAsync()
        {
            // 'review' = El usuario ya subió foto y espera aprobación del Admin
            const string sql = @"SELECT COUNT(*) FROM ""Eventos_D_Transacciones"" WHERE ""Estatus_Pago"" = 'review'";

            // Convertimos a Int32 porque COUNT devuelve Int64 (long) en PostgreSQL
            return Convert.ToInt32(await EjecutarEscalarAsync(sql));
        }

        // =========================================================
        // MÉTODO PRIVADO AUXILIAR (Para no repetir código de conexión)
        // =========================================================
        private async Task<int> EjecutarEscalarAsync(string sql, Action<NpgsqlCommand> agregarParametros = null)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_connectionString))
                {
                    await conexion.OpenAsync();
                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        // Si hay parámetros (como el ID de usuario), los agregamos aquí
                        agregarParametros?.Invoke(cmd);

                        var resultado = await cmd.ExecuteScalarAsync();

                        if (resultado != null && resultado != DBNull.Value)
                        {
                            return Convert.ToInt32(resultado);
                        }
                    }
                }
            }
            catch
            {
                // Si falla la BD, devolvemos 0 para que la página siga funcionando
                return 0;
            }
            return 0;
        }
    }
}