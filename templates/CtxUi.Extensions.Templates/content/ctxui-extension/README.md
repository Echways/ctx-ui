# MyExtension

An extension for the ctx-ui panel.

Preview it next to a ctx-ui binary:

```bash
export CTXUI_BINARY=/path/to/ctx-ui
dotnet build -t:CtxUiPreview
```

Build the archive and install it on a server:

```bash
dotnet publish -c Release
sudo ctx-ui extension install bin/Release/net10.0/my-extension-1.0.0.zip
sudo systemctl restart ctx-ui
```

Guide: https://github.com/Echways/ctx-ui/blob/main/docs/en/extensions.md

Licensed under GPL-3.0, see LICENSE. Whoever receives this extension also receives its source code.
