$css = [System.IO.File]::ReadAllText('wwwroot\css\estilos_pagina.css');
$match = [regex]::Match($css, '(:root\s*\{[^\}]*\})');
if ($match.Success) {
    Write-Host $match.Groups[1].Value.Substring(0, [math]::Min(1000, $match.Groups[1].Value.Length))
} else {
    Write-Host 'NO ROOT'
}
