# Overlay tests

`build.cmd` also builds `bin\vktest.exe`: a Vulkan program with a window that's never shown, which presents 30 frames
of one colour. Nothing appears on screen.

Run it with the layer, opened, saving the fifth frame drawn (PowerShell):

```powershell
$env:VK_ADD_LAYER_PATH = "$PWD\bin"; $env:VK_INSTANCE_LAYERS = "VK_LAYER_DRANGED_overlay"
$env:DRANGED_OVERLAY_OPEN = "1"; $env:DRANGED_OVERLAY_DUMP = "$PWD\bin\frame.bmp"
.\bin\vktest.exe
```

As the manager loads it (an implicit layer, only with `DRANGED_OVERLAY_ENABLE=1`): set
`VK_ADD_IMPLICIT_LAYER_PATH` to `bin` instead of the two `VK_*` lines above. Without `DRANGED_OVERLAY_ENABLE` the
loader skips it.

The layer logs to `%LOCALAPPDATA%\DrangedAddons\overlay.log`.
