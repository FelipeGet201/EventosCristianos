using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System;
using System.Threading.Tasks;

namespace RedAJP.ViewComponents
{
    public class SolicitudesBadge : ViewComponent
    {
        private readonly IConfiguration _config;

        public SolicitudesBadge(IConfiguration config)
        {
            _config = config;
        }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            int total = 0;

            if (User.Identity.IsAuthenticated)
            {
                try
                {
                    using var conexion = new NpgsqlConnection(_config.GetConnectionString("MiConexion"));
                    await conexion.OpenAsync();

                    var sql = @"SELECT COUNT(*) FROM ""Tienda_Solicitudes_Diseno"" WHERE ""Id_Estatus_Diseno"" = 1";
                    using var cmd = new NpgsqlCommand(sql, conexion);
                    total = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }
                catch { }
            }

            return Content(total.ToString());
        }

    }
}