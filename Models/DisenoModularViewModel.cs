using System.Collections.Generic;

namespace RedAJP.Models
{
    public class DisenoModularViewModel
    {
        public TemaVisualViewModel TemaVisual { get; set; } = new TemaVisualViewModel();
        public List<SeccionDisenoViewModel> Secciones { get; set; } = new List<SeccionDisenoViewModel>();
    }

    public class TemaVisualViewModel
    {
        public string ColorFondo { get; set; } = "#F4F7F9";
        public string ColorSuperficie { get; set; } = "#FFFFFF";
        public string ColorPrimario { get; set; } = "#0F172A";
        public string ColorAcento { get; set; } = "#E11D48";
        public string ColorTextoPrincipal { get; set; } = "#334155";
        public string ColorTextoSecundario { get; set; } = "#64748B";
        public string FuenteTitulos { get; set; } = "'Outfit', sans-serif";
        public string FuenteCuerpo { get; set; } = "'Inter', sans-serif";
        public string RadioBordes { get; set; } = "16px";
    }

    public class SeccionDisenoViewModel
    {
        public int Orden { get; set; }
        public string Identificador { get; set; } // Ej: "Navbar", "Hero"
        public string Vista { get; set; }         // Ej: "_NavbarFlotante"
    }
}