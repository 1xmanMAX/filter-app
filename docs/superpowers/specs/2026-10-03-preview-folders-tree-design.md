# Filter App 1.2 — Vista previa, carpetas enteras y tarjetas multinivel

Fecha: 2026-10-03 · Estado: aprobado por el usuario ("impleméntalo todo según tu criterio de inicio a fin").

## Objetivo

Que Filter App sirva para reorganizar discos completos, no solo una lista de nombres:

1. **Ver** cada archivo al instante (PDF, Word, Excel, imágenes, texto, video…) sin abrir otra app.
2. **Meter carpetas enteras** (cientos de archivos con subcarpetas).
3. **Destino multinivel**: un árbol de carpetas dentro de carpetas con nombres de archivo, que se puede
   crear de golpe (pegando una estructura) o sobre la marcha (al soltar un archivo).
4. **Hacerlo rápido**: arrastrar y soltar sigue igual; además, todo se puede hacer con teclado.

## Decisiones

| Tema | Decisión | Por qué |
|---|---|---|
| Copiar o mover | Interruptor **Mover** por sesión, apagado por defecto | Reorganizar un disco sin duplicar espacio, pero el comportamiento seguro de siempre sigue por defecto |
| Mover en el mismo disco | `File.Move` (renombrado instantáneo, atómico) | 1000 archivos en segundos |
| Mover a otro disco | Copia con `.partial` + borrar original | Nunca deja archivos truncados |
| Deshacer un movido | Devuelve el archivo a su ruta original y a Pendientes | Corregir un error sin buscar el archivo |
| Árbol | Cada carpeta tiene `Folders`, `Cards` (nombres) y `Files` (archivos que conservan su nombre) | El modelo actual de tarjeta se reutiliza tal cual; las pruebas existentes siguen valiendo |
| Navegación | Ruta (breadcrumb) + tarjetas del nivel actual; las carpetas se abren con doble clic o al **mantener un archivo encima** 0,7 s | Igual que el Explorador; sin perder espacio con un TreeView |
| Soltar en carpeta | El archivo entra con su nombre original (`foto.jpg`, si existe `foto (2).jpg`) | Lo natural al reorganizar |
| Soltar una carpeta del Explorador sobre una carpeta-tarjeta | Entra entera con sus subcarpetas | Como en el Explorador |
| Soltar una carpeta en Pendientes | Se leen todos sus archivos (recursivo, en segundo plano); la lista se agrupa por subcarpeta | Repartir archivo por archivo |
| Barra rápida | Escribe para buscar en todo el árbol; **Enter** envía el pendiente seleccionado al resultado. `Clientes/Juan/DNI` crea carpetas y el nombre; `Clientes/Juan/` crea carpetas y conserva el nombre | Teclado: ver → escribir → Enter → siguiente |
| + Nombres | Acepta estructura: `Carpeta/` o `A/B/Nombre`, y sangría para anidar. Botón para copiar la estructura de carpetas de una carpeta existente | Pegar una estructura completa de una vez |
| Vista previa | Panel junto a Pendientes, redimensionable y ocultable | |
| Motor de vista previa | Imágenes y texto: WPF nativo. PDF, SVG, audio y video: WebView2 (motor de Edge, ya está en Windows 11). Word, Excel, PowerPoint, Outlook…: los *preview handlers* de Windows (los mismos del panel de vista previa del Explorador). Sin handler: `.docx` como texto; resto: miniatura del sistema + datos + **Abrir** | Rápido, sin licencias, y el mismo resultado que el Explorador |
| Deshacer global | **Ctrl+Z** deshace la última colocación | |

## Fuera de alcance

- Renombrar en disco una carpeta ya creada al renombrar su tarjeta (solo afecta a lo que se coloque después).
- Borrar las carpetas de origen que quedan vacías tras mover.
- Editar documentos dentro de la app (solo se ven).

## Pruebas

- Core (xUnit): parser de estructura, árbol (rutas, fusión por nombre, estadísticas), colocar en carpeta,
  mover/deshacer movido, persistencia del árbol y compatibilidad con sesiones antiguas, intake de carpetas.
- UI: compilación + prueba manual en la app real (vista previa de cada tipo, arrastrar entre niveles).
