# Filter App · Para desarrolladores


WPF · .NET 10 · Windows 10/11

```bat
dotnet test tests\FilterApp.Core.Tests   :: pruebas
publish.cmd                              :: exe local (requiere .NET 10 Desktop Runtime)
release.cmd                              :: instalador + ZIP autocontenido para Releases
```

| Carpeta | Contenido |
|---|---|
| `src/FilterApp.Core` | Lógica: árbol de carpetas, tarjetas, copia/movimiento seguro, estado, sesiones |
| `src/FilterApp` | Ventana WPF, arrastrar/pegar, Explorador, Mini |
| `src/FilterApp/Preview` | Vista previa: PDFium, lector de Word propio, handlers de Office, WebView2, medios |
| `installer/` | `FilterApp.iss` (Inno Setup), `Instalar.cmd` / `Desinstalar.cmd` |

Las capturas de este README se hicieron con archivos de ejemplo inventados.

