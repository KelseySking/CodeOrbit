# Bundled Plugins

This directory contains plugin definitions for built-in CLI tools that ship with CodeOrbit.

## Structure

Each plugin is defined in a separate JSON file following the plugin schema 2.0 format:

```
bundled-plugins/
├── claude.json          # Claude Code
├── codex.json           # Codex CLI
├── cursor.json          # Cursor
├── gemini.json          # Gemini
└── ...
```

## Bundled vs User Plugins

- **Bundled plugins**: Embedded, loaded first, cannot be overridden
- **User plugins**: Located in `%AppData%/CodeOrbit/sources/`, loaded second

## Priority

Bundled plugins have higher priority than user plugins with the same source key.
This ensures stable behavior for known CLI tools.

## Adding a New Bundled Plugin

1. Create `<source-key>.json` in this directory
2. Follow schema 2.0 format
3. Include `detection` and `hook_installation` sections
4. Test with ConfigInstaller.InstallPlugin()
5. Add to bundled-plugins.csproj as EmbeddedResource (if using embedded model)

## Schema Reference

See `docs/plugin-schema.md` for complete schema documentation.
