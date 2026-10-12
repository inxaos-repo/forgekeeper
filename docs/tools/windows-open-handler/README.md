# forgekeeper-open:// (Windows Explorer handler)

Optional. It makes the **Explorer** / **Open in Explorer** buttons on the model page open Windows Explorer
at the model folder, or at the folder with the file selected. Without it, use **Copy path** / **Copy folder** and
paste the path into Explorer (Win+E, then Ctrl+L, then Ctrl+V).

## Install (per user, no admin rights)

```powershell
# from a checkout or a copy of this folder
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
# custom prefix(es), matching Library__WindowsPathPrefix:
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -Prefix '\\samba.k8s.inxaos.com\warehousepool\3d printing','W:\3d printing'
# remove
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
```

The installer copies `forgekeeper-open.ps1` to `%LOCALAPPDATA%\Forgekeeper\` and registers
`HKCU\Software\Classes\forgekeeper-open`. The first time you click a button, Chrome asks to open
"Forgekeeper Open". Tick "Always allow" to skip that prompt afterwards.

## Safety

- It accepts only paths under the allowed prefixes, and it rejects `..`.
- It never runs or opens the file itself. It only starts `explorer.exe` at the folder (`/select` for files).
- A link from any website can only open an Explorer window inside the library.
