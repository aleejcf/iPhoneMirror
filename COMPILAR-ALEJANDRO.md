# Compilar la edición Alejandro

La entrega corresponde a `1.8.5-pre3.alejandro.1` y conserva los cambios
de la copia compartida. El núcleo C++ se conserva como binario suministrado;
la interfaz WPF se publica desde estas fuentes, sin depender de .NET instalado.

Extrae el ZIP de fuentes en una **carpeta nueva**, sin reutilizar `bin` u `obj`
del respaldo: sus fechas futuras pueden hacer que MSBuild reutilice resultados
antiguos. Usa un SDK .NET 10 compatible con `global.json` e Inno Setup 6.7.3.

Descarga el ZIP oficial `iPhoneMirror-v1.8.5-pre3-win-x64.zip` y comprueba
su SHA256 contra `SHA256SUMS.txt` de la misma versión antes de extraerlo:
`bd8642a0d755246e6703c6313525d79b520ea1216b761b537b8574e26cce99dc`.
Los componentes auxiliares provienen de ese ZIP; no sustituyen la interfaz
ni el núcleo personalizados.

```powershell
./scripts/package_alejandro.ps1 `
  -DotnetPath 'C:\ruta\dotnet.exe' `
  -BasePackageDirectory 'C:\ruta\iPhoneMirror-v1.8.5-pre3-win-x64' `
  -NativeCorePath "$PWD\prebuilt-alejandro\iPhoneMirror.Core.dll" `
  -InnoCompiler 'C:\ruta\ISCC.exe' `
  -OutputDirectory 'C:\ruta\entrega-nueva'
```

El instalador se registra por usuario como iPhoneMirror Alejandro, con su
propia identidad de desinstalación. Incluye las dependencias de ejecución.
Los ajustes de la aplicación permanecen en `%LOCALAPPDATA%\iPhoneMirror`.

Para recompilar también C++, instala MSVC, Windows SDK y CMake y sigue
`docs/DEVELOPMENT.md`. El núcleo suministrado se identifica por el SHA256
que consta en `installer/LEEME-Alejandro.txt`; el script lo comprueba para
evitar reemplazarlo accidentalmente por el original. Una compilación nativa
nueva requiere actualizar ese valor tras verificarla. No se ha demostrado
una compilación reproducible byte a byte del núcleo suministrado.

Se conservan las licencias GPL y avisos de terceros. El manifiesto SPDX
heredado del ZIP oficial describe componentes de esa distribución original;
no es un SBOM nuevo de esta edición personalizada.
